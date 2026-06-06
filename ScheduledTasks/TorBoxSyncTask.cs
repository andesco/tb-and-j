using Jellyfin.Plugin.TorBoxSync.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.TorBoxSync.ScheduledTasks;

public sealed class TorBoxSyncTask : IScheduledTask
{
    private readonly TorBoxSyncManager _syncManager;

    public TorBoxSyncTask(TorBoxSyncManager syncManager)
    {
        _syncManager = syncManager;
    }

    public string Name => "Sync TorBox STRM library";

    public string Key => "TorBoxSync";

    public string Description => "Fetches TorBox media, writes managed STRM files, tombstones missing/deleted files, and optionally deletes fully tombstoned TorBox downloads.";

    public string Category => "TorBox Sync";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => await _syncManager.RunSyncAsync(progress, cancellationToken).ConfigureAwait(false);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var minutes = Math.Max(5, Plugin.Instance.Configuration.SyncIntervalMinutes);
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(minutes).Ticks
            }
        ];
    }
}
