namespace Jellyfin.Plugin.TorBoxSync.Models;

public sealed class ManagedFileRecord
{
    public string TorBoxType { get; set; } = string.Empty;

    public string TorBoxItemId { get; set; } = string.Empty;

    public string TorBoxFileId { get; set; } = string.Empty;

    public string TorBoxItemName { get; set; } = string.Empty;

    public string TorBoxFileName { get; set; } = string.Empty;

    public string TorBoxPath { get; set; } = string.Empty;

    public string MediaKind { get; set; } = string.Empty;

    public string ShowName { get; set; } = string.Empty;

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public string RelativeStrmPath { get; set; } = string.Empty;

    public string StrmPath { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenUtc { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }

    public DateTimeOffset? TombstonedAtUtc { get; set; }

    public string TombstoneReason { get; set; } = string.Empty;

    public DateTimeOffset? DeletedFromTorBoxAtUtc { get; set; }

    public int ConsecutiveRemoteMisses { get; set; }

    public string Key => BuildKey(TorBoxType, TorBoxItemId, TorBoxFileId);

    public bool IsTombstoned => TombstonedAtUtc.HasValue;

    public static string BuildKey(string torBoxType, string torBoxItemId, string torBoxFileId)
        => $"{torBoxType}:{torBoxItemId}:{torBoxFileId}";
}
