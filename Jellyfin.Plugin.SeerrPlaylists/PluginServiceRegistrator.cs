using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.SeerrPlaylists.Configuration;
using Jellyfin.Plugin.SeerrPlaylists.Seerr;
using Jellyfin.Plugin.SeerrPlaylists.Sync;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
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
        serviceCollection.AddSingleton<CoverImage>();
        serviceCollection.AddSingleton<CollectionReconciler>();
        serviceCollection.AddSingleton<SyncScheduler>();
        serviceCollection.AddHostedService<LibraryChangeTrigger>();
        serviceCollection.AddScoped<IEventConsumer<UserCreatedEventArgs>, UserEventConsumer>();
        serviceCollection.AddScoped<IEventConsumer<UserUpdatedEventArgs>, UserEventConsumer>();
        serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, UserEventConsumer>();
    }
}
