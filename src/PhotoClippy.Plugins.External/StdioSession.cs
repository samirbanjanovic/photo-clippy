using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PhotoClippy.Plugins.External;

/// <summary>
/// One child process speaking newline-delimited JSON-RPC on stdin/stdout. Requests are multiplexed by id;
/// a dedicated reader loop completes them in whatever order the plugin answers.
/// </summary>
internal sealed class StdioSession : IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly string _pluginId;
    private readonly ILogger _logger;
    private readonly Action<StdioSession, int?> _onExited;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly EventHandler _processExitHandler;
    private readonly Task _readerTask;
    private int _closed;
    private int _disposed;

    private StdioSession(Process process, string pluginId, ILogger logger, Action<StdioSession, int?> onExited)
    {
        _process = process;
        _pluginId = pluginId;
        _logger = logger;
        _onExited = onExited;
        ProcessId = process.Id;
        _stdin = process.StandardInput.BaseStream;
        _processExitHandler = (_, _) => TryKill();
        AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
        _process.ErrorDataReceived += OnStderr;
        _process.BeginErrorReadLine();
        _readerTask = Task.Run(ReadLoopAsync);
    }

    public int ProcessId { get; }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public static StdioSession Start(ExternalPluginOptions options, string stateDirectory, ILogger logger, Action<StdioSession, int?> onExited)
    {
        var workingDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(options.WorkingDirectory) ? Directory.GetCurrentDirectory() : options.WorkingDirectory);
        var command = options.Command!;
        if (!Path.IsPathRooted(command) && (command.Contains('/', StringComparison.Ordinal) || command.Contains('\\', StringComparison.Ordinal)))
        {
            command = Path.GetFullPath(Path.Combine(workingDirectory, command));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        foreach (var arg in options.Args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["PHOTO_CLIPPY_PLUGIN_ID"] = options.Id;
        startInfo.Environment["PHOTO_CLIPPY_TRANSPORT"] = ExternalPluginOptions.StdioTransport;
        startInfo.Environment["PHOTO_CLIPPY_STATE_DIR"] = stateDirectory;
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var (key, value) in options.Environment)
        {
            startInfo.Environment[key] = value;
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Process.Start returned false.");
            }
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new ExternalPluginException(options.Id, ExternalPluginErrorCodes.StartFailed, $"failed to start '{command}': {ex.Message}", innerException: ex);
        }

        logger.LogDebug("Started external plugin {PluginId} (pid {Pid}): {Command}", options.Id, process.Id, command);
        return new StdioSession(process, options.Id, logger, onExited);
    }

    public async Task<JsonElement> CallAsync(long id, string method, ParamsWriter? writeParams, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            if (IsClosed)
            {
                throw Exited();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                var message = JsonRpc.WriteMessage(id, method, writeParams, appendNewline: true);
                var writeError = await WriteAsync(message).WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                if (writeError is not null)
                {
                    throw IsClosed || writeError is IOException or ObjectDisposedException
                        ? Exited(writeError)
                        : new ExternalPluginException(_pluginId, ExternalPluginErrorCodes.TransportFailure, $"failed to write request: {writeError.Message}", innerException: writeError);
                }

                return await completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ExternalPluginException(_pluginId, ExternalPluginErrorCodes.Timeout, $"'{method}' timed out after {timeout.TotalSeconds:0.###} s");
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task NotifyAsync(string method, TimeSpan timeout)
    {
        if (IsClosed)
        {
            return;
        }

        var message = JsonRpc.WriteMessage(null, method, null, appendNewline: true);
        try
        {
            await WriteAsync(message).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    /// <summary>Asks the plugin to exit (shutdown notification, stdin EOF), then kills it after <paramref name="grace"/>.</summary>
    public async ValueTask StopAsync(TimeSpan grace)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        if (!IsClosed && !HasExited())
        {
            await NotifyAsync("shutdown", TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            await CloseStdinAsync().ConfigureAwait(false);
            using var graceCts = new CancellationTokenSource(grace);
            try
            {
                await _process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (!HasExited())
        {
            _logger.LogDebug("Killing external plugin {PluginId} (pid {Pid})", _pluginId, ProcessId);
            TryKill();
        }

        try
        {
            await _readerTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            FailPending(Exited());
        }

        try
        {
            _process.CancelErrorRead();
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }

    public ValueTask DisposeAsync() => StopAsync(TimeSpan.Zero);

    private async Task<Exception?> WriteAsync(PooledBufferWriter message)
    {
        try
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _stdin.WriteAsync(message.WrittenMemory).ConfigureAwait(false);
                await _stdin.FlushAsync().ConfigureAwait(false);
                return null;
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            message.Dispose();
        }
    }

    private async Task CloseStdinAsync()
    {
        var acquired = await _writeLock.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        try
        {
            await _stdin.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        finally
        {
            if (acquired)
            {
                _writeLock.Release();
            }
        }
    }

    private async Task ReadLoopAsync()
    {
        var reader = PipeReader.Create(_process.StandardOutput.BaseStream, new StreamPipeReaderOptions(bufferSize: 64 * 1024, leaveOpen: true));
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync().ConfigureAwait(false);
                var buffer = read.Buffer;
                while (TryReadLine(ref buffer, out var line))
                {
                    Dispatch(line);
                }

                if (buffer.Length > JsonRpc.MaxMessageBytes)
                {
                    _logger.LogError("External plugin {PluginId} sent a line longer than {Max} bytes; terminating it", _pluginId, JsonRpc.MaxMessageBytes);
                    TryKill();
                    break;
                }

                if (read.IsCompleted)
                {
                    if (!buffer.IsEmpty)
                    {
                        Dispatch(buffer);
                    }

                    break;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reader loop of external plugin {PluginId} stopped", _pluginId);
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }

        await HandleExitAsync().ConfigureAwait(false);
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        var position = buffer.PositionOf((byte)'\n');
        if (position is null)
        {
            line = default;
            return false;
        }

        line = buffer.Slice(0, position.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
        return true;
    }

    private void Dispatch(ReadOnlySequence<byte> line)
    {
        if (IsBlank(line))
        {
            return;
        }

        JsonDocument document;
        try
        {
            var jsonReader = new Utf8JsonReader(line, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 });
            document = JsonDocument.ParseValue(ref jsonReader);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("External plugin {PluginId} wrote a malformed line to stdout ({Error}): {Line}", _pluginId, ex.Message, Preview(line));
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("External plugin {PluginId} wrote a non-object JSON line: {Line}", _pluginId, Preview(line));
                return;
            }

            if (root.TryGetProperty("method"u8, out _))
            {
                _logger.LogDebug("Ignoring plugin-initiated message from {PluginId}: {Line}", _pluginId, Preview(line));
                return;
            }

            if (!root.TryGetProperty("id"u8, out var idElement) || !JsonRpc.TryGetId(idElement, out var id))
            {
                if (root.TryGetProperty("error"u8, out var error))
                {
                    _logger.LogWarning("External plugin {PluginId} reported an error without request id: {Error}", _pluginId, error.GetRawText());
                }
                else
                {
                    _logger.LogWarning("External plugin {PluginId} wrote a response without a usable id: {Line}", _pluginId, Preview(line));
                }

                return;
            }

            if (!_pending.TryRemove(id, out var completion))
            {
                _logger.LogDebug("External plugin {PluginId} answered unknown or expired request {Id}", _pluginId, id);
                return;
            }

            var result = JsonRpc.ToResult(root, _pluginId, out var failure);
            if (failure is not null)
            {
                completion.TrySetException(failure);
            }
            else
            {
                completion.TrySetResult(result);
            }
        }
    }

    private async Task HandleExitAsync()
    {
        Volatile.Write(ref _closed, 1);
        int? exitCode = null;
        try
        {
            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(exitCts.Token).ConfigureAwait(false);
            exitCode = _process.ExitCode;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            TryKill();
        }

        if (Volatile.Read(ref _disposed) == 0)
        {
            _logger.LogWarning("External plugin {PluginId} (pid {Pid}) exited with code {ExitCode}", _pluginId, ProcessId, exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?");
        }

        try
        {
            _onExited(this, exitCode);
        }
        finally
        {
            FailPending(Exited(exitCode: exitCode));
        }
    }

    private void FailPending(ExternalPluginException error)
    {
        Volatile.Write(ref _closed, 1);
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var completion))
            {
                completion.TrySetException(error);
            }
        }
    }

    private ExternalPluginException Exited(Exception? inner = null, int? exitCode = null) =>
        new(_pluginId, ExternalPluginErrorCodes.ProcessExited,
            exitCode is null ? "plugin process exited" : $"plugin process exited with code {exitCode}", innerException: inner);

    private void OnStderr(object sender, DataReceivedEventArgs e)
    {
        var line = e.Data;
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _logger.Log(StderrLevel(line), "[{PluginId}] {Line}", _pluginId, line);
    }

    internal static LogLevel StderrLevel(string line)
    {
        var text = line.AsSpan().TrimStart().TrimStart('[');
        if (text.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || text.StartsWith("CRITICAL", StringComparison.OrdinalIgnoreCase) || text.StartsWith("FATAL", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Traceback", StringComparison.Ordinal))
        {
            return LogLevel.Error;
        }

        if (text.StartsWith("WARN", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Warning;
        }

        if (text.StartsWith("DEBUG", StringComparison.OrdinalIgnoreCase) || text.StartsWith("TRACE", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Debug;
        }

        return LogLevel.Information;
    }

    private bool HasExited()
    {
        try
        {
            return _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void TryKill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private static bool IsBlank(ReadOnlySequence<byte> line)
    {
        foreach (var segment in line)
        {
            foreach (var b in segment.Span)
            {
                if (b is not ((byte)' ' or (byte)'\r' or (byte)'\t'))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string Preview(ReadOnlySequence<byte> line)
    {
        var length = (int)Math.Min(line.Length, 800);
        var bytes = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            line.Slice(0, length).CopyTo(bytes);
            return JsonRpc.Preview(bytes.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }
}
