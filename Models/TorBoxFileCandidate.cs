namespace Jellyfin.Plugin.TorBoxSync.Models;

public sealed class TorBoxFileCandidate
{
    public string TorBoxType { get; init; } = string.Empty;

    public string ItemId { get; init; } = string.Empty;

    public string FileId { get; init; } = string.Empty;

    public string ItemName { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;

    public string DownloadLink { get; init; } = string.Empty;
}
