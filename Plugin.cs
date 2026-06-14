using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TorBoxSync;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginName = "TB&J";

    public static readonly Guid PluginId = Guid.Parse("a2f4e2bb-7dcb-49ea-8bbf-f8b9d77f1fb5");

    public static Plugin Instance { get; private set; } = null!;

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => PluginName;

    public override Guid Id => PluginId;

    public override string Description => "Synchronizes TorBox media into a Jellyfin-managed STRM library.";

    public IEnumerable<PluginPageInfo> GetPages()
        =>
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        ];
}
