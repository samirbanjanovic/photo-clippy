namespace PhotoClippy.Plugins.Clip;

/// <summary>Settings for <c>builtin.composition.clip</c> (config section <c>plugins:builtin.composition.clip</c>).</summary>
public sealed record ClipSettings
{
    /// <summary>Hugging Face repository with ONNX exports (Xenova/transformers.js layout).</summary>
    public string Model { get; init; } = "Xenova/clip-vit-base-patch32";

    /// <summary>
    /// Use the int8 dynamically quantized vision model. Default on: on an M1 Pro CPU it is ~2× faster than fp32
    /// (≈62 vs ≈30 img/s with 4 callers × 2 intra-op threads) and a 4× smaller download, with the same top-1
    /// genre on the reference images; fp32 gives somewhat sharper, closer-to-reference probabilities.
    /// </summary>
    public bool Quantized { get; init; } = true;

    /// <summary>
    /// Use the int8 text model. Off by default: text embeddings are computed once and cached, and the
    /// quantized text model noticeably degrades zero-shot accuracy.
    /// </summary>
    public bool QuantizedText { get; init; }

    /// <summary>ONNX Runtime intra-op threads per inference (0 = ONNX Runtime default).</summary>
    public int IntraOpThreads { get; init; } = 2;

    /// <summary>Softmax temperature applied to cosine similarities (CLIP's learned scale is 100).</summary>
    public double LogitScale { get; init; } = 100;
}
