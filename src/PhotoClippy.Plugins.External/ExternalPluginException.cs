using System.Text.Json;

namespace PhotoClippy.Plugins.External;

/// <summary>
/// JSON-RPC error codes. Codes from −32768 to −32000 follow JSON-RPC 2.0; the −32001…−32009 range is used by
/// the host for transport-level failures (<see cref="ExternalPluginException.IsRemote"/> is false for those).
/// </summary>
public static class ExternalPluginErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    public const int Timeout = -32001;
    public const int ProcessExited = -32002;
    /// <summary>The plugin is degraded (restart budget exhausted) or disposed; requests fail fast.</summary>
    public const int Unavailable = -32003;
    public const int InvalidResponse = -32004;
    public const int TransportFailure = -32005;
    public const int StartFailed = -32006;
    public const int ManifestRejected = -32007;
}

/// <summary>A failed call to an external plugin.</summary>
public sealed class ExternalPluginException : Exception
{
    public ExternalPluginException(string pluginId, int code, string message, bool isRemote = false, JsonElement? errorData = null, Exception? innerException = null)
        : base($"[{pluginId}] {message} (code {code})", innerException)
    {
        PluginId = pluginId;
        Code = code;
        RemoteMessage = message;
        IsRemote = isRemote;
        ErrorData = errorData;
    }

    public string PluginId { get; }

    public int Code { get; }

    /// <summary>The error message without the plugin id / code decoration.</summary>
    public string RemoteMessage { get; }

    /// <summary>True when the plugin answered with a JSON-RPC error object; false for transport failures.</summary>
    public bool IsRemote { get; }

    public JsonElement? ErrorData { get; }
}
