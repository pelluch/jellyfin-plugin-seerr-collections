using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SeerrPlaylists.Seerr;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrPlaylists.Sync;

public class SyncRequestsTask : IScheduledTask
{
    private readonly SeerrClient _client;
    private readonly SeerrConnectionVerifier _verifier;
    private readonly RequestResolver _resolver;
    private readonly ILogger<SyncRequestsTask> _logger;

    public SyncRequestsTask(
        SeerrClient client,
        SeerrConnectionVerifier verifier,
        RequestResolver resolver,
        ILogger<SyncRequestsTask> logger)
    {
        _client = client;
        _verifier = verifier;
        _resolver = resolver;
        _logger = logger;
    }

    public string Name => "Sync Seerr requests";

    public string Key => "SeerrPlaylistsSync";

    public string Description => "Updates every user's collection of the items they requested on Seerr.";

    public string Category => "Seerr Playlists";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(configuration.SeerrUrl) || string.IsNullOrWhiteSpace(configuration.SeerrApiKey))
        {
            _logger.LogInformation("Seerr is not configured yet, nothing to sync");
            return;
        }

        var connection = SeerrConnection.Create(configuration.SeerrUrl, configuration.SeerrApiKey);
        await _verifier.VerifyAsync(connection, cancellationToken).ConfigureAwait(false);
        progress.Report(20);

        var requests = await _client.GetRequestsAsync(connection, cancellationToken).ConfigureAwait(false);
        progress.Report(60);

        var userRequests = _resolver.Resolve(requests);
        _logger.LogInformation(
            "Fetched {RequestCount} Seerr requests, {UserCount} users have requested items in the library",
            requests.Count,
            userRequests.Count);
        foreach (var (user, items) in userRequests)
        {
            _logger.LogInformation(
                "{User} requested {ItemCount} items in the library: {Items}",
                user.Username,
                items.Count,
                string.Join(", ", items.Select(i => i.Name).Order(StringComparer.CurrentCulture)));
        }

        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(15).Ticks,
        };
    }
}
