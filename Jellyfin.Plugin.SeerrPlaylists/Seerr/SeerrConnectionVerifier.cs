using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.SeerrPlaylists.Seerr.Models;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.SeerrPlaylists.Seerr;

public class SeerrConnectionVerifier
{
    private readonly SeerrClient _client;
    private readonly IServerApplicationHost _applicationHost;

    public SeerrConnectionVerifier(SeerrClient client, IServerApplicationHost applicationHost)
    {
        _client = client;
        _applicationHost = applicationHost;
    }

    public async Task<SeerrServerInfo> VerifyAsync(SeerrConnection connection, CancellationToken cancellationToken)
    {
        var status = await _client.GetStatusAsync(connection, cancellationToken).ConfigureAwait(false);

        var mediaServerType = (await _client.GetPublicSettingsAsync(connection, cancellationToken).ConfigureAwait(false)).MediaServerType;
        if (mediaServerType != SeerrMediaServerType.Jellyfin)
        {
            throw new SeerrException(mediaServerType switch
            {
                SeerrMediaServerType.Plex => "Seerr is set up for Plex, not Jellyfin.",
                SeerrMediaServerType.Emby => "Seerr is set up for Emby, not Jellyfin.",
                _ => "Seerr has no media server set up.",
            });
        }

        var jellyfin = await _client.GetJellyfinSettingsAsync(connection, cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(jellyfin.ServerId, out var seerrServerId))
        {
            throw new SeerrException("Seerr has no Jellyfin server set up.");
        }

        if (seerrServerId != Guid.Parse(_applicationHost.SystemId))
        {
            throw new SeerrException($"Seerr is connected to a different Jellyfin server: \"{jellyfin.Name}\" ({jellyfin.ServerId}).");
        }

        return new SeerrServerInfo(status.Version, jellyfin.Name);
    }
}
