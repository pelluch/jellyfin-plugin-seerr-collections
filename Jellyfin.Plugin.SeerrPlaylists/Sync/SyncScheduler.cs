using System;
using System.Threading;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.SeerrPlaylists.Sync;

public sealed class SyncScheduler : IDisposable
{
    // Scans add many items at once, and provide IDs don't arrive immediately.
    private static readonly TimeSpan _quietPeriod = TimeSpan.FromSeconds(60);

    private readonly Timer _timer;

    public SyncScheduler(ITaskManager taskManager)
    {
        _timer = new Timer(_ => taskManager.QueueScheduledTask<SyncRequestsTask>());
    }

    public void SyncSoon() => _timer.Change(_quietPeriod, Timeout.InfiniteTimeSpan);

    public void Dispose() => _timer.Dispose();
}
