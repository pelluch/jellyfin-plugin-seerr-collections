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
        var users = new Dictionary<Guid, (User User, Dictionary<Guid, BaseItem> Items)>();
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

            var item = FindItem(request, user);
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

            if (!users.TryGetValue(user.Id, out var entry))
            {
                entry = (user, new Dictionary<Guid, BaseItem>());
                users.Add(user.Id, entry);
            }

            entry.Items.TryAdd(item.Id, item);
        }

        return users.Values.Select(e => new UserRequests(e.User, e.Items.Values)).ToList();
    }

    private User? FindUser(SeerrRequest request)
        => Guid.TryParse(request.RequestedBy?.JellyfinUserId, out var userId) ? _userManager.GetUserById(userId) : null;

    private BaseItem? FindItem(SeerrRequest request, User user)
    {
        var media = request.Media;
        BaseItemKind? kind = request.Type switch
        {
            "movie" => BaseItemKind.Movie,
            "tv" => BaseItemKind.Series,
            _ => null,
        };
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

            // Querying as the requester returns one item per merged group: the primary if they can see it,
            // otherwise a version in a library they can. Separate copies of a title are not merged.
            var copies = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = [kind.Value],
                Recursive = true,
                HasAnyProviderId = new Dictionary<string, string> { [provider] = id.Value.ToString(CultureInfo.InvariantCulture) },
            });
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
