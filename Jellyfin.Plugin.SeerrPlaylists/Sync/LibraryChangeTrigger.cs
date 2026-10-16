using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.SeerrPlaylists.Sync;

public sealed class LibraryChangeTrigger : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly SyncScheduler _scheduler;

    public LibraryChangeTrigger(ILibraryManager libraryManager, SyncScheduler scheduler)
    {
        _libraryManager = libraryManager;
        _scheduler = scheduler;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e) => SyncIfRelevant(e);

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if ((e.UpdateReason & (ItemUpdateType.MetadataImport | ItemUpdateType.MetadataDownload)) != 0)
        {
            SyncIfRelevant(e);
        }
    }

    private void SyncIfRelevant(ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Series)
        {
            _scheduler.SyncSoon();
        }
    }
}
