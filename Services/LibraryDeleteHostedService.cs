using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorBoxSync.Services;

public sealed class LibraryDeleteHostedService : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly TorBoxSyncManager _syncManager;
    private readonly ILogger<LibraryDeleteHostedService> _logger;

    public LibraryDeleteHostedService(
        ILibraryManager libraryManager,
        TorBoxSyncManager syncManager,
        ILogger<LibraryDeleteHostedService> logger)
    {
        _libraryManager = libraryManager;
        _syncManager = syncManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved += OnItemRemoved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved -= OnItemRemoved;
        return Task.CompletedTask;
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        var path = e.Item.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _syncManager.TombstonePathAsync(path, "jellyfin-delete", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to tombstone deleted Jellyfin item {Path}", path);
            }
        });
    }
}
