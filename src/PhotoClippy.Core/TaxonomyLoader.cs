using System.Text.Json;
using PhotoClippy.Contracts;

namespace PhotoClippy.Core;

public static class TaxonomyLoader
{
    public const string RelativePath = "taxonomy/genres.json";

    public static GenreTaxonomy Load(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, RelativePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Genre taxonomy not found at {path}. Set paths.data or PHOTO_CLIPPY_PATHS__DATA.", path);
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<GenreTaxonomy>(stream, ContractJson.Options)
            ?? throw new InvalidDataException($"Genre taxonomy at {path} is empty.");
    }
}
