using System.Threading.Channels;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorBoxSync.Services;

public sealed class LibraryDeleteHostedService : BackgroundService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    private readonly ILibraryManager _libraryManager;
    private readonly TorBoxSyncManager _syncManager;
    private readonly ILogger<LibraryDeleteHostedService> _logger;
    private readonly Channel<string> _paths = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait
    });

    public LibraryDeleteHostedService(
        ILibraryManager libraryManager,
        TorBoxSyncManager syncManager,
        ILogger<LibraryDeleteHostedService> logger)
    {
        _libraryManager = libraryManager;
        _syncManager = syncManager;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved += OnItemRemoved;
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _paths.Writer.TryComplete();
        return base.StopAsync(cancellationToken);
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        var path = e.Item.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!_paths.Writer.TryWrite(path))
        {
            _logger.LogWarning("Jellyfin deletion queue is full; deferring tombstone detection for {Path} to the next sync.", path);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var firstPath in _paths.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            var batch = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { firstPath };
            await Task.Delay(DebounceDelay, stoppingToken).ConfigureAwait(false);
            while (_paths.Reader.TryRead(out var path))
            {
                batch.Add(path);
            }

            try
            {
                await _syncManager.TombstonePathsAsync(batch, "jellyfin-delete", stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to tombstone a batch of {Count} deleted Jellyfin items", batch.Count);
            }
        }
    }
}
