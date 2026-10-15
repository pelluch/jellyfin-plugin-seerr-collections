using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SeerrPlaylists.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public const string DefaultCollectionName = "My Requests";

    public string SeerrUrl { get; set; } = string.Empty;

    public string SeerrApiKey { get; set; } = string.Empty;

    public string CollectionName { get; set; } = DefaultCollectionName;

    public bool DryRun { get; set; } = true;
}
