using Jellyfin.Plugin.TorBoxSync.Models;
using Jellyfin.Plugin.TorBoxSync.Services;

namespace Jellyfin.Plugin.TorBoxSync.Tests;

public sealed class TorBoxSyncManagerTests
{
    [Fact]
    public void PluginAssembly_ContainsConfigurationPage()
    {
        Assert.Contains(
            "Jellyfin.Plugin.TorBoxSync.Configuration.configPage.html",
            typeof(Plugin).Assembly.GetManifestResourceNames());
    }

    [Fact]
    public void EnsureUniqueStrmPaths_DisambiguatesCollidingRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), "torbox-sync-tests");
        var first = CreateRecord("1", "1", root);
        var second = CreateRecord("2", "2", root);

        TorBoxSyncManager.EnsureUniqueStrmPaths([first, second], new HashSet<string>(), root);

        Assert.NotEqual(first.StrmPath, second.StrmPath);
        Assert.Contains("[tb-", first.StrmPath);
        Assert.Contains("[tb-", second.StrmPath);
    }

    [Fact]
    public void EnsureUniqueStrmPaths_DisambiguatesAnOccupiedPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "torbox-sync-tests");
        var record = CreateRecord("1", "1", root);
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { record.StrmPath };

        TorBoxSyncManager.EnsureUniqueStrmPaths([record], occupied, root);

        Assert.Contains("[tb-", record.StrmPath);
        Assert.DoesNotContain(record.StrmPath, occupied);
    }

    private static ManagedFileRecord CreateRecord(string itemId, string fileId, string root)
        => StrmPathBuilder.ToManagedRecord(
            new TorBoxFileCandidate
            {
                TorBoxType = "torrents",
                ItemId = itemId,
                FileId = fileId,
                ItemName = "Same Movie 2024",
                FileName = "Same.Movie.2024.mkv",
                Path = "Same Movie 2024/Same.Movie.2024.mkv",
                MimeType = "video/x-matroska"
            },
            root,
            DateTimeOffset.UtcNow);
}
