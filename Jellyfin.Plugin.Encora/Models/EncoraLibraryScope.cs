using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Checks whether an item's path falls under one of a set of allow-listed libraries, so Movie/TV/Music
    /// matching can be restricted to specific libraries instead of applying to every library of that type.
    /// </summary>
    public static class EncoraLibraryScope
    {
        /// <summary>
        /// Returns true if <paramref name="path"/> is within one of the allowed libraries, or if no
        /// restriction is configured (empty/null <paramref name="allowedLibraryIds"/> means unrestricted).
        /// </summary>
        /// <param name="libraryManager">Used to resolve configured library folders and their physical paths.</param>
        /// <param name="path">The item's file or folder path.</param>
        /// <param name="allowedLibraryIds">The allow-listed library IDs, or empty/null for no restriction.</param>
        /// <returns>Whether the path is in scope.</returns>
        public static bool IsPathInScope(ILibraryManager libraryManager, string path, IReadOnlyCollection<string>? allowedLibraryIds)
        {
            if (allowedLibraryIds == null || allowedLibraryIds.Count == 0 || string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            var normalizedPath = path.Replace('\\', '/');

            foreach (var folder in libraryManager.GetVirtualFolders())
            {
                if (!allowedLibraryIds.Contains(folder.ItemId, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var location in folder.Locations)
                {
                    var normalizedLocation = location.Replace('\\', '/').TrimEnd('/');
                    if (normalizedPath.StartsWith(normalizedLocation + "/", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(normalizedPath, normalizedLocation, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
