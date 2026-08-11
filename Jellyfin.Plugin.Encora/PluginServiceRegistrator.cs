using Jellyfin.Plugin.Encora.Providers;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Encora;

/// <summary>
/// Registers Encora's <see cref="ISubtitleProvider"/> with the server's dependency injection container.
/// Unlike metadata providers (discovered by reflection), <see cref="ISubtitleProvider"/> instances are
/// resolved through DI, so they need an explicit registration to be picked up by <c>SubtitleManager</c>.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ISubtitleProvider, EncoraSubtitleProvider>();
    }
}
