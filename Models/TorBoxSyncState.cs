namespace Jellyfin.Plugin.TorBoxSync.Models;

public sealed class TorBoxSyncState
{
    public int SchemaVersion { get; set; } = 1;

    public DateTimeOffset? LastSyncStartedUtc { get; set; }

    public DateTimeOffset? LastSyncCompletedUtc { get; set; }

    public bool LegacyNfoCleanupCompleted { get; set; }

    public List<ManagedFileRecord> ManagedFiles { get; set; } = [];

    public List<TorBoxDeletionRecord> TorBoxDeletions { get; set; } = [];
}
