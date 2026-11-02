using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PhotoClippy.Plugins.External;

/// <summary>JSON-RPC over HTTP: one POST per request, the response body is the JSON-RPC response.</summary>
internal sealed class HttpTransport : IRpcTransport
{
    private static readonly MediaTypeHeaderValue JsonContentType = new("application/json") { CharSet = "utf-8" };

    private readonly ExternalPluginOptions _options;
    private readonly Uri _url;
    private readonly ILogger _logger;
    private readonly ParamsWriter _initializeParams;
    private readonly HttpClient _client;
    private long _nextId;
    private int _disposed;

    public HttpTransport(ExternalPluginOptions options, ILogger logger, ParamsWriter initializeParams)
    {
        _options = options;
        _url = new Uri(options.Url!, UriKind.Absolute);
        _logger = logger;
        _initializeParams = initializeParams;
        _client = new HttpClient(
            new SocketsHttpHandler
            {
                MaxConnectionsPerServer = Math.Max(1, options.MaxConcurrency),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                AutomaticDecompression = DecompressionMethods.All,
            },
            disposeHandler: true)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    public bool PassesFramesByPath => false;

    public bool IsDegraded => false;

    public int RestartCount => 0;

    public int? ProcessId => null;

    public Action<JsonElement>? Reinitialized { get; set; }

    public Task<JsonElement> StartAsync(CancellationToken cancellationToken) =>
        CallAsync("initialize", _initializeParams, TimeSpan.FromSeconds(_options.TimeoutSeconds), cancellationToken);

    public async Task<JsonElement> CallAsync(string method, ParamsWriter? writeParams, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var id = Interlocked.Increment(ref _nextId);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var body = JsonRpc.WriteMessage(id, method, writeParams, appendNewline: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, _url)
        {
            Content = new ReadOnlyMemoryContent(body.WrittenMemory),
        };
        request.Content.Headers.ContentType = JsonContentType;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(timeoutCts.Token).ConfigureAwait(false);
            JsonDocument? document = null;
            try
            {
                if (bytes.Length > 0)
                {
                    try
                    {
                        document = JsonDocument.Parse(bytes);
                    }
                    catch (JsonException) when (!response.IsSuccessStatusCode)
                    {
                    }
                }

                if (document is { RootElement.ValueKind: JsonValueKind.Object } && (response.IsSuccessStatusCode || document.RootElement.TryGetProperty("error"u8, out _)))
                {
                    var result = JsonRpc.ToResult(document.RootElement, _options.Id, out var error);
                    return error is null ? result : throw error;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new ExternalPluginException(_options.Id, ExternalPluginErrorCodes.TransportFailure, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} from {_url}: {JsonRpc.Preview(bytes)}");
                }

                throw new ExternalPluginException(_options.Id, ExternalPluginErrorCodes.InvalidResponse, $"'{method}' returned a non-object response: {JsonRpc.Preview(bytes)}");
            }
            finally
            {
                document?.Dispose();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExternalPluginException(_options.Id, ExternalPluginErrorCodes.Timeout, $"'{method}' timed out after {timeout.TotalSeconds:0.###} s");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "HTTP request to external plugin {PluginId} failed", _options.Id);
            throw new ExternalPluginException(_options.Id, ExternalPluginErrorCodes.TransportFailure, $"HTTP request to {_url} failed: {ex.Message}", innerException: ex);
        }
        catch (JsonException ex)
        {
            throw new ExternalPluginException(_options.Id, ExternalPluginErrorCodes.InvalidResponse, $"'{method}' returned malformed JSON: {ex.Message}", innerException: ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _client.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}
