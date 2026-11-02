using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PhotoClippy.Plugins.External;

/// <summary>
/// Owns the child process lifecycle: spawns a <see cref="StdioSession"/>, restarts it with exponential backoff
/// when it dies, and degrades (fail fast) once <see cref="ExternalPluginOptions.MaxRestarts"/> is exhausted.
/// </summary>
internal sealed class StdioTransport : IRpcTransport
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(15);

    private readonly ExternalPluginOptions _options;
    private readonly string _stateDirectory;
    private readonly ILogger _logger;
    private readonly ParamsWriter _initializeParams;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private StdioSession? _current;
    private Task<StdioSession>? _session;
    private long _nextId;
    private int _restarts;
    private bool _degraded;
    private bool _disposed;

    public StdioTransport(ExternalPluginOptions options, string stateDirectory, ILogger logger, ParamsWriter initializeParams)
    {
        _options = options;
        _stateDirectory = stateDirectory;
        _logger = logger;
        _initializeParams = initializeParams;
    }

    public bool PassesFramesByPath => true;

    public bool IsDegraded
    {
        get
        {
            lock (_gate)
            {
                return _degraded;
            }
        }
    }

    public int RestartCount => Volatile.Read(ref _restarts);

    public int? ProcessId
    {
        get
        {
            lock (_gate)
            {
                return _current is { IsClosed: false } session ? session.ProcessId : null;
            }
        }
    }

    public Action<JsonElement>? Reinitialized { get; set; }

    private TimeSpan Timeout => TimeSpan.FromSeconds(_options.TimeoutSeconds);

    public async Task<JsonElement> StartAsync(CancellationToken cancellationToken)
    {
        var (session, result) = await SpawnAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (session.IsClosed)
            {
                _ = session.DisposeAsync().AsTask();
                throw new ExternalPluginException(_options.Id, ExternalPluginErrorCodes.ProcessExited, "plugin process exited right after initialize");
            }

            _current = session;
            _session = Task.FromResult(session);
        }

        return result;
    }

    public async Task<JsonElement> CallAsync(string method, ParamsWriter? writeParams, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task<StdioSession> pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_degraded || _session is null)
            {
                throw Unavailable();
            }

            pending = _session;
        }

        StdioSession session;
        try
        {
            session = await pending.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new ExternalPluginException(_options.Id, ExternalPluginErrorCodes.Timeout, $"'{method}' timed out after {timeout.TotalSeconds:0.#} s waiting for the plugin to restart");
        }

        return await session.CallAsync(Interlocked.Increment(ref _nextId), method, writeParams, timeout, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        StdioSession? current;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            current = _current;
            _current = null;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (current is not null)
        {
            await current.StopAsync(_options.ShutdownGrace).ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }

    private async Task<(StdioSession Session, JsonElement Result)> SpawnAsync(CancellationToken cancellationToken)
    {
        var session = StdioSession.Start(_options, _stateDirectory, _logger, OnSessionExited);
        try
        {
            var result = await session.CallAsync(Interlocked.Increment(ref _nextId), "initialize", _initializeParams, Timeout, cancellationToken).ConfigureAwait(false);
            return (session, result);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void OnSessionExited(StdioSession session, int? exitCode)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_current, session))
            {
                return;
            }

            _current = null;
            if (_restarts >= _options.MaxRestarts)
            {
                Degrade(exitCode);
                return;
            }

            _restarts++;
            var restart = RestartAsync(_restarts);
            restart.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _session = restart;
        }

        _ = session.DisposeAsync().AsTask();
    }

    private async Task<StdioSession> RestartAsync(int attempt)
    {
        while (true)
        {
            var delay = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, _options.RestartBackoff.Ticks * (1L << Math.Min(attempt - 1, 16))));
            _logger.LogWarning("Restarting external plugin {PluginId} in {Delay} ms (attempt {Attempt}/{Max})", _options.Id, (int)delay.TotalMilliseconds, attempt, _options.MaxRestarts);
            try
            {
                await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
                var (session, result) = await SpawnAsync(_lifetime.Token).ConfigureAwait(false);
                bool closed;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        _ = session.DisposeAsync().AsTask();
                        throw new ObjectDisposedException(nameof(StdioTransport));
                    }

                    closed = session.IsClosed;
                    if (!closed)
                    {
                        _current = session;
                    }
                }

                if (closed)
                {
                    _ = session.DisposeAsync().AsTask();
                    throw new ExternalPluginException(_options.Id, ExternalPluginErrorCodes.ProcessExited, "plugin process exited right after initialize");
                }

                _logger.LogInformation("External plugin {PluginId} restarted (pid {Pid})", _options.Id, session.ProcessId);
                Reinitialized?.Invoke(result);
                return session;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
            {
                _logger.LogWarning(ex, "Restart of external plugin {PluginId} failed", _options.Id);
                lock (_gate)
                {
                    if (_disposed)
                    {
                        throw;
                    }

                    if (_restarts >= _options.MaxRestarts)
                    {
                        _degraded = true;
                        _logger.LogError("External plugin {PluginId} is degraded: restart budget ({Max}) exhausted", _options.Id, _options.MaxRestarts);
                        throw Unavailable(ex);
                    }

                    attempt = ++_restarts;
                }
            }
        }
    }

    private void Degrade(int? exitCode)
    {
        _degraded = true;
        var failed = Task.FromException<StdioSession>(Unavailable());
        _ = failed.Exception;
        _session = failed;
        _logger.LogError(
            "External plugin {PluginId} exited (code {ExitCode}) and the restart budget ({Max}) is exhausted; it is now degraded",
            _options.Id,
            exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?",
            _options.MaxRestarts);
    }

    private ExternalPluginException Unavailable(Exception? inner = null) =>
        new(_options.Id, ExternalPluginErrorCodes.Unavailable, $"plugin is unavailable (degraded after {_options.MaxRestarts} restarts)", innerException: inner);
}
