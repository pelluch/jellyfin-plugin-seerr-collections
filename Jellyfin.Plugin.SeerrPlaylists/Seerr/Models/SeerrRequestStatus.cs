namespace Jellyfin.Plugin.SeerrPlaylists.Seerr.Models;

public enum SeerrRequestStatus
{
    Unknown = 0,
    Pending = 1,
    Approved = 2,
    Declined = 3,
    Failed = 4,
    Completed = 5,
}
