using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TorBoxSync;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string TorBoxApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Public base URL of this Jellyfin server (no trailing slash), e.g. https://jelly.andrewe.dev
    /// STRM files are written with this as the host so Infuse can authenticate using its
    /// existing Jellyfin credentials. The plugin controller then redirects to TorBox.
    /// </summary>
    public string JellyfinPublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Random secret included in every play URL so the endpoint is not guessable.
    /// Auto-generated on first sync if empty. To invalidate all existing STRM links,
    /// clear this value — the next sync will generate a new secret and rewrite all STRMs.
    /// </summary>
    public string PlaySecret { get; set; } = string.Empty;

    public string LibraryRootPath { get; set; } = "/var/lib/jellyfin/data/torbox-sync/library";

    public bool AutoCreateJellyfinLibraries { get; set; } = true;

    public string MovieLibraryName { get; set; } = "TorBox Movies";

    public string SeriesLibraryName { get; set; } = "TorBox Series";

    public bool SyncTorrents { get; set; } = true;

    public bool SyncUsenet { get; set; } = true;

    public bool SyncWebDownloads { get; set; } = true;

    public bool DeleteTorBoxItemWhenAllManagedFilesTombstoned { get; set; }

    public bool RemoveUnmanagedStrmFiles { get; set; } = true;

    public int RemoteMissingGraceSyncs { get; set; } = 2;

    public int SyncIntervalMinutes { get; set; } = 15;

    public string[] AllowedVideoExtensions { get; set; } =
    [
        ".mkv",
        ".mp4",
        ".avi",
        ".mov",
        ".m4v",
        ".wmv",
        ".webm",
        ".ts",
        ".m2ts"
    ];
}
