using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.SeerrPlaylists.Seerr.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrPlaylists.Sync;

public class RequestResolver
{
    private static readonly string[] _providers = [nameof(MetadataProvider.Tvdb), nameof(MetadataProvider.Tmdb)];

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<RequestResolver> _logger;

    public RequestResolver(ILibraryManager libraryManager, IUserManager userManager, ILogger<RequestResolver> logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    public IReadOnlyList<UserRequests> Resolve(IReadOnlyCollection<SeerrRequest> requests)
    {
        var groupingStopwatch = Stopwatch.StartNew();
        var users = _userManager.GetUsers().ToDictionary(u => u.Id);
        var requestsByUser = new Dictionary<Guid, (User User, List<SeerrRequest> Requests)>();
        var declined = 0;
        var withoutJellyfinUser = 0;
        foreach (var request in requests)
        {
            if (request.Status == SeerrRequestStatus.Declined)
            {
                declined++;
                _logger.LogDebug("Skipping Seerr request {RequestId}: declined", request.Id);
                continue;
            }

            var user = Guid.TryParse(request.RequestedBy?.JellyfinUserId, out var userId) ? users.GetValueOrDefault(userId) : null;
            if (user is null)
            {
                withoutJellyfinUser++;
                _logger.LogDebug(
                    "Skipping Seerr request {RequestId}: Seerr user {SeerrUserId} is not a Jellyfin user",
                    request.Id,
                    request.RequestedBy?.Id);
                continue;
            }

            if (!requestsByUser.TryGetValue(user.Id, out var entry))
            {
                entry = (user, []);
                requestsByUser.Add(user.Id, entry);
            }

            entry.Requests.Add(request);
        }

        if (Plugin.VerboseLogging)
        {
            _logger.LogInformation(
                "Grouped requests by {UserCount} requesters in {ElapsedMs} ms",
                requestsByUser.Count,
                groupingStopwatch.ElapsedMilliseconds);
        }

        var totals = new Dictionary<RequestOutcome, int>();
        var result = new List<UserRequests>();
        foreach (var (user, userRequests) in requestsByUser.Values)
        {
            var stopwatch = Stopwatch.StartNew();
            var visible = FindVisibleItems(user, userRequests);
            var lookupMs = stopwatch.ElapsedMilliseconds;
            var items = new Dictionary<Guid, BaseItem>();
            var repeats = 0;
            foreach (var request in userRequests)
            {
                var (item, outcome) = Match(request, user, visible);
                if (item is not null && !items.TryAdd(item.Id, item))
                {
                    outcome = RequestOutcome.Repeat;
                    repeats++;
                }

                totals[outcome] = totals.GetValueOrDefault(outcome) + 1;
                if (item is null && Plugin.VerboseLogging)
                {
                    _logger.LogInformation(
                        "Request {RequestId} of user {UserId} not matched ({Outcome}): {Type} with TMDb id {TmdbId} and TVDB id {TvdbId}, Seerr status {SeerrStatus}",
                        request.Id,
                        user.Id.ToString("N"),
                        outcome,
                        request.Type,
                        request.Media?.TmdbId,
                        request.Media?.TvdbId,
                        SeerrStatus(request));
                }
            }

            if (Plugin.VerboseLogging)
            {
                _logger.LogInformation(
                    "User {UserId}: {Requests} requests gave {Items} items, {Repeats} repeats and {Unmatched} unmatched, in {ElapsedMs} ms ({LookupMs} ms looking up {Candidates} library items)",
                    user.Id.ToString("N"),
                    userRequests.Count,
                    items.Count,
                    repeats,
                    userRequests.Count - items.Count - repeats,
                    stopwatch.ElapsedMilliseconds,
                    lookupMs,
                    visible.SelectMany(g => g).Distinct().Count());
            }

            if (items.Count > 0)
            {
                result.Add(new UserRequests(user, items.Values));
            }
        }

        _logger.LogInformation(
            "Fetched {Total} Seerr requests: {Matched} matched, {Repeats} repeats, {NotAvailableYet} not available yet, {NoLongerAvailable} no longer available, {Hidden} hidden from their requester, {NotFound} not found, {Other} other, {Declined} declined, {WithoutJellyfinUser} from Seerr users without a Jellyfin account. {UserCount} users have requested items in the library",
            requests.Count,
            totals.GetValueOrDefault(RequestOutcome.Matched),
            totals.GetValueOrDefault(RequestOutcome.Repeat),
            totals.GetValueOrDefault(RequestOutcome.NotAvailableYet),
            totals.GetValueOrDefault(RequestOutcome.NoLongerAvailable),
            totals.GetValueOrDefault(RequestOutcome.HiddenFromRequester),
            totals.GetValueOrDefault(RequestOutcome.NotFound),
            totals.GetValueOrDefault(RequestOutcome.Other),
            declined,
            withoutJellyfinUser,
            result.Count);

        return result;
    }

    private static BaseItemKind? KindOf(SeerrRequest request) => request.Type switch
    {
        "movie" => BaseItemKind.Movie,
        "tv" => BaseItemKind.Series,
        _ => null,
    };

    private static string Key(BaseItemKind kind, string provider, string id) => $"{kind}:{provider}:{id}";

    private static SeerrMediaStatus SeerrStatus(SeerrRequest request)
        => (request.Is4k ? request.Media?.Status4k : request.Media?.Status) ?? SeerrMediaStatus.NotSet;

    private ILookup<string, BaseItem> FindVisibleItems(User user, IReadOnlyCollection<SeerrRequest> requests)
    {
        var ids = new Dictionary<string, string[]>
        {
            [nameof(MetadataProvider.Tvdb)] = requests
                .Where(r => KindOf(r) == BaseItemKind.Series && r.Media?.TvdbId is not null)
                .Select(r => r.Media!.TvdbId!.Value.ToString(CultureInfo.InvariantCulture))
                .Distinct()
                .ToArray(),
            [nameof(MetadataProvider.Tmdb)] = requests
                .Where(r => KindOf(r) is not null && r.Media is not null)
                .Select(r => r.Media!.TmdbId.ToString(CultureInfo.InvariantCulture))
                .Distinct()
                .ToArray(),
        };

        // An empty list would match every item that has the provider at all.
        var providerIds = ids.Where(p => p.Value.Length > 0).ToDictionary(p => p.Key, p => p.Value);
        if (providerIds.Count == 0)
        {
            return Array.Empty<BaseItem>().ToLookup(_ => string.Empty);
        }

        // Querying as the requester returns one item per merged group: the primary if they can see it,
        // otherwise a version in a library they can. Separate copies of a title are not merged.
        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            Recursive = true,
            HasAnyProviderIds = providerIds,
        });

        return items
            .SelectMany(item => _providers
                .Where(provider => item.ProviderIds.TryGetValue(provider, out var id) && !string.IsNullOrEmpty(id))
                .Select(provider => (Key: Key(item.GetBaseItemKind(), provider, item.ProviderIds[provider]), Item: item)))
            .ToLookup(entry => entry.Key, entry => entry.Item);
    }

    private (BaseItem? Item, RequestOutcome Outcome) Match(SeerrRequest request, User user, ILookup<string, BaseItem> visible)
    {
        var media = request.Media;
        var kind = KindOf(request);
        if (media is null || kind is null)
        {
            return (null, RequestOutcome.Other);
        }

        var lookups = new List<(string Provider, int? Id)>();
        if (kind == BaseItemKind.Series)
        {
            lookups.Add((nameof(MetadataProvider.Tvdb), media.TvdbId));
        }

        lookups.Add((nameof(MetadataProvider.Tmdb), media.TmdbId));

        foreach (var (provider, id) in lookups)
        {
            if (id is null)
            {
                continue;
            }

            var copies = visible[Key(kind.Value, provider, id.Value.ToString(CultureInfo.InvariantCulture))].ToList();
            if (copies.Count > 1)
            {
                _logger.LogDebug(
                    "Seerr request {RequestId}: user {UserId} can see {Count} copies of {Name}, using the oldest",
                    request.Id,
                    user.Id.ToString("N"),
                    copies.Count,
                    copies[0].Name);
            }

            if (copies.Count > 0)
            {
                return (copies.OrderBy(c => c.DateCreated).ThenBy(c => c.Id).First(), RequestOutcome.Matched);
            }
        }

        var linkedId = request.Is4k ? media.JellyfinMediaId4k ?? media.JellyfinMediaId : media.JellyfinMediaId;
        if (Guid.TryParse(linkedId, out var itemId)
            && _libraryManager.GetItemById(itemId) is { } linkedItem
            && linkedItem.GetBaseItemKind() == kind)
        {
            return linkedItem.IsVisibleStandalone(user)
                ? (linkedItem, RequestOutcome.Matched)
                : (null, RequestOutcome.HiddenFromRequester);
        }

        return (null, SeerrStatus(request) switch
        {
            SeerrMediaStatus.Unknown or SeerrMediaStatus.Pending or SeerrMediaStatus.Processing => RequestOutcome.NotAvailableYet,
            SeerrMediaStatus.Deleted => RequestOutcome.NoLongerAvailable,
            SeerrMediaStatus.PartiallyAvailable or SeerrMediaStatus.Available => RequestOutcome.NotFound,
            _ => RequestOutcome.Other,
        });
    }
}
