namespace PhotoClippy.Plugins.Color;

/// <summary>Settings for <c>builtin.color</c> (config section <c>plugins:builtin.color</c>).</summary>
public sealed record ColorSettings
{
    /// <summary>Long edge (px) of the working image; larger frames are area-averaged down to it. Clamped to 32..4096.</summary>
    public int WorkingEdge { get; init; } = 320;

    /// <summary>Number of k-means clusters before merging/dropping. Clamped to 1..16.</summary>
    public int PaletteSize { get; init; } = 5;

    /// <summary>Upper bound of the stratified pixel subsample used for palette clustering. Clamped to 64..262144.</summary>
    public int MaxPaletteSamples { get; init; } = 4096;

    internal ColorSettings Normalize() => this with
    {
        WorkingEdge = Math.Clamp(WorkingEdge, 32, 4096),
        PaletteSize = Math.Clamp(PaletteSize, 1, 16),
        MaxPaletteSamples = Math.Clamp(MaxPaletteSamples, 64, 262_144),
    };
}
