using Jellyfin.Plugin.Encora.Models;
using Jellyfin.Plugin.Encora.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Encora;

/// <summary>
/// Registers Encora's <see cref="ISubtitleProvider"/> and background services with the server's dependency
/// injection container. Unlike metadata providers (discovered by reflection), these need an explicit
/// registration to be picked up by the server.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ISubtitleProvider, EncoraSubtitleProvider>();
        serviceCollection.AddHostedService<EncoraLibraryScanWatcher>();
    }
}
