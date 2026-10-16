namespace Jellyfin.Plugin.SeerrPlaylists.Seerr.Models;

public enum SeerrMediaStatus
{
    NotSet = 0,
    Unknown = 1,
    Pending = 2,
    Processing = 3,
    PartiallyAvailable = 4,
    Available = 5,
    Blocklisted = 6,
    Deleted = 7,
}
