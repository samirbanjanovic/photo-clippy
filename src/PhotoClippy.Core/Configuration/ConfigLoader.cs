using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PhotoClippy.Core.Configuration;

/// <summary>
/// Loads <see cref="AppOptions"/> from a JSON file (comments allowed) and applies environment overrides:
/// <c>PHOTO_CLIPPY_&lt;SECTION&gt;__&lt;KEY&gt;[__&lt;KEY&gt;…]=value</c> (numeric segments index arrays; values that
/// parse as JSON are used as JSON). <c>PHOTO_CLIPPY_HOME</c> and <c>PHOTO_CLIPPY_MODELS</c> map to paths.
/// </summary>
public static class ConfigLoader
{
    public const string EnvPrefix = "PHOTO_CLIPPY_";
    public const string DefaultFileName = "photo-clippy.json";

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new LenientStringListConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Accepts a JSON array or a single string with ';'-separated items (handy for environment variables).</summary>
    private sealed class LenientStringListConverter : JsonConverter<List<string>>
    {
        public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return [];
                case JsonTokenType.String:
                    return [.. (reader.GetString() ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                case JsonTokenType.StartArray:
                {
                    var list = new List<string>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: > 0 } item)
                        {
                            list.Add(item);
                        }
                        else if (reader.TokenType is JsonTokenType.Number)
                        {
                            list.Add(System.Text.Encoding.UTF8.GetString(reader.ValueSpan));
                        }
                    }

                    return list;
                }

                default:
                    throw new JsonException("Expected a string or an array of strings.");
            }
        }

        public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value)
            {
                writer.WriteStringValue(item);
            }

            writer.WriteEndArray();
        }
    }

    /// <summary>
    /// Explicit path, then PHOTO_CLIPPY_CONFIG, ./photo-clippy.json, &lt;home&gt;/photo-clippy.json, where home is
    /// <paramref name="homeOverride"/> (the --home flag) when given, else the default home.
    /// </summary>
    public static string? FindConfigFile(string? explicitPath, IDictionary? environment = null, string? homeOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        environment ??= Environment.GetEnvironmentVariables();
        if (environment[EnvPrefix + "CONFIG"] is string fromEnv && fromEnv.Length > 0)
        {
            return Path.GetFullPath(fromEnv);
        }

        var local = Path.GetFullPath(DefaultFileName);
        if (File.Exists(local))
        {
            return local;
        }

        var home = Path.Combine(homeOverride is null ? AppPaths.DefaultHome(environment) : Path.GetFullPath(homeOverride), DefaultFileName);
        return File.Exists(home) ? home : null;
    }

    public static AppOptions Load(string? configFile, IDictionary? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariables();
        JsonObject root;
        if (configFile is not null)
        {
            if (!File.Exists(configFile))
            {
                throw new FileNotFoundException($"Config file not found: {configFile}", configFile);
            }

            var node = JsonNode.Parse(File.ReadAllText(configFile), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            root = node as JsonObject ?? throw new InvalidDataException($"{configFile}: the root must be a JSON object.");
        }
        else
        {
            root = [];
        }

        ApplyEnvironment(root, environment);
        return root.Deserialize<AppOptions>(JsonOptions) ?? new AppOptions();
    }

    public static string Serialize(AppOptions options) => JsonSerializer.Serialize(options, JsonOptions);

    /// <summary>Writes <paramref name="options"/> atomically (temp file + rename), keeping the previous file as <c>.bak</c>.</summary>
    public static void Save(AppOptions options, string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        File.WriteAllText(temp, Serialize(options) + Environment.NewLine);
        if (File.Exists(full))
        {
            File.Copy(full, full + ".bak", overwrite: true);
        }

        File.Move(temp, full, overwrite: true);
    }

    internal static void ApplyEnvironment(JsonObject root, IDictionary environment)
    {
        var entries = new List<(string Key, string Value)>();
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is string key && entry.Value is string value && key.StartsWith(EnvPrefix, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add((key[EnvPrefix.Length..], value));
            }
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        foreach (var (key, value) in entries)
        {
            switch (key.ToUpperInvariant())
            {
                case "HOME":
                    Set(root, ["paths", "home"], value);
                    continue;
                case "MODELS":
                    Set(root, ["paths", "models"], value);
                    continue;
            }

            if (!key.Contains("__", StringComparison.Ordinal))
            {
                continue;
            }

            var segments = key.Split("__", StringSplitOptions.RemoveEmptyEntries);
            Set(root, segments, value);
        }
    }

    private static void Set(JsonObject root, IReadOnlyList<string> segments, string rawValue)
    {
        JsonNode current = root;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            var last = i == segments.Count - 1;
            var nextIsIndex = !last && int.TryParse(segments[i + 1], out _);
            switch (current)
            {
                case JsonObject obj:
                {
                    var name = FindProperty(obj, segment) ?? segment.ToLowerInvariant();
                    if (last)
                    {
                        obj[name] = ParseValue(rawValue);
                        return;
                    }

                    if (obj[name] is not (JsonObject or JsonArray))
                    {
                        obj[name] = nextIsIndex ? new JsonArray() : new JsonObject();
                    }

                    current = obj[name]!;
                    break;
                }

                case JsonArray array when int.TryParse(segment, out var index) && index >= 0:
                {
                    while (array.Count <= index)
                    {
                        array.Add(null);
                    }

                    if (last)
                    {
                        array[index] = ParseValue(rawValue);
                        return;
                    }

                    if (array[index] is not (JsonObject or JsonArray))
                    {
                        array[index] = nextIsIndex ? new JsonArray() : new JsonObject();
                    }

                    current = array[index]!;
                    break;
                }

                default:
                    return;
            }
        }
    }

    private static string? FindProperty(JsonObject obj, string name)
    {
        foreach (var property in obj)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Key;
            }
        }

        return null;
    }

    private static JsonNode? ParseValue(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length > 0 && (trimmed[0] is '[' or '{' or '"' || trimmed is "true" or "false" or "null" || double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)))
        {
            try
            {
                return JsonNode.Parse(trimmed);
            }
            catch (JsonException)
            {
            }
        }

        return JsonValue.Create(raw);
    }
}
