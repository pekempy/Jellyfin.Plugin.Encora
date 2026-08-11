using System;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Guards the Overview field against being clobbered when an admin has manually edited it since the last Encora sync.
    /// </summary>
    public static class EncoraOverviewGuard
    {
        private const string SyncHashProviderId = "EncoraOverviewSyncHash";

        /// <summary>
        /// Applies the freshly-fetched Overview text to <paramref name="item"/>, unless the admin has manually
        /// edited the existing item's Overview since the last time this plugin wrote it (detected via a stored
        /// content hash), in which case the existing text is left untouched.
        /// </summary>
        /// <typeparam name="T">The item type.</typeparam>
        /// <param name="item">The metadata result item to populate.</param>
        /// <param name="libraryManager">Used to look up the currently-saved item, if any.</param>
        /// <param name="path">The file or folder path used to locate the existing item.</param>
        /// <param name="isFolder">Whether <paramref name="path"/> refers to a folder (Series) or a file (Movie/Episode).</param>
        /// <param name="newOverview">The freshly-generated Overview text from Encora.</param>
        /// <param name="preserveManualEdits">Whether the guard is enabled at all (plugin setting).</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="logContext">A short description of the item, for log messages.</param>
        public static void ApplyOverview<T>(T item, ILibraryManager libraryManager, string path, bool isFolder, string newOverview, bool preserveManualEdits, ILogger logger, string logContext)
            where T : BaseItem
        {
            if (preserveManualEdits)
            {
                var existing = libraryManager.FindByPath(path, isFolder);
                if (existing != null &&
                    existing.ProviderIds.TryGetValue(SyncHashProviderId, out var storedHash) &&
                    !string.Equals(ComputeHash(existing.Overview ?? string.Empty), storedHash, StringComparison.Ordinal))
                {
                    logger.LogInformation("[Encora] Overview for {Context} was manually edited, preserving existing text", logContext);
                    return;
                }
            }

            item.Overview = newOverview;
            item.ProviderIds[SyncHashProviderId] = ComputeHash(newOverview);
        }

        private static string ComputeHash(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(bytes);
        }
    }
}
