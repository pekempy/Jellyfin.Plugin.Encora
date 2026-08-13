using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Encora.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Keeps a JSON backup of the plugin's configuration alongside Jellyfin's own XML config file, with the
    /// API keys encrypted (see <see cref="EncoraConfigurationEncryption"/>). Jellyfin's
    /// <c>BasePlugin&lt;T&gt;.LoadConfiguration()</c> silently overwrites the saved config with a fresh
    /// default instance if deserializing it throws for any reason - which happens whenever the plugin gets
    /// updated while the server is running, because Jellyfin hot-loads the new assembly into a second
    /// AssemblyLoadContext without unloading the old one first, and the very first Configuration access
    /// afterwards throws a cross-context <see cref="InvalidCastException"/>. This backup - written and read
    /// independently of Jellyfin's own XML serializer - lets that wipe be detected and reversed automatically
    /// the next time the plugin starts up.
    /// </summary>
    public static class EncoraConfigurationBackup
    {
        private const string BackupFileName = "Jellyfin.Plugin.Encora.backup.json";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        /// <summary>
        /// Writes a backup copy of the given configuration, with the API keys encrypted. Skipped entirely if
        /// the configuration looks like an unconfigured default - otherwise a wipe would immediately
        /// overwrite a previously-good backup with an empty one.
        /// </summary>
        /// <param name="configFolderPath">The folder Jellyfin stores plugin configuration files in.</param>
        /// <param name="config">The configuration to back up.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        public static void Save(string configFolderPath, PluginConfiguration config, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(config.EncoraAPIKey))
            {
                return;
            }

            try
            {
                if (JsonSerializer.SerializeToNode(config, JsonOptions) is not JsonObject node)
                {
                    return;
                }

                node["EncoraAPIKey"] = JsonValue.Create(EncoraConfigurationEncryption.Encrypt(configFolderPath, config.EncoraAPIKey));
                node["StageMediaAPIKey"] = JsonValue.Create(EncoraConfigurationEncryption.Encrypt(configFolderPath, config.StageMediaAPIKey ?? string.Empty));

                var path = Path.Combine(configFolderPath, BackupFileName);
                File.WriteAllText(path, node.ToJsonString(JsonOptions));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Encora] Failed to write configuration backup");
            }
        }

        /// <summary>
        /// Loads the backup configuration, if one exists and looks usable, decrypting the API keys.
        /// </summary>
        /// <param name="configFolderPath">The folder Jellyfin stores plugin configuration files in.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <returns>The backed-up configuration, or null if there isn't a usable one.</returns>
        public static PluginConfiguration? TryLoad(string configFolderPath, ILogger logger)
        {
            try
            {
                var path = Path.Combine(configFolderPath, BackupFileName);
                if (!File.Exists(path))
                {
                    return null;
                }

                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject node)
                {
                    return null;
                }

                var encryptedApiKey = node["EncoraAPIKey"]?.GetValue<string>();
                var encryptedStageMediaKey = node["StageMediaAPIKey"]?.GetValue<string>();

                node["EncoraAPIKey"] = JsonValue.Create(string.IsNullOrEmpty(encryptedApiKey)
                    ? string.Empty
                    : EncoraConfigurationEncryption.Decrypt(configFolderPath, encryptedApiKey));
                node["StageMediaAPIKey"] = JsonValue.Create(string.IsNullOrEmpty(encryptedStageMediaKey)
                    ? string.Empty
                    : EncoraConfigurationEncryption.Decrypt(configFolderPath, encryptedStageMediaKey));

                var backup = node.Deserialize<PluginConfiguration>(JsonOptions);
                return string.IsNullOrWhiteSpace(backup?.EncoraAPIKey) ? null : backup;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Encora] Failed to read configuration backup");
                return null;
            }
        }
    }
}
