using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.ArrDashboard.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ArrDashboard;

/// <summary>
/// Arr Dashboard plugin entry point.
/// </summary>
public class ArrDashboardPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public ArrDashboardPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static ArrDashboardPlugin? Instance { get; private set; }

    public override string Name => "Arr Dashboard";

    public override string Description =>
        "Monitor Sonarr and Radarr from inside Jellyfin: release calendar, series and movie status, download queue and history.";

    public override Guid Id => Guid.Parse("b9d3a0a1-6a7f-4a6c-9b20-8e4a2b3f1d77");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;

        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                prefix)
        };

        yield return new PluginPageInfo
        {
            Name = "arrdashboard",
            DisplayName = "Arr Dashboard",
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Web.arrdashboard.html",
                prefix),
            EnableInMainMenu = true,
            MenuIcon = "calendar_month"
        };
    }
}
