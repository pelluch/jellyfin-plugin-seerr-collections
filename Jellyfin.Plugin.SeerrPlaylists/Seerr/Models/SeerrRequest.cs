namespace Jellyfin.Plugin.SeerrPlaylists.Seerr.Models;

public sealed record SeerrRequest(
    int Id,
    SeerrRequestStatus Status,
    string Type,
    bool Is4k,
    SeerrMedia? Media,
    SeerrUser? RequestedBy);
