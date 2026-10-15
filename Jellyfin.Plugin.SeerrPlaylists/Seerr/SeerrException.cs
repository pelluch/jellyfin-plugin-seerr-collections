using System;

namespace Jellyfin.Plugin.SeerrPlaylists.Seerr;

public class SeerrException : Exception
{
    public SeerrException(string message, bool isTransient = false, Exception? innerException = null)
        : base(message, innerException)
    {
        IsTransient = isTransient;
    }

    public bool IsTransient { get; }
}
