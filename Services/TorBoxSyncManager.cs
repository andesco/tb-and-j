using Jellyfin.Plugin.TorBoxSync.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorBoxSync.Services;

public sealed class TorBoxSyncManager
{
    private readonly TorBoxClient _client;
    private readonly TorBoxStateStore _stateStore;
    private readonly JellyfinLibraryProvisioner _libraryProvisioner;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<TorBoxSyncManager> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public TorBoxSyncManager(
        TorBoxClient client,
        TorBoxStateStore stateStore,
        JellyfinLibraryProvisioner libraryProvisioner,
        ILibraryManager libraryManager,
        ILogger<TorBoxSyncManager> logger)
    {
        _client = client;
        _stateStore = stateStore;
        _libraryProvisioner = libraryProvisioner;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public async Task RunSyncAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = Plugin.Instance.Configuration;
            if (string.IsNullOrWhiteSpace(configuration.TorBoxApiKey))
            {
                _logger.LogWarning("TorBox Sync skipped because TorBoxApiKey is not configured.");
                return;
            }

            await _libraryProvisioner.EnsureLibrariesAsync(configuration, refreshLibrary: false, cancellationToken).ConfigureAwait(false);
            progress?.Report(5);

            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            state.LastSyncStartedUtc = now;
            TombstoneMissingLocalFiles(state, configuration, now);
            progress?.Report(10);

            var enabledTypes = GetEnabledTypes(configuration).ToArray();
            var candidates = new List<TorBoxFileCandidate>();
            foreach (var type in enabledTypes)
            {
                candidates.AddRange(await _client.GetManagedVideoFilesAsync(type, configuration, cancellationToken).ConfigureAwait(false));
            }

            progress?.Report(45);
            UpsertCandidates(state, candidates, configuration, now);
            TombstoneRemoteMissingFiles(state, candidates, configuration, now, enabledTypes);
            progress?.Report(70);

            WriteDesiredStrmFiles(state, configuration);
            CleanupUnmanagedStrmFiles(state, configuration);
            progress?.Report(85);

            var currentRemoteItemKeys = candidates
                .Select(i => TorBoxDeletionRecord.BuildKey(i.TorBoxType, i.ItemId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            await DeleteFullyTombstonedTorBoxItemsAsync(state, configuration, currentRemoteItemKeys, cancellationToken).ConfigureAwait(false);
            state.LastSyncCompletedUtc = DateTimeOffset.UtcNow;
            await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            progress?.Report(100);

            _logger.LogInformation(
                "TorBox Sync completed: {ManagedCount} managed files, {TombstonedCount} tombstoned files",
                state.ManagedFiles.Count,
                state.ManagedFiles.Count(i => i.IsTombstoned));

            await _libraryManager.ValidateMediaLibrary(new Progress<double>(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task TombstonePathAsync(string path, string reason, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance.Configuration;
        var normalizedPath = StrmPathBuilder.NormalizePath(path);
        if (!StrmPathBuilder.IsUnderRoot(normalizedPath, configuration.LibraryRootPath))
        {
            return;
        }

        await _syncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var changed = false;

            foreach (var record in state.ManagedFiles.Where(i => !i.IsTombstoned))
            {
                var recordPath = StrmPathBuilder.NormalizePath(record.StrmPath);
                if (string.Equals(recordPath, normalizedPath, StringComparison.OrdinalIgnoreCase)
                    || recordPath.StartsWith(normalizedPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    record.TombstonedAtUtc = now;
                    record.TombstoneReason = reason;
                    changed = true;
                    TryDeleteFileAndPrune(record.StrmPath, configuration);
                }
            }

            if (!changed)
            {
                return;
            }

            await DeleteFullyTombstonedTorBoxItemsAsync(state, configuration, currentRemoteItemKeys: null, cancellationToken).ConfigureAwait(false);
            await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private static IEnumerable<string> GetEnabledTypes(PluginConfiguration configuration)
    {
        if (configuration.SyncTorrents)
        {
            yield return "torrents";
        }

        if (configuration.SyncUsenet)
        {
            yield return "usenet";
        }

        if (configuration.SyncWebDownloads)
        {
            yield return "webdl";
        }
    }

    // Folder names that indicate non-canonical content — years found inside these
    // subdirectories are pilot/bonus years, not the series premiere year.
    private static readonly HashSet<string> _nonCanonicalFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "extras", "extra", "bonus", "bonus features", "featurettes", "featurette",
        "behind the scenes", "deleted scenes", "trailers", "trailer", "interviews",
        "shorts", "clips", "specials", "special", "season 0", "season 00", "s00",
        "pilot", "pilots",
    };

    private static Dictionary<string, int?> BuildEarliestYearMap(IReadOnlyList<TorBoxFileCandidate> candidates)
    {
        var result = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates.GroupBy(c => $"{c.TorBoxType}:{c.ItemId}"))
        {
            // Mine years only from main episode/movie files, skipping anything inside
            // Pilot/, Extras/, Specials/ etc. subdirectories. Pilots have earlier years
            // than the series premiere, which would produce the wrong folder name.
            var mainFiles = group.Where(c => !IsNonCanonicalPath(c.Path));

            var years = mainFiles
                .SelectMany(c => new[] { c.FileName, c.Path, c.ItemName })
                .Select(StrmPathBuilder.ExtractFirstYear)
                .Where(y => y.HasValue)
                .Select(y => y!.Value)
                .Order()
                .ToList();

            result[group.Key] = years.Count > 0 ? years[0] : null;
        }
        return result;
    }

    private static bool IsNonCanonicalPath(string path)
    {
        var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        // Check every intermediate directory (skip root [0] and filename [^1])
        for (var i = 1; i < parts.Length - 1; i++)
        {
            if (_nonCanonicalFolders.Contains(parts[i].Trim()))
                return true;
        }
        return false;
    }

    private void UpsertCandidates(
        TorBoxSyncState state,
        IReadOnlyList<TorBoxFileCandidate> candidates,
        PluginConfiguration configuration,
        DateTimeOffset now)
    {
        // For each TorBox download item, find the earliest year present in any of its
        // file names or paths. Used as a fallback when the show/movie title itself
        // carries no year (e.g. "The.Planets.S01.BluRay" has no year in the root folder,
        // but individual episode files may be dated).
        var earliestYearByItemKey = BuildEarliestYearMap(candidates);

        var existingByKey = state.ManagedFiles.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var itemKey = $"{candidate.TorBoxType}:{candidate.ItemId}";
            earliestYearByItemKey.TryGetValue(itemKey, out var fallbackYear);
            var newRecord = StrmPathBuilder.ToManagedRecord(candidate, configuration.LibraryRootPath, now, fallbackYear);
            if (existingByKey.TryGetValue(newRecord.Key, out var existing))
            {
                existing.TorBoxItemName = newRecord.TorBoxItemName;
                existing.TorBoxFileName = newRecord.TorBoxFileName;
                existing.TorBoxPath = newRecord.TorBoxPath;
                existing.MediaKind = newRecord.MediaKind;
                existing.ShowName = newRecord.ShowName;
                existing.SeasonNumber = newRecord.SeasonNumber;
                existing.EpisodeNumber = newRecord.EpisodeNumber;
                existing.RelativeStrmPath = newRecord.RelativeStrmPath;
                existing.StrmPath = newRecord.StrmPath;
                existing.DownloadLink = newRecord.DownloadLink;
                existing.LastSeenUtc = now;
                continue;
            }

            state.ManagedFiles.Add(newRecord);
        }
    }

    private void WriteDesiredStrmFiles(TorBoxSyncState state, PluginConfiguration configuration)
    {
        var baseUrl = configuration.JellyfinPublicBaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogWarning(
                "JellyfinPublicBaseUrl is not configured. STRM files will contain the raw TorBox API URL " +
                "instead of an authenticated Jellyfin URL. Set JellyfinPublicBaseUrl in plugin config (e.g. https://jelly.andrewe.dev).");
        }

        // Auto-generate a play secret on first use so the endpoint is not guessable.
        if (!string.IsNullOrWhiteSpace(baseUrl) && string.IsNullOrWhiteSpace(configuration.PlaySecret))
        {
            configuration.PlaySecret = Guid.NewGuid().ToString("N");
            Plugin.Instance.SaveConfiguration();
            _logger.LogInformation("TB&J: generated play secret for STRM URLs.");
        }

        var secret = configuration.PlaySecret;

        foreach (var record in state.ManagedFiles.Where(i => !i.IsTombstoned))
        {
            if (!StrmPathBuilder.IsUnderRoot(record.StrmPath, configuration.LibraryRootPath))
            {
                _logger.LogWarning("Skipping STRM outside configured root: {Path}", record.StrmPath);
                continue;
            }

            var strmContent = string.IsNullOrWhiteSpace(baseUrl)
                ? record.DownloadLink
                : BuildJellyfinPlayUrl(baseUrl, secret, record);

            Directory.CreateDirectory(Path.GetDirectoryName(record.StrmPath)!);

            if (File.Exists(record.StrmPath) && File.ReadAllText(record.StrmPath) == strmContent)
                continue;

            File.WriteAllText(record.StrmPath, strmContent);
        }
    }

    private static string BuildJellyfinPlayUrl(string jellyfinBase, string secret, ManagedFileRecord record)
    {
        // When TorBox stored only an infohash as the item name (magnet links added without
        // a resolved title), fall back to the individual filename which is always descriptive.
        var itemName = record.TorBoxItemName ?? string.Empty;
        var displayName = IsHexHash(itemName)
            ? (record.TorBoxFileName ?? record.TorBoxItemName ?? "media")
            : itemName;

        if (string.IsNullOrWhiteSpace(displayName))
            displayName = "media";

        var name = Uri.EscapeDataString(displayName);
        return $"{jellyfinBase}/torboxsync/play/{secret}/{record.TorBoxType}/{record.TorBoxItemId}/{record.TorBoxFileId}/{name}";
    }

    private static bool IsHexHash(string value)
    {
        if (value.Length < 24) return false;
        foreach (var c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                return false;
        }
        return true;
    }

    private void CleanupUnmanagedStrmFiles(TorBoxSyncState state, PluginConfiguration configuration)
    {
        if (!configuration.RemoveUnmanagedStrmFiles || !Directory.Exists(configuration.LibraryRootPath))
        {
            return;
        }

        var desired = state.ManagedFiles
            .Where(i => !i.IsTombstoned)
            .Select(i => StrmPathBuilder.NormalizePath(i.StrmPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var strmPath in Directory.EnumerateFiles(configuration.LibraryRootPath, "*.strm", SearchOption.AllDirectories))
        {
            var normalizedPath = StrmPathBuilder.NormalizePath(strmPath);
            if (!desired.Contains(normalizedPath))
            {
                TryDeleteFileAndPrune(strmPath, configuration);
            }
        }

        // Remove any .nfo sidecars left over from a previous plugin version.
        foreach (var nfoPath in Directory.EnumerateFiles(configuration.LibraryRootPath, "*.nfo", SearchOption.AllDirectories))
        {
            try { File.Delete(nfoPath); } catch { /* best effort */ }
        }
    }

    private void TombstoneMissingLocalFiles(TorBoxSyncState state, PluginConfiguration configuration, DateTimeOffset now)
    {
        foreach (var record in state.ManagedFiles.Where(i => !i.IsTombstoned))
        {
            if (string.IsNullOrWhiteSpace(record.StrmPath)
                || !StrmPathBuilder.IsUnderRoot(record.StrmPath, configuration.LibraryRootPath)
                || File.Exists(record.StrmPath))
            {
                continue;
            }

            record.TombstonedAtUtc = now;
            record.TombstoneReason = "local-missing";
            PruneEmptyParentDirectories(record.StrmPath, configuration.LibraryRootPath);
            _logger.LogInformation("Tombstoned missing STRM {Path}", record.StrmPath);
        }
    }

    private void TombstoneRemoteMissingFiles(
        TorBoxSyncState state,
        IReadOnlyList<TorBoxFileCandidate> candidates,
        PluginConfiguration configuration,
        DateTimeOffset now,
        IReadOnlyCollection<string> enabledTypes)
    {
        var enabledTypeSet = enabledTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentRemoteKeys = candidates
            .Select(i => ManagedFileRecord.BuildKey(i.TorBoxType, i.ItemId, i.FileId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var record in state.ManagedFiles.Where(i => !i.IsTombstoned && enabledTypeSet.Contains(i.TorBoxType)))
        {
            if (currentRemoteKeys.Contains(record.Key))
            {
                continue;
            }

            record.TombstonedAtUtc = now;
            record.TombstoneReason = "remote-missing";
            record.DeletedFromTorBoxAtUtc = now;
            TryDeleteFileAndPrune(record.StrmPath, configuration);
            _logger.LogInformation(
                "Tombstoned remote-missing TorBox file {Type}:{ItemId}:{FileId}",
                record.TorBoxType,
                record.TorBoxItemId,
                record.TorBoxFileId);
        }
    }

    private async Task DeleteFullyTombstonedTorBoxItemsAsync(
        TorBoxSyncState state,
        PluginConfiguration configuration,
        IReadOnlySet<string>? currentRemoteItemKeys,
        CancellationToken cancellationToken)
    {
        if (!configuration.DeleteTorBoxItemWhenAllManagedFilesTombstoned)
        {
            return;
        }

        var deletionsByKey = state.TorBoxDeletions.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var group in state.ManagedFiles.GroupBy(i => TorBoxDeletionRecord.BuildKey(i.TorBoxType, i.TorBoxItemId)))
        {
            var managed = group.ToList();
            if (managed.Count == 0 || managed.Any(i => !i.IsTombstoned))
            {
                continue;
            }

            var first = managed[0];
            if (currentRemoteItemKeys is not null && !currentRemoteItemKeys.Contains(group.Key))
            {
                _logger.LogDebug(
                    "Skipping TorBox delete for {Type} item {ItemId} because it is no longer returned by TorBox.",
                    first.TorBoxType,
                    first.TorBoxItemId);
                continue;
            }

            if (deletionsByKey.TryGetValue(group.Key, out var existingDeletion) && existingDeletion.SucceededAtUtc.HasValue)
            {
                continue;
            }

            var deletion = existingDeletion ?? new TorBoxDeletionRecord
            {
                TorBoxType = first.TorBoxType,
                TorBoxItemId = first.TorBoxItemId,
                RequestedAtUtc = DateTimeOffset.UtcNow,
                Reason = "all-managed-files-tombstoned"
            };

            try
            {
                await _client.DeleteDownloadAsync(first.TorBoxType, first.TorBoxItemId, configuration, cancellationToken).ConfigureAwait(false);
                deletion.SucceededAtUtc = DateTimeOffset.UtcNow;
                deletion.Error = string.Empty;
                foreach (var record in managed)
                {
                    record.DeletedFromTorBoxAtUtc = deletion.SucceededAtUtc;
                }

                _logger.LogInformation(
                    "Deleted TorBox {Type} item {ItemId} after {Count} managed files were tombstoned",
                    first.TorBoxType,
                    first.TorBoxItemId,
                    managed.Count);
            }
            catch (Exception ex)
            {
                deletion.Error = ex.Message;
                _logger.LogError(ex, "Failed to delete TorBox {Type} item {ItemId}", first.TorBoxType, first.TorBoxItemId);
            }

            if (!deletionsByKey.ContainsKey(group.Key))
            {
                state.TorBoxDeletions.Add(deletion);
                deletionsByKey[group.Key] = deletion;
            }
        }
    }

    private void TryDeleteFileAndPrune(string path, PluginConfiguration configuration)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            PruneEmptyParentDirectories(path, configuration.LibraryRootPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to delete STRM or prune empty folders for {Path}", path);
            // The next sync pass will retry cleanup.
        }
    }

    private static void PruneEmptyParentDirectories(string filePath, string libraryRootPath)
    {
        var rootPath = StrmPathBuilder.NormalizePath(libraryRootPath);
        var movieRootPath = StrmPathBuilder.NormalizePath(Path.Combine(rootPath, "movies"));
        var seriesRootPath = StrmPathBuilder.NormalizePath(Path.Combine(rootPath, "series"));

        var directory = Path.GetDirectoryName(StrmPathBuilder.NormalizePath(filePath));
        while (!string.IsNullOrWhiteSpace(directory))
        {
            var normalizedDirectory = StrmPathBuilder.NormalizePath(directory);
            if (string.Equals(normalizedDirectory, rootPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedDirectory, movieRootPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedDirectory, seriesRootPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!StrmPathBuilder.IsUnderRoot(normalizedDirectory, rootPath) || !Directory.Exists(normalizedDirectory))
            {
                return;
            }

            if (Directory.EnumerateFileSystemEntries(normalizedDirectory).Any())
            {
                return;
            }

            Directory.Delete(normalizedDirectory);
            directory = Path.GetDirectoryName(normalizedDirectory);
        }
    }
}
