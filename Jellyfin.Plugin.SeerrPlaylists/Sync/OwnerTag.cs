using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SeerrPlaylists.Sync;

public static class OwnerTag
{
    private const string Prefix = "seerr-requests-";

    public static string For(Guid userId) => Prefix + userId.ToString("N");

    public static bool IsOwnerTag(string tag) => tag.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static Guid? FindOwner(IEnumerable<string> tags)
    {
        foreach (var tag in tags)
        {
            if (IsOwnerTag(tag) && Guid.TryParse(tag.AsSpan(Prefix.Length), out var userId))
            {
                return userId;
            }
        }

        return null;
    }
}
