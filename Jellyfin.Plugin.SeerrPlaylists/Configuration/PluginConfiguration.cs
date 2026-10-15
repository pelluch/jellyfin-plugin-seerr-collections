using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SeerrPlaylists.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string SeerrUrl { get; set; } = string.Empty;

    public string SeerrApiKey { get; set; } = string.Empty;

    public string CollectionName { get; set; } = "My Requests";

    public bool DryRun { get; set; } = true;
}
