namespace Jellyfin.Plugin.SeerrPlaylists.Seerr.Models;

public sealed record SeerrMedia(
    int TmdbId,
    int? TvdbId,
    SeerrMediaStatus Status,
    SeerrMediaStatus Status4k,
    string? JellyfinMediaId,
    string? JellyfinMediaId4k);
