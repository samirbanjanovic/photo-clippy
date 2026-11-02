using System.Text.RegularExpressions;
using PhotoClippy.Contracts;

namespace PhotoClippy.Plugins.Clip;

internal enum ClipModelRole
{
    Vision,
    Text,
    Vocab,
    Merges,
}

internal sealed record ClipModelFile(ClipModelRole Role, string RepoPath, string RelativePath, long? SizeBytes, string? Sha256);

internal static partial class ClipModelCatalog
{
    public const string License = "MIT";

    private static readonly Dictionary<string, (long Size, string Sha256)> Pinned = new(StringComparer.Ordinal)
    {
        ["Xenova/clip-vit-base-patch32/onnx/vision_model.onnx"] = (351_685_709, "fd6e1402a588279d1723c7534d4bcba5bc0b14b47dfab0e46f8c47b8270d7d40"),
        ["Xenova/clip-vit-base-patch32/onnx/vision_model_quantized.onnx"] = (89_117_001, "583fd1110a514667812fee7d684952aaf82a99b959760c8d7dca7e0ab9839299"),
        ["Xenova/clip-vit-base-patch32/onnx/text_model.onnx"] = (254_058_553, "3f6571f5bad13a97c469c1622e1cfc4d9aef78b79fdbfcff804ca357bfada8cc"),
        ["Xenova/clip-vit-base-patch32/onnx/text_model_quantized.onnx"] = (64_504_507, "73baab855d406190da9faa498cfedf65f15cf309f4cc7385b7b032e6d08e5c3a"),
        ["Xenova/clip-vit-base-patch32/vocab.json"] = (862_328, "5047b556ce86ccaf6aa22b3ffccfc52d391ea4accdab9c2f2407da5b742d4363"),
        ["Xenova/clip-vit-base-patch32/merges.txt"] = (524_619, "9fd691f7c8039210e0fced15865466c65820d09b63988b0174bfe25de299051a"),
    };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]*/[A-Za-z0-9][A-Za-z0-9_.\-]*$")]
    private static partial Regex ModelIdRegex();

    public static bool IsValidModelId(string? model) =>
        !string.IsNullOrEmpty(model) && !model.Contains("..", StringComparison.Ordinal) && ModelIdRegex().IsMatch(model);

    public static string DirectoryName(string model) => model[(model.IndexOf('/', StringComparison.Ordinal) + 1)..];

    public static IReadOnlyList<ClipModelFile> Files(string model, bool quantizedVision, bool quantizedText) =>
    [
        Create(model, ClipModelRole.Vision, quantizedVision ? "onnx/vision_model_quantized.onnx" : "onnx/vision_model.onnx"),
        Create(model, ClipModelRole.Text, quantizedText ? "onnx/text_model_quantized.onnx" : "onnx/text_model.onnx"),
        Create(model, ClipModelRole.Vocab, "vocab.json"),
        Create(model, ClipModelRole.Merges, "merges.txt"),
    ];

    public static ModelAsset ToAsset(string model, ClipModelFile file) => new()
    {
        RelativePath = file.RelativePath,
        Source = new Uri($"https://huggingface.co/{model}/resolve/main/{file.RepoPath}"),
        SizeBytes = file.SizeBytes,
        Sha256 = file.Sha256,
        License = License,
    };

    private static ClipModelFile Create(string model, ClipModelRole role, string repoPath)
    {
        var relative = DirectoryName(model) + "/" + Path.GetFileName(repoPath);
        return Pinned.TryGetValue(model + "/" + repoPath, out var pin)
            ? new ClipModelFile(role, repoPath, relative, pin.Size, pin.Sha256)
            : new ClipModelFile(role, repoPath, relative, null, null);
    }
}
