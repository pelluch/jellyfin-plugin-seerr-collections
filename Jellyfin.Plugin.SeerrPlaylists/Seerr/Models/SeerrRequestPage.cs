using System.Collections.Generic;

namespace Jellyfin.Plugin.SeerrPlaylists.Seerr.Models;

public sealed record SeerrRequestPage(SeerrPageInfo PageInfo, IReadOnlyList<SeerrRequest> Results);
