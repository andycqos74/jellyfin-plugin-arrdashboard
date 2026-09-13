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

    /// <summary>
    /// Page name of the dashboard view, i.e. #/configurationpage?name=arrdashboard.
    /// </summary>
    public const string DashboardPageName = "arrdashboard";

    public static ArrDashboardPlugin? Instance { get; private set; }

    public override string Name => "Arr Dashboard";

    public override string Description =>
        "Monitor Sonarr and Radarr from inside Jellyfin: release calendar, series and movie status, download queue and history.";

    public override Guid Id => Guid.Parse("b9d3a0a1-6a7f-4a6c-9b20-8e4a2b3f1d77");

    /// <summary>
    /// Both pages are served from /web/ConfigurationPage?name=...
    /// </summary>
    /// <remarks>
    /// The settings page must be listed first and no page may set
    /// <c>EnableInMainMenu</c>. The web client picks the page behind a plugin's
    /// "Settings" button with <c>findBestConfigurationPage()</c>, which prefers a
    /// page flagged for the main menu and otherwise takes the first one; flagging
    /// the dashboard therefore made Settings open the dashboard and left the
    /// settings page unreachable. The flag buys nothing in return: main-menu
    /// entries for plugin pages were dropped from the web client after 10.8, so
    /// <c>EnableInMainMenu</c> and <c>MenuIcon</c> are now inert.
    /// </remarks>
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
            Name = DashboardPageName,
            DisplayName = "Arr Dashboard",
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Web.arrdashboard.html",
                prefix)
        };
    }
}
