namespace PhotoClippy.Plugins.Lenses;

/// <summary>Settings for <c>builtin.lenses.lensfun</c> (config section <c>plugins.settings["builtin.lenses.lensfun"]</c>).</summary>
public sealed class LensfunSettings
{
    /// <summary>GitHub contents API listing the database files. Used to discover new files on refresh.</summary>
    public string IndexUrl { get; set; } = "https://api.github.com/repos/lensfun/lensfun/contents/data/db";

    /// <summary>Base URL the XML files are downloaded from.</summary>
    public string BaseUrl { get; set; } = "https://raw.githubusercontent.com/lensfun/lensfun/master/data/db/";

    /// <summary>
    /// Folder of Lensfun XML files to load instead of downloading (offline installs, or a local
    /// checkout of <c>lensfun/data/db</c>). When set, refresh never touches the network.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>Download timeout per request, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 60;
}
