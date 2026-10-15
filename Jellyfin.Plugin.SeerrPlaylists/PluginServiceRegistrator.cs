using Jellyfin.Plugin.SeerrPlaylists.Seerr;
using Jellyfin.Plugin.SeerrPlaylists.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.SeerrPlaylists;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<SeerrClient>();
        serviceCollection.AddSingleton<SeerrConnectionVerifier>();
        serviceCollection.AddSingleton<RequestResolver>();
    }
}
