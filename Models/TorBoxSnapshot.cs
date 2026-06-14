namespace Jellyfin.Plugin.TorBoxSync.Models;

public sealed class TorBoxSnapshot
{
    public string TorBoxType { get; init; } = string.Empty;

    public IReadOnlyList<TorBoxFileCandidate> Candidates { get; init; } = [];

    public IReadOnlySet<string> ObservedItemKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> UnavailableItemKeys { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
