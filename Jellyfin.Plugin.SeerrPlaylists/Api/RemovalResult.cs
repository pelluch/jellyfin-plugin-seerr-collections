namespace Jellyfin.Plugin.SeerrPlaylists.Api;

public sealed record RemovalResult(bool DryRun, int Collections, int Users, bool SyncEnabled);
