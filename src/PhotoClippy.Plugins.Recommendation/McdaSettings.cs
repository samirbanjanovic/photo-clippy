namespace PhotoClippy.Plugins.Recommendation;

/// <summary>Settings of <c>builtin.recommend.mcda</c> (config section <c>plugins:builtin.recommend.mcda</c>).</summary>
public sealed record McdaSettings
{
    /// <summary>Log-focal standard deviation of a prime's coverage kernel (0.15 ≈ ±19 % framing at half coverage).</summary>
    public double PrimeSigma { get; init; } = 0.15;

    /// <summary>Score multiplier is 1 − RedundancyPenalty × redundancy with an owned lens.</summary>
    public double RedundancyPenalty { get; init; } = 0.8;

    /// <summary>Maximum number of spec archetypes proposed when no candidates are supplied.</summary>
    public int MaxSuggestions { get; init; } = 6;

    internal McdaSettings Normalized() => new()
    {
        PrimeSigma = double.IsFinite(PrimeSigma) ? Math.Clamp(PrimeSigma, 0.02, 1.0) : 0.15,
        RedundancyPenalty = double.IsFinite(RedundancyPenalty) ? Math.Clamp(RedundancyPenalty, 0.0, 1.0) : 0.8,
        MaxSuggestions = Math.Clamp(MaxSuggestions, 1, 20),
    };
}
