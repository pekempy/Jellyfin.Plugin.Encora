using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Encora.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Persistent on-disk cache of the user's Encora collection, keyed by recording ID.
    /// Populated from the <c>/api/collection</c> bulk endpoint (one fetch covers the entire
    /// collection) so individual metadata providers never need to call <c>/api/recording/{id}</c>
    /// for recordings that are already in the collection — eliminating the primary source of
    /// rate-limit pressure during library scans.
    /// </summary>
    public static class EncoraCollectionCache
    {
        private static readonly ConcurrentDictionary<string, EncoraRecording> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        private static string? _cacheFilePath;
        private static DateTime _lastFetched = DateTime.MinValue;
        private static ILogger? _logger;

        /// <summary>
        /// Gets the UTC time the cache was last successfully populated from Encora.
        /// </summary>
        public static DateTime LastFetched => _lastFetched;

        /// <summary>
        /// Gets the number of recordings currently held in the cache.
        /// </summary>
        public static int Count => _cache.Count;

        /// <summary>
        /// Initializes the cache: resolves the on-disk path and loads any previously-saved data.
        /// Must be called once during plugin startup before any metadata providers run.
        /// </summary>
        /// <param name="dataPath">The Jellyfin data directory (<c>IApplicationPaths.DataPath</c>).</param>
        /// <param name="logger">Logger for diagnostics.</param>
        public static void Initialize(string dataPath, ILogger logger)
        {
            _logger = logger;
            _cacheFilePath = Path.Combine(dataPath, "encora_collection_cache.json");
            LoadFromDisk();
        }

        /// <summary>
        /// Returns true if the cache is empty or older than <paramref name="maxAgeHours"/>.
        /// </summary>
        /// <param name="maxAgeHours">Maximum age in hours before the cache is considered stale.</param>
        /// <returns><see langword="true"/> if the cache needs refreshing.</returns>
        public static bool IsStale(int maxAgeHours = 24) =>
            _cache.IsEmpty || DateTime.UtcNow - _lastFetched > TimeSpan.FromHours(maxAgeHours);

        /// <summary>
        /// Looks up a recording by its Encora ID. Returns <see langword="true"/> and sets
        /// <paramref name="recording"/> on a cache hit; returns <see langword="false"/> on a miss
        /// (recording not in the user's collection — a live API call is then appropriate).
        /// </summary>
        /// <param name="encoraId">The Encora recording ID to look up.</param>
        /// <param name="recording">The cached recording if found; otherwise <see langword="null"/>.</param>
        /// <returns><see langword="true"/> if the recording is in the cache.</returns>
        public static bool TryGet(string encoraId, out EncoraRecording? recording) =>
            _cache.TryGetValue(encoraId, out recording);

        /// <summary>
        /// Replaces the entire cache with the supplied recordings and persists to disk.
        /// Called by <see cref="ScheduledTasks.EncoraCollectionCacheRefreshTask"/> after a
        /// successful full fetch of the user's Encora collection.
        /// </summary>
        /// <param name="recordings">The recordings to populate the cache with.</param>
        public static void Populate(IEnumerable<EncoraRecording> recordings)
        {
            _cache.Clear();
            var count = 0;
            foreach (var rec in recordings)
            {
                _cache[rec.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)] = rec;
                count++;
            }

            _lastFetched = DateTime.UtcNow;
            _logger?.LogInformation("[Encora] Collection cache populated: {Count} recording(s)", count);
            SaveToDisk();
        }

        private static void LoadFromDisk()
        {
            if (string.IsNullOrWhiteSpace(_cacheFilePath) || !File.Exists(_cacheFilePath))
            {
                _logger?.LogInformation("[Encora] No collection cache on disk yet");
                return;
            }

            try
            {
                var json = File.ReadAllText(_cacheFilePath);
                var payload = JsonSerializer.Deserialize<CachePayload>(json, _jsonOptions);
                if (payload?.Recordings == null)
                {
                    return;
                }

                _cache.Clear();
                foreach (var kvp in payload.Recordings)
                {
                    _cache[kvp.Key] = kvp.Value;
                }

                _lastFetched = payload.FetchedAt;
                _logger?.LogInformation(
                    "[Encora] Loaded collection cache from disk: {Count} recording(s), fetched {FetchedAt:u}",
                    _cache.Count,
                    _lastFetched);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[Encora] Failed to load collection cache from disk — will re-fetch");
            }
        }

        private static void SaveToDisk()
        {
            if (string.IsNullOrWhiteSpace(_cacheFilePath))
            {
                return;
            }

            try
            {
                var payload = new CachePayload
                {
                    FetchedAt = _lastFetched,
                    Recordings = new Dictionary<string, EncoraRecording>(_cache),
                };

                var json = JsonSerializer.Serialize(payload, _jsonOptions);
                File.WriteAllText(_cacheFilePath, json);
                _logger?.LogDebug("[Encora] Collection cache saved to disk ({Count} recordings)", _cache.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[Encora] Failed to save collection cache to disk");
            }
        }

        private sealed class CachePayload
        {
            [JsonPropertyName("fetchedAt")]
            public DateTime FetchedAt { get; set; }

            [JsonPropertyName("recordings")]
            public Dictionary<string, EncoraRecording>? Recordings { get; set; }
        }
    }
}
