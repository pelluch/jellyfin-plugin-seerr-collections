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

            var items = FindItems(request);
            if (items.Count == 0)
            {
                _logger.LogDebug(
                    "Skipping Seerr request {RequestId}: {Type} with TMDb id {TmdbId} is not in the library",
                    request.Id,
                    request.Type,
                    request.Media?.TmdbId);
                continue;
            }

            if (!users.TryGetValue(user.Id, out var entry))
            {
                entry = (user, new Dictionary<Guid, BaseItem>());
                users.Add(user.Id, entry);
            }

            foreach (var item in items)
            {
                entry.Items.TryAdd(item.Id, item);
            }
        }

        return users.Values.Select(e => new UserRequests(e.User, e.Items.Values)).ToList();
    }

    private User? FindUser(SeerrRequest request)
        => Guid.TryParse(request.RequestedBy?.JellyfinUserId, out var userId) ? _userManager.GetUserById(userId) : null;

    private IReadOnlyList<BaseItem> FindItems(SeerrRequest request)
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
            return [];
        }

        var linkedId = request.Is4k ? media.JellyfinMediaId4k ?? media.JellyfinMediaId : media.JellyfinMediaId;
        if (Guid.TryParse(linkedId, out var itemId)
            && _libraryManager.GetItemById(itemId) is { } linkedItem
            && linkedItem.GetBaseItemKind() == kind)
        {
            return [linkedItem];
        }

        var providerIds = new Dictionary<string, string>
        {
            [nameof(MetadataProvider.Tmdb)] = media.TmdbId.ToString(CultureInfo.InvariantCulture),
        };
        if (kind == BaseItemKind.Series && media.TvdbId is { } tvdbId)
        {
            providerIds[nameof(MetadataProvider.Tvdb)] = tvdbId.ToString(CultureInfo.InvariantCulture);
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [kind.Value],
            Recursive = true,
            HasAnyProviderId = providerIds,
        });
    }
}
