using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.Encora.Configuration;
using Jellyfin.Plugin.Encora.Models;
using Jellyfin.Plugin.Encora.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora;

/// <summary>
/// The main plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    /// <param name="libraryManager">The library manager, used to keep libraries' fetcher allow-lists in sync with plugin settings.</param>
    /// <param name="logger">The logger instance.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILibraryManager libraryManager, ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets the name of the plugin.
    /// </summary>
    public override string Name => "Encora";

    /// <summary>
    /// Gets the unique identifier for the plugin.
    /// </summary>
    public override Guid Id => Guid.Parse("e0e9f5b9-5687-4a39-8e67-86c3399f9176");

    /// <summary>
    /// Gets the singleton instance of the plugin.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the configuration web pages for the plugin.
    /// </summary>
    /// <returns>An enumerable of <see cref="PluginPageInfo"/>.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        };
    }

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);

        try
        {
            EncoraLibraryEnabler.SyncEnabledLibraries(_libraryManager, Configuration, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Encora] Failed to sync library fetcher settings after configuration update");
        }
    }
}
