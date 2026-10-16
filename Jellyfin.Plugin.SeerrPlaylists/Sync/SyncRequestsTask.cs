using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SeerrPlaylists.Configuration;
using Jellyfin.Plugin.SeerrPlaylists.Seerr;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrPlaylists.Sync;

public class SyncRequestsTask : IScheduledTask
{
    private readonly SeerrClient _client;
    private readonly SeerrConnectionVerifier _verifier;
    private readonly RequestResolver _resolver;
    private readonly CollectionReconciler _reconciler;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly ILogger<SyncRequestsTask> _logger;

    public SyncRequestsTask(
        SeerrClient client,
        SeerrConnectionVerifier verifier,
        RequestResolver resolver,
        CollectionReconciler reconciler,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<SyncRequestsTask> logger)
    {
        _client = client;
        _verifier = verifier;
        _resolver = resolver;
        _reconciler = reconciler;
        _serverConfigurationManager = serverConfigurationManager;
        _logger = logger;
    }

    public string Name => "Sync Seerr requests";

    public string Key => "SeerrPlaylistsSync";

    public string Description => "Updates every user's collection of the items they requested on Seerr.";

    public string Category => "Seerr Playlists";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance!.Configuration;
        if (!configuration.SyncEnabled)
        {
            _logger.LogInformation("Syncing is turned off in the plugin settings, nothing to sync");
            return;
        }

        if (string.IsNullOrWhiteSpace(configuration.SeerrUrl) || string.IsNullOrWhiteSpace(configuration.SeerrApiKey))
        {
            _logger.LogInformation("Seerr is not configured yet, nothing to sync");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var connection = SeerrConnection.Create(configuration.SeerrUrl, configuration.SeerrApiKey);
        await _verifier.VerifyAsync(connection, cancellationToken).ConfigureAwait(false);
        var verifyMs = stopwatch.ElapsedMilliseconds;
        progress.Report(10);

        var requests = await _client.GetRequestsAsync(connection, cancellationToken).ConfigureAwait(false);
        var fetchMs = stopwatch.ElapsedMilliseconds - verifyMs;
        progress.Report(40);

        var userRequests = _resolver.Resolve(requests);
        var matchMs = stopwatch.ElapsedMilliseconds - verifyMs - fetchMs;

        WarnAboutGrouping();

        var collectionName = string.IsNullOrWhiteSpace(configuration.CollectionName)
            ? PluginConfiguration.DefaultCollectionName
            : configuration.CollectionName.Trim();
        await _reconciler.ReconcileAsync(userRequests, collectionName, configuration.DryRun, cancellationToken).ConfigureAwait(false);
        progress.Report(100);

        if (Plugin.VerboseLogging)
        {
            _logger.LogInformation(
                "Sync took {TotalMs} ms: checking Seerr {VerifyMs} ms, fetching requests {FetchMs} ms, matching {MatchMs} ms, collections and tags {ReconcileMs} ms",
                stopwatch.ElapsedMilliseconds,
                verifyMs,
                fetchMs,
                matchMs,
                stopwatch.ElapsedMilliseconds - verifyMs - fetchMs - matchMs);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(15).Ticks,
        };
    }

    private void WarnAboutGrouping()
    {
        var serverConfiguration = _serverConfigurationManager.Configuration;
        if (serverConfiguration.EnableGroupingMoviesIntoCollections || serverConfiguration.EnableGroupingShowsIntoCollections)
        {
            _logger.LogWarning(
                "Grouping movies or shows into collections is enabled. Jellyfin then hides requested items from "
                + "every user's library views behind request collections that only their owner can open.");
        }
    }
}
