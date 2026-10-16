using System;
using System.Collections.Generic;
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

    public IReadOnlyList<UserRequests> Resolve(IEnumerable<SeerrRequest> requests)
    {
        var requestsByUser = new Dictionary<Guid, (User User, List<SeerrRequest> Requests)>();
        foreach (var request in requests)
        {
            if (request.Status == SeerrRequestStatus.Declined)
            {
                _logger.LogDebug("Skipping Seerr request {RequestId}: declined", request.Id);
                continue;
            }

            var user = FindUser(request);
            if (user is null)
            {
                _logger.LogDebug(
                    "Skipping Seerr request {RequestId}: requester {SeerrUser} is not a Jellyfin user",
                    request.Id,
                    request.RequestedBy?.DisplayName);
                continue;
            }

            if (!requestsByUser.TryGetValue(user.Id, out var entry))
            {
                entry = (user, []);
                requestsByUser.Add(user.Id, entry);
            }

            entry.Requests.Add(request);
        }

        var result = new List<UserRequests>();
        foreach (var (user, userRequests) in requestsByUser.Values)
        {
            var visible = FindVisibleItems(user, userRequests);
            var items = new Dictionary<Guid, BaseItem>();
            foreach (var request in userRequests)
            {
                var item = FindItem(request, user, visible);
                if (item is null)
                {
                    _logger.LogDebug(
                        "Skipping Seerr request {RequestId}: {User} can see no {Type} with TMDb id {TmdbId}",
                        request.Id,
                        user.Username,
                        request.Type,
                        request.Media?.TmdbId);
                    continue;
                }

                items.TryAdd(item.Id, item);
            }

            if (items.Count > 0)
            {
                result.Add(new UserRequests(user, items.Values));
            }
        }

        return result;
    }

    private static BaseItemKind? KindOf(SeerrRequest request) => request.Type switch
    {
        "movie" => BaseItemKind.Movie,
        "tv" => BaseItemKind.Series,
        _ => null,
    };

    private static string Key(BaseItemKind kind, string provider, string id) => $"{kind}:{provider}:{id}";

    private User? FindUser(SeerrRequest request)
        => Guid.TryParse(request.RequestedBy?.JellyfinUserId, out var userId) ? _userManager.GetUserById(userId) : null;

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

    private BaseItem? FindItem(SeerrRequest request, User user, ILookup<string, BaseItem> visible)
    {
        var media = request.Media;
        var kind = KindOf(request);
        if (media is null || kind is null)
        {
            return null;
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
                    "Seerr request {RequestId}: {User} can see {Count} copies of {Name}, using the oldest",
                    request.Id,
                    user.Username,
                    copies.Count,
                    copies[0].Name);
            }

            if (copies.Count > 0)
            {
                return copies.OrderBy(c => c.DateCreated).ThenBy(c => c.Id).First();
            }
        }

        var linkedId = request.Is4k ? media.JellyfinMediaId4k ?? media.JellyfinMediaId : media.JellyfinMediaId;
        return Guid.TryParse(linkedId, out var itemId)
            && _libraryManager.GetItemById(itemId) is { } linkedItem
            && linkedItem.GetBaseItemKind() == kind
            && linkedItem.IsVisibleStandalone(user)
                ? linkedItem
                : null;
    }
}
