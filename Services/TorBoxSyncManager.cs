using System.Security.Cryptography;
using System.Text;
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

            if (string.IsNullOrWhiteSpace(configuration.JellyfinPublicBaseUrl))
            {
                _logger.LogError("TorBox Sync skipped because JellyfinPublicBaseUrl is not configured.");
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
            var observedRemoteItemKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unavailableRemoteItemKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var snapshots = await Task.WhenAll(enabledTypes.Select(type =>
                _client.GetManagedVideoFilesAsync(type, configuration, cancellationToken))).ConfigureAwait(false);
            foreach (var snapshot in snapshots)
            {
                candidates.AddRange(snapshot.Candidates);
                observedRemoteItemKeys.UnionWith(snapshot.ObservedItemKeys);
                unavailableRemoteItemKeys.UnionWith(snapshot.UnavailableItemKeys);
            }

            progress?.Report(45);
            var obsoleteManagedPaths = UpsertCandidates(state, candidates, configuration, now);
            var filesystemChanged = TombstoneRemoteMissingFiles(
                state,
                candidates,
                unavailableRemoteItemKeys,
                configuration,
                now,
                enabledTypes);
            progress?.Report(70);

            filesystemChanged |= WriteDesiredStrmFiles(state, configuration);
            filesystemChanged |= DeleteObsoleteManagedPaths(state, obsoleteManagedPaths, configuration);
            if (filesystemChanged || !state.LegacyNfoCleanupCompleted)
            {
                filesystemChanged |= CleanupUnmanagedStrmFiles(state, configuration);
            }

            progress?.Report(85);

            await DeleteFullyTombstonedTorBoxItemsAsync(state, configuration, observedRemoteItemKeys, cancellationToken).ConfigureAwait(false);
            PruneTerminalState(state, configuration, now);
            state.LastSyncCompletedUtc = DateTimeOffset.UtcNow;
            await _stateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            progress?.Report(100);

            _logger.LogInformation(
                "TorBox Sync completed: {ManagedCount} managed files, {TombstonedCount} tombstoned files",
                state.ManagedFiles.Count,
                state.ManagedFiles.Count(i => i.IsTombstoned));

            if (filesystemChanged)
            {
                await _libraryManager.ValidateMediaLibrary(new Progress<double>(), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task TombstonePathAsync(string path, string reason, CancellationToken cancellationToken)
        => await TombstonePathsAsync([path], reason, cancellationToken).ConfigureAwait(false);

    public async Task TombstonePathsAsync(
        IReadOnlyCollection<string> paths,
        string reason,
        CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance.Configuration;
        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(StrmPathBuilder.NormalizePath)
            .Where(path => StrmPathBuilder.IsUnderRoot(path, configuration.LibraryRootPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPaths.Length == 0)
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
                if (normalizedPaths.Any(path =>
                        string.Equals(recordPath, path, StringComparison.OrdinalIgnoreCase)
                        || recordPath.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
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

            var earliestYear = mainFiles
                .SelectMany(c => new[] { c.FileName, c.Path, c.ItemName })
                .Select(StrmPathBuilder.ExtractFirstYear)
                .Where(y => y.HasValue)
                .Select(y => y!.Value)
                .DefaultIfEmpty()
                .Min();

            result[group.Key] = earliestYear == 0 ? null : earliestYear;
        }
        return result;
    }

    private void PruneTerminalState(TorBoxSyncState state, PluginConfiguration configuration, DateTimeOffset now)
    {
        var retentionDays = Math.Max(1, configuration.StateRetentionDays);
        var cutoff = now.AddDays(-retentionDays);
        var removedManagedFiles = state.ManagedFiles.RemoveAll(record =>
            record.IsTombstoned
            && record.DeletedFromTorBoxAtUtc.HasValue
            && record.DeletedFromTorBoxAtUtc.Value < cutoff);
        var activeDeletionKeys = state.ManagedFiles
            .Select(record => TorBoxDeletionRecord.BuildKey(record.TorBoxType, record.TorBoxItemId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedDeletions = state.TorBoxDeletions.RemoveAll(deletion =>
            deletion.SucceededAtUtc.HasValue
            && deletion.SucceededAtUtc.Value < cutoff
            && !activeDeletionKeys.Contains(deletion.Key));

        if (removedManagedFiles > 0 || removedDeletions > 0)
        {
            _logger.LogInformation(
                "Pruned {ManagedCount} terminal managed-file records and {DeletionCount} completed deletion records older than {RetentionDays} days",
                removedManagedFiles,
                removedDeletions,
                retentionDays);
        }
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

    private IReadOnlyList<string> UpsertCandidates(
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

        var newRecords = candidates
            .Select(candidate =>
            {
                var itemKey = $"{candidate.TorBoxType}:{candidate.ItemId}";
                earliestYearByItemKey.TryGetValue(itemKey, out var fallbackYear);
                return StrmPathBuilder.ToManagedRecord(candidate, configuration.LibraryRootPath, now, fallbackYear);
            })
            .ToList();
        var newRecordKeys = newRecords.Select(record => record.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var occupiedPaths = state.ManagedFiles
            .Where(record => !record.IsTombstoned && !newRecordKeys.Contains(record.Key))
            .Select(record => StrmPathBuilder.NormalizePath(record.StrmPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        EnsureUniqueStrmPaths(newRecords, occupiedPaths, configuration.LibraryRootPath);

        var obsoleteManagedPaths = new List<string>();
        var existingByKey = state.ManagedFiles.ToDictionary(i => i.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var newRecord in newRecords)
        {
            if (existingByKey.TryGetValue(newRecord.Key, out var existing))
            {
                if (!string.Equals(
                        StrmPathBuilder.NormalizePath(existing.StrmPath),
                        StrmPathBuilder.NormalizePath(newRecord.StrmPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    obsoleteManagedPaths.Add(existing.StrmPath);
                }

                existing.TorBoxItemName = newRecord.TorBoxItemName;
                existing.TorBoxFileName = newRecord.TorBoxFileName;
                existing.TorBoxPath = newRecord.TorBoxPath;
                existing.MediaKind = newRecord.MediaKind;
                existing.ShowName = newRecord.ShowName;
                existing.SeasonNumber = newRecord.SeasonNumber;
                existing.EpisodeNumber = newRecord.EpisodeNumber;
                existing.RelativeStrmPath = newRecord.RelativeStrmPath;
                existing.StrmPath = newRecord.StrmPath;
                existing.LastSeenUtc = now;
                existing.ConsecutiveRemoteMisses = 0;
                if (string.Equals(existing.TombstoneReason, "remote-missing", StringComparison.OrdinalIgnoreCase))
                {
                    existing.TombstonedAtUtc = null;
                    existing.TombstoneReason = string.Empty;
                    existing.DeletedFromTorBoxAtUtc = null;
                }

                continue;
            }

            state.ManagedFiles.Add(newRecord);
        }

        return obsoleteManagedPaths;
    }

    internal static void EnsureUniqueStrmPaths(
        IReadOnlyList<ManagedFileRecord> records,
        IReadOnlySet<string> occupiedPaths,
        string libraryRootPath)
    {
        var pathsToDisambiguate = records
            .GroupBy(record => StrmPathBuilder.NormalizePath(record.StrmPath), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1 || occupiedPaths.Contains(group.Key))
            .SelectMany(group => group)
            .ToList();
        foreach (var record in pathsToDisambiguate)
        {
            var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.Key)))[..10].ToLowerInvariant();
            var directory = Path.GetDirectoryName(record.RelativeStrmPath)!;
            var fileName = Path.GetFileNameWithoutExtension(record.RelativeStrmPath);
            record.RelativeStrmPath = Path.Combine(directory, $"{fileName} [tb-{suffix}].strm");
            record.StrmPath = Path.GetFullPath(Path.Combine(libraryRootPath, record.RelativeStrmPath));
        }
    }

    private bool DeleteObsoleteManagedPaths(
        TorBoxSyncState state,
        IReadOnlyList<string> obsoleteManagedPaths,
        PluginConfiguration configuration)
    {
        if (obsoleteManagedPaths.Count == 0)
        {
            return false;
        }

        var changed = false;
        var desiredPaths = state.ManagedFiles
            .Where(record => !record.IsTombstoned)
            .Select(record => StrmPathBuilder.NormalizePath(record.StrmPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var obsoletePath in obsoleteManagedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!desiredPaths.Contains(StrmPathBuilder.NormalizePath(obsoletePath)))
            {
                changed |= TryDeleteFileAndPrune(obsoletePath, configuration);
            }
        }

        return changed;
    }

    private bool WriteDesiredStrmFiles(TorBoxSyncState state, PluginConfiguration configuration)
    {
        var baseUrl = configuration.JellyfinPublicBaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("JellyfinPublicBaseUrl is required to write secure STRM files.");
        }

        // Auto-generate a play secret on first use so the endpoint is not guessable.
        if (string.IsNullOrWhiteSpace(configuration.PlaySecret))
        {
            configuration.PlaySecret = Guid.NewGuid().ToString("N");
            Plugin.Instance.SaveConfiguration();
            _logger.LogInformation("TB&J: generated play secret for STRM URLs.");
        }

        var secret = configuration.PlaySecret;
        var changed = false;

        foreach (var record in state.ManagedFiles.Where(i => !i.IsTombstoned))
        {
            if (!StrmPathBuilder.IsUnderRoot(record.StrmPath, configuration.LibraryRootPath))
            {
                _logger.LogWarning("Skipping STRM outside configured root: {Path}", record.StrmPath);
                continue;
            }

            var strmContent = BuildJellyfinPlayUrl(baseUrl, secret, record);
            var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(strmContent)));

            Directory.CreateDirectory(Path.GetDirectoryName(record.StrmPath)!);

            if (File.Exists(record.StrmPath)
                && (string.Equals(record.StrmContentHash, contentHash, StringComparison.Ordinal)
                    || File.ReadAllText(record.StrmPath) == strmContent))
            {
                record.StrmContentHash = contentHash;
                continue;
            }

            File.WriteAllText(record.StrmPath, strmContent);
            record.StrmContentHash = contentHash;
            changed = true;
        }

        return changed;
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
        return $"{jellyfinBase}/torboxsync/play/{record.TorBoxType}/{record.TorBoxItemId}/{record.TorBoxFileId}/{name}?s={secret}";
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

    private bool CleanupUnmanagedStrmFiles(TorBoxSyncState state, PluginConfiguration configuration)
    {
        if (!configuration.RemoveUnmanagedStrmFiles || !Directory.Exists(configuration.LibraryRootPath))
        {
            return false;
        }

        var changed = false;
        var desired = state.ManagedFiles
            .Where(i => !i.IsTombstoned)
            .Select(i => StrmPathBuilder.NormalizePath(i.StrmPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var strmPath in Directory.EnumerateFiles(configuration.LibraryRootPath, "*.strm", SearchOption.AllDirectories))
        {
            var normalizedPath = StrmPathBuilder.NormalizePath(strmPath);
            if (!desired.Contains(normalizedPath))
            {
                changed |= TryDeleteFileAndPrune(strmPath, configuration);
            }
        }

        if (!state.LegacyNfoCleanupCompleted)
        {
            foreach (var nfoPath in Directory.EnumerateFiles(configuration.LibraryRootPath, "*.nfo", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(nfoPath);
                    changed = true;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Unable to delete legacy NFO {Path}", nfoPath);
                }
            }

            state.LegacyNfoCleanupCompleted = true;
        }

        return changed;
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

    private bool TombstoneRemoteMissingFiles(
        TorBoxSyncState state,
        IReadOnlyList<TorBoxFileCandidate> candidates,
        IReadOnlySet<string> unavailableRemoteItemKeys,
        PluginConfiguration configuration,
        DateTimeOffset now,
        IReadOnlyCollection<string> enabledTypes)
    {
        var enabledTypeSet = enabledTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currentRemoteKeys = candidates
            .Select(i => ManagedFileRecord.BuildKey(i.TorBoxType, i.ItemId, i.FileId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var graceSyncs = Math.Max(1, configuration.RemoteMissingGraceSyncs);
        var changed = false;
        foreach (var record in state.ManagedFiles.Where(i => !i.IsTombstoned && enabledTypeSet.Contains(i.TorBoxType)))
        {
            if (currentRemoteKeys.Contains(record.Key))
            {
                record.ConsecutiveRemoteMisses = 0;
                continue;
            }

            var itemKey = TorBoxDeletionRecord.BuildKey(record.TorBoxType, record.TorBoxItemId);
            if (unavailableRemoteItemKeys.Contains(itemKey))
            {
                record.ConsecutiveRemoteMisses = 0;
                continue;
            }

            record.ConsecutiveRemoteMisses++;
            if (record.ConsecutiveRemoteMisses < graceSyncs)
            {
                _logger.LogDebug(
                    "Deferring remote-missing tombstone for {Type}:{ItemId}:{FileId}; miss {MissCount} of {GraceSyncs}",
                    record.TorBoxType,
                    record.TorBoxItemId,
                    record.TorBoxFileId,
                    record.ConsecutiveRemoteMisses,
                    graceSyncs);
                continue;
            }

            record.TombstonedAtUtc = now;
            record.TombstoneReason = "remote-missing";
            record.DeletedFromTorBoxAtUtc = now;
            changed |= TryDeleteFileAndPrune(record.StrmPath, configuration);
            _logger.LogInformation(
                "Tombstoned remote-missing TorBox file {Type}:{ItemId}:{FileId}",
                record.TorBoxType,
                record.TorBoxItemId,
                record.TorBoxFileId);
        }

        return changed;
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

    private bool TryDeleteFileAndPrune(string path, PluginConfiguration configuration)
    {
        var deleted = false;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                deleted = true;
            }

            PruneEmptyParentDirectories(path, configuration.LibraryRootPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to delete STRM or prune empty folders for {Path}", path);
            // The next sync pass will retry cleanup.
        }

        return deleted;
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
