namespace Jellyfin.Plugin.SeerrPlaylists.Seerr.Models;

public sealed record SeerrMedia(int TmdbId, int? TvdbId, string? JellyfinMediaId, string? JellyfinMediaId4k);
