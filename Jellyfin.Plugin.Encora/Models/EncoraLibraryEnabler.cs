using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Jellyfin.Plugin.Encora.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// When a library is enabled/scoped for Encora matching in the plugin settings, makes sure that
    /// library's own metadata/image fetcher allow-list (set via Jellyfin's "Manage Library" screen)
    /// doesn't silently exclude the Encora/StageMedia providers. Only ever adds our providers to an
    /// existing allow-list - it never creates a new one and never removes/disables anything else.
    /// </summary>
    public static class EncoraLibraryEnabler
    {
        private const string MetadataProviderName = "Encora";
        private const string ImageProviderName = "StageMedia";

        private static readonly string[] MovieItemTypes = { "Movie" };
        private static readonly string[] TvItemTypes = { "Series", "Season", "Episode" };
        private static readonly string[] AudioItemTypes = { "MusicArtist", "MusicAlbum", "Audio" };

        /// <summary>
        /// Syncs every enabled library type against the current configuration.
        /// </summary>
        /// <param name="libraryManager">The library manager.</param>
        /// <param name="config">The current plugin configuration.</param>
        /// <param name="logger">Logger for reporting what changed.</param>
        public static void SyncEnabledLibraries(ILibraryManager libraryManager, PluginConfiguration config, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(libraryManager);
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(logger);

            SyncGroup(libraryManager, config.EnableMovieMatching, config.MovieLibraryIds, CollectionTypeOptions.movies, MovieItemTypes, includeImageFetcher: true, logger);
            SyncGroup(libraryManager, config.EnableTvMatching, config.TvLibraryIds, CollectionTypeOptions.tvshows, TvItemTypes, includeImageFetcher: true, logger);
            SyncGroup(libraryManager, config.EnableAudioMatching, config.AudioLibraryIds, CollectionTypeOptions.music, AudioItemTypes, includeImageFetcher: false, logger);
        }

        private static void SyncGroup(
            ILibraryManager libraryManager,
            bool enabled,
            Collection<string> scopedLibraryIds,
            CollectionTypeOptions collectionType,
            IReadOnlyList<string> itemTypeNames,
            bool includeImageFetcher,
            ILogger logger)
        {
            if (!enabled)
            {
                return;
            }

            var scoped = scopedLibraryIds != null && scopedLibraryIds.Count > 0
                ? new HashSet<string>(scopedLibraryIds, StringComparer.OrdinalIgnoreCase)
                : null;

            foreach (var folder in libraryManager.GetVirtualFolders())
            {
                if (folder.CollectionType != collectionType)
                {
                    continue;
                }

                if (scoped != null && !scoped.Contains(folder.ItemId))
                {
                    continue;
                }

                if (!Guid.TryParse(folder.ItemId, out var itemId) ||
                    libraryManager.GetItemById(itemId) is not CollectionFolder item)
                {
                    continue;
                }

                var options = item.GetLibraryOptions();
                var changed = false;

                foreach (var typeName in itemTypeNames)
                {
                    var typeOptions = options.GetTypeOptions(typeName);
                    if (typeOptions is null)
                    {
                        // No explicit allow-list exists for this type yet, so every fetcher
                        // (ours included) is already implicitly enabled - nothing to fix.
                        continue;
                    }

                    if (EnsureContains(typeOptions.MetadataFetchers, MetadataProviderName, out var newMetadataFetchers))
                    {
                        typeOptions.MetadataFetchers = newMetadataFetchers;
                        changed = true;
                    }

                    if (includeImageFetcher && EnsureContains(typeOptions.ImageFetchers, ImageProviderName, out var newImageFetchers))
                    {
                        typeOptions.ImageFetchers = newImageFetchers;
                        changed = true;
                    }
                }

                if (changed)
                {
                    item.UpdateLibraryOptions(options);
                    logger.LogInformation("[Encora] Ensured Encora provider is enabled for library '{Library}'", folder.Name);
                }
            }
        }

        private static bool EnsureContains(string[] existing, string name, out string[] updated)
        {
            if (existing != null && existing.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                updated = existing;
                return false;
            }

            updated = existing == null ? new[] { name } : existing.Append(name).ToArray();
            return true;
        }
    }
}
