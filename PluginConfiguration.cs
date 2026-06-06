using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TorBoxSync;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string TorBoxApiKey { get; set; } = string.Empty;

    public string LibraryRootPath { get; set; } = "/var/lib/jellyfin/data/torbox-sync/library";

    public bool AutoCreateJellyfinLibraries { get; set; } = true;

    public string MovieLibraryName { get; set; } = "TorBox Movies";

    public string SeriesLibraryName { get; set; } = "TorBox Series";

    public bool SyncTorrents { get; set; } = true;

    public bool SyncUsenet { get; set; } = true;

    public bool SyncWebDownloads { get; set; } = true;

    public bool DeleteTorBoxItemWhenAllManagedFilesTombstoned { get; set; }

    public bool RemoveUnmanagedStrmFiles { get; set; } = true;

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
