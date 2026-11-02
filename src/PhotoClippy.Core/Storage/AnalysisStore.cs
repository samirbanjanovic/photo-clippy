using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PhotoClippy.Contracts;

namespace PhotoClippy.Core.Storage;

/// <summary>A cached analyzer output for one file, valid while size/mtime and the plugin cache key match.</summary>
public sealed record FeatureRow(string Path, string PluginId, string CacheKey, long Size, long MTime, string Json);

/// <summary>SQLite cache (WAL): per-file metadata, per-plugin frame features and run snapshots.</summary>
public sealed class AnalysisStore : IDisposable
{
    public const string FramePluginId = "frame";
    private const int SchemaVersion = 1;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private AnalysisStore(string connectionString, string path)
    {
        _connectionString = connectionString;
        DatabasePath = path;
    }

    public string DatabasePath { get; }

    public static AnalysisStore Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 60,
        };
        var store = new AnalysisStore(builder.ToString(), path);
        store.Initialize();
        return store;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        var version = Scalar<long>(connection, "PRAGMA user_version;");
        if (version != SchemaVersion)
        {
            Execute(connection, """
                DROP TABLE IF EXISTS metadata;
                DROP TABLE IF EXISTS features;
                DROP TABLE IF EXISTS snapshots;
                """);
        }

        Execute(connection, $"""
            CREATE TABLE IF NOT EXISTS metadata(
                path TEXT PRIMARY KEY, size INTEGER NOT NULL, mtime INTEGER NOT NULL,
                extractor TEXT NOT NULL, json TEXT NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS features(
                path TEXT NOT NULL, plugin TEXT NOT NULL, key TEXT NOT NULL, size INTEGER NOT NULL,
                mtime INTEGER NOT NULL, json TEXT NOT NULL, PRIMARY KEY(path, plugin)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS snapshots(
                id INTEGER PRIMARY KEY AUTOINCREMENT, created TEXT NOT NULL, roots TEXT NOT NULL, data BLOB NOT NULL);
            PRAGMA user_version = {SchemaVersion};
            """);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-32000;");
        return connection;
    }

    /// <summary>Streams cached metadata for files whose size/mtime/extractor key still match.</summary>
    public IEnumerable<(SourceFile File, PhotoMetadata Metadata)> ReadMetadata(IReadOnlyDictionary<string, SourceFile> files, string extractorKey, CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, size, mtime, extractor, json FROM metadata";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = reader.GetString(0);
            if (!files.TryGetValue(path, out var file) || reader.GetInt64(1) != file.Size || reader.GetInt64(2) != Ticks(file) ||
                !string.Equals(reader.GetString(3), extractorKey, StringComparison.Ordinal))
            {
                continue;
            }

            PhotoMetadata? metadata;
            try
            {
                metadata = JsonSerializer.Deserialize<PhotoMetadata>(reader.GetString(4), ContractJson.Options);
            }
            catch (JsonException)
            {
                continue;
            }

            if (metadata is not null)
            {
                yield return (file, metadata with { Path = file.Path });
            }
        }
    }

    public async Task WriteMetadataAsync(IReadOnlyList<(SourceFile File, PhotoMetadata Metadata)> rows, string extractorKey, CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO metadata(path, size, mtime, extractor, json) VALUES ($p, $s, $m, $e, $j)";
            var p = command.Parameters.Add("$p", SqliteType.Text);
            var s = command.Parameters.Add("$s", SqliteType.Integer);
            var m = command.Parameters.Add("$m", SqliteType.Integer);
            var e = command.Parameters.Add("$e", SqliteType.Text);
            var j = command.Parameters.Add("$j", SqliteType.Text);
            command.Prepare();
            foreach (var (file, metadata) in rows)
            {
                p.Value = file.Path;
                s.Value = file.Size;
                m.Value = Ticks(file);
                e.Value = extractorKey;
                j.Value = JsonSerializer.Serialize(metadata, ContractJson.Options);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Streams all feature rows ordered by path (rows of one path are adjacent).</summary>
    public IEnumerable<FeatureRow> ReadFeatures(CancellationToken cancellationToken = default)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, plugin, key, size, mtime, json FROM features ORDER BY path";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new FeatureRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetString(5));
        }
    }

    public async Task WriteFeaturesAsync(IReadOnlyList<FeatureRow> rows, CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO features(path, plugin, key, size, mtime, json) VALUES ($p, $g, $k, $s, $m, $j)";
            var p = command.Parameters.Add("$p", SqliteType.Text);
            var g = command.Parameters.Add("$g", SqliteType.Text);
            var k = command.Parameters.Add("$k", SqliteType.Text);
            var s = command.Parameters.Add("$s", SqliteType.Integer);
            var m = command.Parameters.Add("$m", SqliteType.Integer);
            var j = command.Parameters.Add("$j", SqliteType.Text);
            command.Prepare();
            foreach (var row in rows)
            {
                p.Value = row.Path;
                g.Value = row.PluginId;
                k.Value = row.CacheKey;
                s.Value = row.Size;
                m.Value = row.MTime;
                j.Value = row.Json;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<long> SaveSnapshotAsync(AnalysisSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        byte[] data;
        using (var buffer = new MemoryStream())
        {
            await using (var brotli = new BrotliStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                await JsonSerializer.SerializeAsync(brotli, snapshot, ContractJson.Options, cancellationToken).ConfigureAwait(false);
            }

            data = buffer.ToArray();
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO snapshots(created, roots, data) VALUES ($c, $r, $d);
                DELETE FROM snapshots WHERE id NOT IN (SELECT id FROM snapshots ORDER BY id DESC LIMIT 10);
                SELECT max(id) FROM snapshots;
                """;
            command.Parameters.AddWithValue("$c", snapshot.Run.FinishedUtc.ToString("O"));
            command.Parameters.AddWithValue("$r", string.Join('\n', snapshot.Profile.Library.Roots));
            command.Parameters.AddWithValue("$d", data);
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<AnalysisSnapshot?> LoadLatestSnapshotAsync(CancellationToken cancellationToken = default)
    {
        byte[]? data;
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT data FROM snapshots ORDER BY id DESC LIMIT 1";
            data = command.ExecuteScalar() as byte[];
        }

        if (data is null)
        {
            return null;
        }

        await using var brotli = new BrotliStream(new MemoryStream(data), CompressionMode.Decompress);
        return await JsonSerializer.DeserializeAsync<AnalysisSnapshot>(brotli, ContractJson.Options, cancellationToken).ConfigureAwait(false);
    }

    public StoreStats GetStats()
    {
        using var connection = OpenConnection();
        return new StoreStats(
            Scalar<long>(connection, "SELECT count(*) FROM metadata"),
            Scalar<long>(connection, "SELECT count(*) FROM features"),
            Scalar<long>(connection, "SELECT count(*) FROM snapshots"),
            File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0);
    }

    public void Clear()
    {
        using var connection = OpenConnection();
        Execute(connection, "DELETE FROM metadata; DELETE FROM features; DELETE FROM snapshots;");
        Execute(connection, "VACUUM;");
    }

    public static long Ticks(SourceFile file) => file.LastWriteUtc.UtcTicks;

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        SqliteConnection.ClearAllPools();
    }
}

public sealed record StoreStats(long MetadataRows, long FeatureRows, long Snapshots, long SizeBytes);
