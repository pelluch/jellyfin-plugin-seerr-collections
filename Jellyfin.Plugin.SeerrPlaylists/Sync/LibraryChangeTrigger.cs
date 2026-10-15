using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.SeerrPlaylists.Sync;

public sealed class LibraryChangeTrigger : IHostedService, IDisposable
{
    // Scans add items in bursts, and the provider ids used for matching arrive with the metadata refresh after ItemAdded.
    private static readonly TimeSpan _quietPeriod = TimeSpan.FromSeconds(30);

    private readonly ILibraryManager _libraryManager;
    private readonly Timer _timer;

    public LibraryChangeTrigger(ILibraryManager libraryManager, ITaskManager taskManager)
    {
        _libraryManager = libraryManager;
        _timer = new Timer(_ => taskManager.QueueScheduledTask<SyncRequestsTask>());
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
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer.Dispose();

    private void OnItemAdded(object? sender, ItemChangeEventArgs e) => Restart(e);

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if ((e.UpdateReason & (ItemUpdateType.MetadataImport | ItemUpdateType.MetadataDownload)) != 0)
        {
            Restart(e);
        }
    }

    private void Restart(ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Series)
        {
            _timer.Change(_quietPeriod, Timeout.InfiniteTimeSpan);
        }
    }
}
