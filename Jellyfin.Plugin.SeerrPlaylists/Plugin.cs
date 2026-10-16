using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.SeerrPlaylists.Configuration;
using Jellyfin.Plugin.SeerrPlaylists.Sync;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.SeerrPlaylists;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly CollectionReconciler _reconciler;

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, CollectionReconciler reconciler)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _reconciler = reconciler;
    }

    public override string Name => "Seerr Playlists";

    public override Guid Id => Guid.Parse("727ca564-1ccc-4dff-b104-ab3f2254418d");

    public static Plugin? Instance { get; private set; }

    public static bool VerboseLogging => Instance?.Configuration.VerboseLogging == true;

    public override void OnUninstalling()
    {
        _reconciler.RemoveEverythingAsync().GetAwaiter().GetResult();
        base.OnUninstalling();
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
