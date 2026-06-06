using Jellyfin.Plugin.TorBoxSync.ScheduledTasks;
using Jellyfin.Plugin.TorBoxSync.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.TorBoxSync;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TorBoxStateStore>();
        serviceCollection.AddSingleton<TorBoxClient>();
        serviceCollection.AddSingleton<JellyfinLibraryProvisioner>();
        serviceCollection.AddSingleton<TorBoxSyncManager>();
        serviceCollection.AddSingleton<IScheduledTask, TorBoxSyncTask>();
        serviceCollection.AddHostedService<LibraryDeleteHostedService>();
    }
}
