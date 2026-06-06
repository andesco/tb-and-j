namespace Jellyfin.Plugin.TorBoxSync.Models;

public sealed class TorBoxDeletionRecord
{
    public string TorBoxType { get; set; } = string.Empty;

    public string TorBoxItemId { get; set; } = string.Empty;

    public DateTimeOffset RequestedAtUtc { get; set; }

    public DateTimeOffset? SucceededAtUtc { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;

    public string Key => BuildKey(TorBoxType, TorBoxItemId);

    public static string BuildKey(string torBoxType, string torBoxItemId)
        => $"{torBoxType}:{torBoxItemId}";
}
