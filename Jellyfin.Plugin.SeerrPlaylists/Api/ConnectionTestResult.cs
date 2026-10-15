namespace Jellyfin.Plugin.SeerrPlaylists.Api;

public sealed record ConnectionTestResult(bool Success, string? Error, string? SeerrVersion, string? JellyfinServerName);
