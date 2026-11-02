using System.Text.Json;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.External;

internal delegate void ParamsWriter(Utf8JsonWriter writer);

/// <summary>JSON-RPC 2.0 framing helpers shared by the transports.</summary>
internal static class JsonRpc
{
    public const int MaxMessageBytes = 256 * 1024 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    public static ParamsWriter Params<T>(T value) => writer => JsonSerializer.Serialize(writer, value, ContractJson.Options);

    /// <summary>Serializes a request (or a notification when <paramref name="id"/> is null) into a pooled buffer.</summary>
    public static PooledBufferWriter WriteMessage(long? id, string method, ParamsWriter? writeParams, bool appendNewline)
    {
        var buffer = new PooledBufferWriter();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc"u8, "2.0"u8);
                if (id is { } value)
                {
                    writer.WriteNumber("id"u8, value);
                }

                writer.WriteString("method"u8, method);
                if (writeParams is not null)
                {
                    writer.WritePropertyName("params"u8);
                    writeParams(writer);
                }

                writer.WriteEndObject();
            }

            if (appendNewline)
            {
                buffer.Write((byte)'\n');
            }

            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    public static bool TryGetId(JsonElement element, out long id)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetInt64(out id);
            case JsonValueKind.String:
                return long.TryParse(element.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out id);
            default:
                id = 0;
                return false;
        }
    }

    /// <summary>Converts a parsed response object to its result, or the matching exception.</summary>
    public static JsonElement ToResult(JsonElement response, string pluginId, out ExternalPluginException? error)
    {
        error = null;
        if (response.TryGetProperty("error"u8, out var err) && err.ValueKind == JsonValueKind.Object)
        {
            var code = err.TryGetProperty("code"u8, out var c) && c.TryGetInt32(out var parsed) ? parsed : ExternalPluginErrorCodes.InternalError;
            var message = err.TryGetProperty("message"u8, out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? string.Empty : "plugin returned an error";
            JsonElement? data = err.TryGetProperty("data"u8, out var d) ? d.Clone() : null;
            error = new ExternalPluginException(pluginId, code, message, isRemote: true, errorData: data);
            return default;
        }

        if (response.TryGetProperty("result"u8, out var result))
        {
            return result.Clone();
        }

        error = new ExternalPluginException(pluginId, ExternalPluginErrorCodes.InvalidResponse, "response has neither 'result' nor 'error'");
        return default;
    }

    public static string Preview(ReadOnlySpan<byte> utf8, int max = 200)
    {
        var text = System.Text.Encoding.UTF8.GetString(utf8[..Math.Min(utf8.Length, max * 4)]);
        return text.Length > max ? string.Concat(text.AsSpan(0, max), "…") : text;
    }
}
