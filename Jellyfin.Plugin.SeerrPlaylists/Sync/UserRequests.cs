using System.Collections.Generic;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SeerrPlaylists.Sync;

public sealed record UserRequests(User User, IReadOnlyCollection<BaseItem> Items);
