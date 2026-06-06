using System.Text.Json;
using Jellyfin.Plugin.TorBoxSync.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorBoxSync.Services;

public sealed class TorBoxStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ILogger<TorBoxStateStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public TorBoxStateStore(ILogger<TorBoxStateStore> logger)
    {
        _logger = logger;
    }

    public string StatePath => Path.Combine(Plugin.Instance.DataFolderPath, "state.json");

    public async Task<TorBoxSyncState> LoadAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Plugin.Instance.DataFolderPath);
            if (!File.Exists(StatePath))
            {
                return new TorBoxSyncState();
            }

            await using var stream = File.OpenRead(StatePath);
            return await JsonSerializer.DeserializeAsync<TorBoxSyncState>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? new TorBoxSyncState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load TorBox Sync state from {StatePath}", StatePath);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(TorBoxSyncState state, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Plugin.Instance.DataFolderPath);
            var tempPath = StatePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, StatePath, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save TorBox Sync state to {StatePath}", StatePath);
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }
}
