using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Auth;

/// <summary>Settings for <c>builtin.auth.local</c> (<c>plugins.settings["builtin.auth.local"]</c>).</summary>
public sealed record LocalAccountsSettings
{
    public int MinPasswordLength { get; init; } = 8;

    /// <summary>PBKDF2-SHA256 iterations for new hashes; weaker hashes are upgraded at the next sign-in.</summary>
    public int Iterations { get; init; } = 600_000;

    /// <summary>Accounts file. Default: &lt;state&gt;/builtin.auth.local/users.json.</summary>
    public string? File { get; init; }
}

/// <summary>
/// Accounts stored in a JSON file in the data folder. The file is re-read when it changes on disk, so the
/// <c>users</c> command can edit accounts while the server runs.
/// </summary>
public sealed partial class LocalAccountsProvider : ICredentialAuthProvider, IUserDirectory, IDisposable
{
    public const string PluginId = "builtin.auth.local";
    public const string PluginVersion = "1.0.0";
    private const int MaxPasswordLength = 1024;

    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly LocalAccountsSettings _settings;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lazy<string> _dummyHash;
    private List<StoredUser> _users = [];
    private (DateTime Modified, long Length)? _loadedVersion;

    public LocalAccountsProvider(string stateDirectory, LocalAccountsSettings? settings = null, TimeProvider? time = null)
    {
        _settings = settings ?? new LocalAccountsSettings();
        if (_settings.Iterations < 1_000 || _settings.MinPasswordLength < 1)
        {
            throw new ArgumentException("iterations must be at least 1000 and minPasswordLength at least 1.", nameof(settings));
        }

        _time = time ?? TimeProvider.System;
        FilePath = Path.GetFullPath(_settings.File ?? Path.Combine(stateDirectory, "users.json"));
        _dummyHash = new Lazy<string>(() => PasswordHasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)), _settings.Iterations));
        Manifest = new PluginManifest
        {
            Id = PluginId,
            Name = "Local accounts",
            Version = PluginVersion,
            Kind = ComponentKind.Auth,
            Description = "User names and passwords stored (PBKDF2-hashed) in the data folder; managed in the web UI or with `photo-clippy users`.",
            Capabilities = ["auth.credentials", "auth.directory"],
        };
    }

    public PluginManifest Manifest { get; }

    public string FilePath { get; }

    public int MinPasswordLength => _settings.MinPasswordLength;

    public void Dispose() => _gate.Dispose();

    public async ValueTask<PluginHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var users = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
            return PluginHealth.Ok(users.Count == 0 ? "no accounts yet" : $"{users.Count} account(s) in {FilePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return PluginHealth.Unhealthy($"cannot read {FilePath}: {ex.Message}");
        }
    }

    public async ValueTask<AuthResult> AuthenticateAsync(CredentialRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var users = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var user = Find(users, request.Username);
        if (user is null || request.Password.Length > MaxPasswordLength)
        {
            // Same cost as a real check so response time does not reveal which accounts exist.
            PasswordHasher.Verify(request.Password.Length > MaxPasswordLength ? string.Empty : request.Password, _dummyHash.Value, _settings.Iterations, out _);
            return user is null ? AuthResult.NoResult : AuthResult.Fail();
        }

        if (!PasswordHasher.Verify(request.Password, user.PasswordHash, _settings.Iterations, out var rehash))
        {
            return AuthResult.Fail();
        }

        var newHash = rehash ? PasswordHasher.Hash(request.Password, _settings.Iterations) : null;
        var updated = await MutateAsync(list =>
        {
            var current = list.FirstOrDefault(u => u.Id == user.Id);
            if (current is null)
            {
                return null;
            }

            current.LastSignInUtc = _time.GetUtcNow();
            if (newHash is not null && current.PasswordHash == user.PasswordHash)
            {
                current.PasswordHash = newHash;
            }

            return current.Clone();
        }, cancellationToken).ConfigureAwait(false);

        return updated is null ? AuthResult.Fail() : AuthResult.Success(ToAuthenticated(updated));
    }

    public async ValueTask<AuthenticatedUser?> RefreshAsync(AuthenticatedUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var users = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var current = users.FirstOrDefault(u => u.Id == user.Subject);
        return current is null ? null : ToAuthenticated(current);
    }

    public async ValueTask<IReadOnlyList<DirectoryUser>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        var users = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        return [.. users.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).Select(ToDirectory)];
    }

    public async ValueTask<DirectoryUser> CreateUserAsync(NewDirectoryUser user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        var name = user.Name.Trim();
        if (!NamePattern().IsMatch(name))
        {
            throw new UserDirectoryException("User names are 1–64 characters: letters, digits, '.', '_', '-' or '@', starting with a letter or digit.");
        }

        ValidatePassword(name, user.Password);
        var hash = PasswordHasher.Hash(user.Password, _settings.Iterations);
        var created = await MutateAsync(list =>
        {
            if (Find(list, name) is not null)
            {
                throw new UserDirectoryException($"An account named '{name}' already exists.");
            }

            var roles = NormalizeRoles(user.Roles);
            if (list.Count == 0 && !roles.Contains(AuthRoles.Admin))
            {
                // The first account must be able to manage the rest.
                roles = [AuthRoles.Admin, .. roles];
            }

            var stored = new StoredUser
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                DisplayName = string.IsNullOrWhiteSpace(user.DisplayName) ? null : user.DisplayName.Trim(),
                Roles = roles,
                PasswordHash = hash,
                Stamp = NewStamp(),
                CreatedUtc = _time.GetUtcNow(),
            };
            list.Add(stored);
            return stored.Clone();
        }, cancellationToken).ConfigureAwait(false);

        return ToDirectory(created!);
    }

    public async ValueTask SetPasswordAsync(string name, string newPassword, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(newPassword);
        ValidatePassword(name, newPassword);
        var hash = PasswordHasher.Hash(newPassword, _settings.Iterations);
        await MutateAsync(list =>
        {
            var user = Find(list, name) ?? throw new UserDirectoryException($"No account named '{name}'.");
            user.PasswordHash = hash;
            user.Stamp = NewStamp();
            return user;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetRolesAsync(string name, IReadOnlyList<string> roles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        var normalized = NormalizeRoles(roles);
        await MutateAsync(list =>
        {
            var user = Find(list, name) ?? throw new UserDirectoryException($"No account named '{name}'.");
            if (user.Roles.Contains(AuthRoles.Admin) && !normalized.Contains(AuthRoles.Admin) && AdminCount(list) == 1)
            {
                throw new UserDirectoryException($"'{user.Name}' is the last admin. Make another account an admin first.");
            }

            user.Roles = normalized;
            return user;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> DeleteUserAsync(string name, CancellationToken cancellationToken = default)
    {
        var removed = await MutateAsync(list =>
        {
            var user = Find(list, name);
            if (user is null)
            {
                return null;
            }

            if (user.Roles.Contains(AuthRoles.Admin) && AdminCount(list) == 1)
            {
                throw new UserDirectoryException($"'{user.Name}' is the last admin. Make another account an admin first.");
            }

            list.Remove(user);
            return user;
        }, cancellationToken).ConfigureAwait(false);
        return removed is not null;
    }

    private void ValidatePassword(string name, string password)
    {
        if (password.Length < _settings.MinPasswordLength)
        {
            throw new UserDirectoryException($"Passwords need at least {_settings.MinPasswordLength} characters.");
        }

        if (password.Length > MaxPasswordLength)
        {
            throw new UserDirectoryException($"Passwords can be at most {MaxPasswordLength} characters.");
        }

        if (string.Equals(password.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new UserDirectoryException("The password cannot be the user name.");
        }
    }

    private static List<string> NormalizeRoles(IEnumerable<string> roles) =>
        [.. roles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal)];

    private static int AdminCount(List<StoredUser> users) => users.Count(u => u.Roles.Contains(AuthRoles.Admin));

    private static StoredUser? Find(IEnumerable<StoredUser> users, string name) =>
        users.FirstOrDefault(u => string.Equals(u.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    private static AuthenticatedUser ToAuthenticated(StoredUser user) => new()
    {
        Subject = user.Id,
        Name = user.Name,
        DisplayName = user.DisplayName,
        Roles = user.Roles,
        SessionStamp = user.Stamp,
    };

    private static DirectoryUser ToDirectory(StoredUser user) => new()
    {
        Name = user.Name,
        DisplayName = user.DisplayName,
        Roles = user.Roles,
        CreatedUtc = user.CreatedUtc,
        LastSignInUtc = user.LastSignInUtc,
    };

    private async Task<IReadOnlyList<StoredUser>> SnapshotAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ReloadIfChanged();
            return [.. _users.Select(u => u.Clone())];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reloads, applies <paramref name="change"/> and saves when it returns non-null.</summary>
    private async Task<StoredUser?> MutateAsync(Func<List<StoredUser>, StoredUser?> change, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ReloadIfChanged();
            var working = _users.Select(u => u.Clone()).ToList();
            var result = change(working);
            if (result is not null)
            {
                Save(working);
                _users = working;
            }

            return result?.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ReloadIfChanged()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists)
        {
            _users = [];
            _loadedVersion = null;
            return;
        }

        var version = (info.LastWriteTimeUtc, info.Length);
        if (_loadedVersion == version)
        {
            return;
        }

        using var stream = File.OpenRead(FilePath);
        var file = JsonSerializer.Deserialize<UsersFile>(stream, FileJson) ?? new UsersFile();
        _users = file.Users;
        _loadedVersion = version;
    }

    private void Save(List<StoredUser> users)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = OpenPrivate(temp))
            {
                JsonSerializer.Serialize(stream, new UsersFile { Users = users }, FileJson);
            }

            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }

        var info = new FileInfo(FilePath);
        _loadedVersion = (info.LastWriteTimeUtc, info.Length);
    }

    private static FileStream OpenPrivate(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._@-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    private sealed class UsersFile
    {
        public int Version { get; set; } = 1;
        public List<StoredUser> Users { get; set; } = [];
    }

    private sealed class StoredUser
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public List<string> Roles { get; set; } = [];
        public string PasswordHash { get; set; } = string.Empty;
        public string Stamp { get; set; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset? LastSignInUtc { get; set; }

        public StoredUser Clone()
        {
            var copy = (StoredUser)MemberwiseClone();
            copy.Roles = [.. Roles];
            return copy;
        }
    }
}
