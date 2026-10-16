namespace Jellyfin.Plugin.SeerrPlaylists.Sync;

public enum RequestOutcome
{
    Matched = 0,
    Repeat = 1,
    NotAvailableYet = 2,
    NoLongerAvailable = 3,
    HiddenFromRequester = 4,
    NotFound = 5,
    Other = 6,
}
