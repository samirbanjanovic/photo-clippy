namespace PhotoClippy.Plugins.Composition;

/// <summary>Settings for <c>builtin.composition.heuristic</c> (config section <c>plugins:builtin.composition.heuristic</c>).</summary>
public sealed record HeuristicCompositionSettings
{
    /// <summary>Long edge (px) of the internal analysis raster; clamped to 32..1024. Larger frames are area-averaged down.</summary>
    public int WorkingEdge { get; init; } = 256;

    /// <summary>Emit low-confidence genre priors derived from capture facts and pixel features.</summary>
    public bool EmitGenrePriors { get; init; } = true;
}
