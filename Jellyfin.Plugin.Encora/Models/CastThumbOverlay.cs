using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.Encora.Models
{
    /// <summary>
    /// Composites cast headshots as an overlapping avatar stack onto a generated thumb.png.
    /// Reads from Jellyfin's own local people-image cache first; falls back to a StageMedia
    /// download when no local image is available yet. The original thumb is preserved as
    /// thumb.original.png so re-runs always composite onto the clean source.
    /// </summary>
    public static class CastThumbOverlay
    {
        private const int MaxAvatars = 6;
        private static readonly JsonSerializerOptions JsonOpts =
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        /// <summary>Returns the episode-specific thumb filename for <paramref name="mediaPath"/>.</summary>
        /// <param name="mediaPath">Full path to the video file.</param>
        /// <returns>Filename like <c>Episode Title-thumb.png</c>.</returns>
        public static string GetThumbFileName(string mediaPath)
            => Path.GetFileNameWithoutExtension(mediaPath) + "-thumb.png";

        /// <summary>Returns the backup filename for the clean original thumb.</summary>
        /// <param name="mediaPath">Full path to the video file.</param>
        /// <returns>Filename like <c>Episode Title-thumb.original.png</c>.</returns>
        public static string GetBackupFileName(string mediaPath)
            => Path.GetFileNameWithoutExtension(mediaPath) + "-thumb.original.png";

        /// <summary>Returns the state filename for <paramref name="mediaPath"/>.</summary>
        /// <param name="mediaPath">Full path to the video file.</param>
        /// <returns>Filename like <c>Episode Title-thumb.overlay-state.json</c>.</returns>
        public static string GetStateFileName(string mediaPath)
            => Path.GetFileNameWithoutExtension(mediaPath) + "-thumb.overlay-state.json";

        // ── Public API ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds an ordered list of headshot sources for up to <see cref="MaxAvatars"/> cast members.
        /// Each entry prefers the locally cached Jellyfin person image; the corresponding StageMedia
        /// URL is included as a fallback when no local image is cached yet.
        /// </summary>
        /// <param name="cast">Recording cast list.</param>
        /// <param name="libraryManager">Jellyfin library manager for local-cache lookups.</param>
        /// <param name="headshotFallback">StageMedia performers, used when local cache is absent.</param>
        /// <returns>Ordered sources, at most <see cref="MaxAvatars"/> entries.</returns>
        public static IReadOnlyList<HeadshotSource> GetHeadshotSources(
            IEnumerable<EncoraCastMember>? cast,
            ILibraryManager libraryManager,
            Collection<StageMediaPerformer>? headshotFallback)
        {
            if (cast == null)
            {
                return Array.Empty<HeadshotSource>();
            }

            var sources = new List<HeadshotSource>();
            foreach (var member in cast)
            {
                if (sources.Count >= MaxAvatars)
                {
                    break;
                }

                var name = member.Performer?.Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                string? localPath = null;
                var person = libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Person },
                    Name = name,
                    Limit = 1,
                }).OfType<Person>().FirstOrDefault();

                if (person != null)
                {
                    var info = person.GetImageInfo(ImageType.Primary, 0);
                    if (info?.Path != null && File.Exists(info.Path))
                    {
                        localPath = info.Path;
                    }
                }

                string? remoteUrl = null;
                if (localPath == null && headshotFallback != null && (member.Performer?.Id ?? 0) > 0)
                {
                    var pid = member.Performer!.Id;
                    remoteUrl = headshotFallback.FirstOrDefault(p => p.Id == pid)?.Url;
                }

                if (localPath != null || !string.IsNullOrWhiteSpace(remoteUrl))
                {
                    sources.Add(new HeadshotSource
                    {
                        PerformerName = name,
                        LocalPath = localPath,
                        RemoteUrl = string.IsNullOrWhiteSpace(remoteUrl) ? null : remoteUrl,
                    });
                }
            }

            return sources;
        }

        /// <summary>
        /// Loads headshot images (local file or HTTP fallback), composites them as a circular
        /// avatar stack onto the episode's thumbnail, and writes a state sidecar.
        /// No-ops when no sources have any image or when the thumb does not exist yet.
        /// </summary>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="episodeDir">Directory containing the episode's thumb.</param>
        /// <param name="mediaPath">Path to the video file.</param>
        /// <param name="sources">Ordered headshot sources (local-first, URL fallback).</param>
        /// <param name="httpClientFactory">Used only when a source has no local path.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous overlay operation.</returns>
        public static async Task OverlayAsync(
            ILogger logger,
            string episodeDir,
            string mediaPath,
            IReadOnlyList<HeadshotSource> sources,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken)
        {
            var valid = sources.Where(s => s.HasImage).ToList();
            if (valid.Count == 0)
            {
                return;
            }

            var thumbName = GetThumbFileName(mediaPath);
            var backupName = GetBackupFileName(mediaPath);
            var stateName = GetStateFileName(mediaPath);

            var thumbPath = Path.Combine(episodeDir, thumbName);
            var backupPath = Path.Combine(episodeDir, backupName);
            var statePath = Path.Combine(episodeDir, stateName);

            // Migrate legacy files if new ones do not exist yet
            var legacyThumb = Path.Combine(episodeDir, "thumb.png");
            var legacyBackup = Path.Combine(episodeDir, "thumb.original.png");
            var legacyState = Path.Combine(episodeDir, "thumb.overlay-state.json");

            if (!File.Exists(thumbPath) && File.Exists(legacyThumb))
            {
                try
                {
                    File.Move(legacyThumb, thumbPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Encora] [CastOverlay] Could not migrate legacy thumb.png to {Path}", thumbPath);
                }
            }

            if (!File.Exists(backupPath) && File.Exists(legacyBackup))
            {
                try
                {
                    File.Move(legacyBackup, backupPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Encora] [CastOverlay] Could not migrate legacy thumb.original.png to {Path}", backupPath);
                }
            }

            if (!File.Exists(statePath) && File.Exists(legacyState))
            {
                try
                {
                    File.Move(legacyState, statePath, overwrite: true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Encora] [CastOverlay] Could not migrate legacy state to {Path}", statePath);
                }
            }

            // Remove legacy files from folder so Jellyfin never treats them as Season art
            if (File.Exists(legacyThumb))
            {
                try
                {
                    File.Delete(legacyThumb);
                }
                catch
                {
                }
            }

            if (File.Exists(legacyBackup))
            {
                try
                {
                    File.Delete(legacyBackup);
                }
                catch
                {
                }
            }

            if (File.Exists(legacyState))
            {
                try
                {
                    File.Delete(legacyState);
                }
                catch
                {
                }
            }

            // Remove any {name}-thumb.jpg so Jellyfin doesn't prefer it over {name}-thumb.png
            var legacyJpg = Path.Combine(episodeDir, Path.GetFileNameWithoutExtension(mediaPath) + "-thumb.jpg");
            if (File.Exists(legacyJpg))
            {
                try
                {
                    File.Delete(legacyJpg);
                }
                catch
                {
                }
            }

            if (!File.Exists(thumbPath) && !File.Exists(backupPath))
            {
                return;
            }

            if (!File.Exists(backupPath) && File.Exists(thumbPath))
            {
                try
                {
                    File.Copy(thumbPath, backupPath, overwrite: false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Encora] [CastOverlay] Could not back up thumb to {Path}", backupPath);
                    return;
                }
            }

            var sourcePath = File.Exists(backupPath) ? backupPath : thumbPath;
            var http = httpClientFactory.CreateClient();
            var bitmaps = new List<SKBitmap>();

            foreach (var src in valid)
            {
                SKBitmap? bmp = null;
                if (src.LocalPath != null)
                {
                    try
                    {
                        bmp = SKBitmap.Decode(src.LocalPath);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "[Encora] [CastOverlay] Could not decode local headshot for {Name}", src.PerformerName);
                    }
                }

                if (bmp == null && src.RemoteUrl != null && StageMediaCircuitBreaker.IsAvailable(logger))
                {
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(TimeSpan.FromSeconds(15));
                        var bytes = await http.GetByteArrayAsync(new Uri(src.RemoteUrl), cts.Token).ConfigureAwait(false);
                        bmp = SKBitmap.Decode(bytes);
                        if (bmp != null)
                        {
                            logger.LogDebug("[Encora] [CastOverlay] Used StageMedia fallback for {Name}", src.PerformerName);
                        }
                    }
                    catch (Exception ex)
                    {
                        StageMediaCircuitBreaker.RecordException(logger, ex);
                        logger.LogDebug(ex, "[Encora] [CastOverlay] StageMedia fallback failed for {Name}", src.PerformerName);
                    }
                }

                if (bmp != null)
                {
                    bitmaps.Add(bmp);
                }
            }

            if (bitmaps.Count == 0)
            {
                return;
            }

            try
            {
                await CompositeAsync(logger, sourcePath, thumbPath, bitmaps, cancellationToken).ConfigureAwait(false);
                await SaveOverlayStateAsync(episodeDir, mediaPath, cancellationToken).ConfigureAwait(false);
                logger.LogInformation(
                    "[Encora] [CastOverlay] ✅ Overlaid {Count} cast avatars onto {Path}",
                    bitmaps.Count,
                    thumbPath);
            }
            finally
            {
                foreach (var bmp in bitmaps)
                {
                    bmp.Dispose();
                }
            }
        }

        /// <summary>
        /// Backward-compatible overload when mediaPath is omitted.
        /// </summary>
        /// <param name="logger">Logger instance.</param>
        /// <param name="episodeDir">Directory containing the episode's thumb.</param>
        /// <param name="sources">Ordered headshot sources.</param>
        /// <param name="httpClientFactory">HTTP client factory.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public static Task OverlayAsync(
            ILogger logger,
            string episodeDir,
            IReadOnlyList<HeadshotSource> sources,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken)
        {
            var videoFile = Directory.EnumerateFiles(episodeDir, "*.*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) &&
                                     !f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
                                     !f.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase) &&
                                     !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) ?? Path.Combine(episodeDir, "thumb.png");
            return OverlayAsync(logger, episodeDir, videoFile, sources, httpClientFactory, cancellationToken);
        }

        /// <summary>
        /// Returns true when state sidecar is absent or older than
        /// <paramref name="staleness"/>, indicating a StageMedia re-check is due.
        /// </summary>
        /// <param name="episodeDir">Episode directory to check.</param>
        /// <param name="mediaPath">Media path to check state for.</param>
        /// <param name="staleness">Maximum age before state is considered stale.</param>
        /// <returns>True when a re-check is warranted.</returns>
        public static bool IsOverlayStale(string episodeDir, string mediaPath, TimeSpan staleness)
        {
            var statePath = Path.Combine(episodeDir, GetStateFileName(mediaPath));
            if (!File.Exists(statePath))
            {
                var legacyState = Path.Combine(episodeDir, "thumb.overlay-state.json");
                if (File.Exists(legacyState))
                {
                    statePath = legacyState;
                }
                else
                {
                    return true;
                }
            }

            try
            {
                var json = File.ReadAllText(statePath);
                var state = JsonSerializer.Deserialize<OverlayState>(json, JsonOpts);
                return state == null || (DateTime.UtcNow - state.LastCheckedUtc) > staleness;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Returns true when <c>thumb.overlay-state.json</c> or any state sidecar is absent or older than
        /// <paramref name="staleness"/>, indicating a StageMedia re-check is due.
        /// </summary>
        /// <param name="episodeDir">Episode directory to check.</param>
        /// <param name="staleness">Maximum age before state is considered stale.</param>
        /// <returns>True when a re-check is warranted.</returns>
        public static bool IsOverlayStale(string episodeDir, TimeSpan staleness)
        {
            var stateFiles = Directory.EnumerateFiles(episodeDir, "*-thumb.overlay-state.json").ToList();
            if (stateFiles.Count > 0)
            {
                return stateFiles.Any(f =>
                {
                    try
                    {
                        var json = File.ReadAllText(f);
                        var state = JsonSerializer.Deserialize<OverlayState>(json, JsonOpts);
                        return state == null || (DateTime.UtcNow - state.LastCheckedUtc) > staleness;
                    }
                    catch
                    {
                        return true;
                    }
                });
            }

            var legacyState = Path.Combine(episodeDir, "thumb.overlay-state.json");
            if (!File.Exists(legacyState))
            {
                return true;
            }

            try
            {
                var json = File.ReadAllText(legacyState);
                var state = JsonSerializer.Deserialize<OverlayState>(json, JsonOpts);
                return state == null || (DateTime.UtcNow - state.LastCheckedUtc) > staleness;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Downloads the headshot at <paramref name="performerUrl"/> and compares its SHA-256
        /// against the locally cached file at <paramref name="localPath"/>.
        /// </summary>
        /// <param name="http">HTTP client for the download.</param>
        /// <param name="localPath">Path to the currently cached local image.</param>
        /// <param name="performerUrl">Current StageMedia URL for this performer.</param>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The downloaded bytes when the remote image differs; null when unchanged or on error.</returns>
        public static async Task<byte[]?> FetchIfChangedAsync(
            HttpClient http,
            string localPath,
            string performerUrl,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                var remoteBytes = await http.GetByteArrayAsync(new Uri(performerUrl), cts.Token).ConfigureAwait(false);
                var remoteHash = SHA256.HashData(remoteBytes);
                var localHash = SHA256.HashData(await File.ReadAllBytesAsync(localPath, cancellationToken).ConfigureAwait(false));
                return remoteHash.SequenceEqual(localHash) ? null : remoteBytes;
            }
            catch (Exception ex)
            {
                StageMediaCircuitBreaker.RecordException(logger, ex);
                return null;
            }
        }

        /// <summary>Backward compatible overload without logger.</summary>
        /// <param name="http">HTTP client for the download.</param>
        /// <param name="localPath">Path to the currently cached local image.</param>
        /// <param name="performerUrl">Current StageMedia URL for this performer.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The downloaded bytes when the remote image differs; null when unchanged or on error.</returns>
        public static Task<byte[]?> FetchIfChangedAsync(
            HttpClient http,
            string localPath,
            string performerUrl,
            CancellationToken cancellationToken)
        {
            return FetchIfChangedAsync(http, localPath, performerUrl, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, cancellationToken);
        }

        /// <summary>Writes or updates the overlay state sidecar with the current UTC timestamp.</summary>
        /// <param name="episodeDir">Episode directory to write the sidecar into.</param>
        /// <param name="mediaPath">Media path to write sidecar for.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous write.</returns>
        public static async Task SaveOverlayStateAsync(string episodeDir, string mediaPath, CancellationToken cancellationToken)
        {
            var statePath = Path.Combine(episodeDir, GetStateFileName(mediaPath));
            var state = new OverlayState { LastCheckedUtc = DateTime.UtcNow };
            var json = JsonSerializer.Serialize(state, JsonOpts);
            await File.WriteAllTextAsync(statePath, json, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Writes or updates the legacy overlay state sidecar with the current UTC timestamp.</summary>
        /// <param name="episodeDir">Episode directory to write the sidecar into.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous write.</returns>
        public static async Task SaveOverlayStateAsync(string episodeDir, CancellationToken cancellationToken)
        {
            var statePath = Path.Combine(episodeDir, "thumb.overlay-state.json");
            var state = new OverlayState { LastCheckedUtc = DateTime.UtcNow };
            var json = JsonSerializer.Serialize(state, JsonOpts);
            await File.WriteAllTextAsync(statePath, json, cancellationToken).ConfigureAwait(false);
        }

        // ── Private helpers ─────────────────────────────────────────────────────────

        private static Task CompositeAsync(
            ILogger logger,
            string sourcePath,
            string destPath,
            List<SKBitmap> headshots,
            CancellationToken cancellationToken)
        {
            return Task.Run(
                () =>
                {
                    var sourceBytes = File.ReadAllBytes(sourcePath);
                    using var sourceBitmap = SKBitmap.Decode(sourceBytes);
                    if (sourceBitmap == null)
                    {
                        logger.LogWarning("[Encora] [CastOverlay] Could not decode source image {Path}", sourcePath);
                        return;
                    }

                    var avatarSize = Math.Clamp((int)(sourceBitmap.Height * 0.16), 96, 192);
                    var borderWidth = Math.Max(3, avatarSize / 20);
                    var step = (int)(avatarSize * 0.70);
                    var marginX = avatarSize / 2;
                    var marginY = avatarSize / 2;

                    var imageInfo = new SKImageInfo(
                        sourceBitmap.Width,
                        sourceBitmap.Height,
                        SKColorType.Rgba8888,
                        SKAlphaType.Premul);

                    using var surface = SKSurface.Create(imageInfo);
                    var canvas = surface.Canvas;
                    canvas.DrawBitmap(sourceBitmap, 0, 0);

                    var totalWidth = avatarSize + ((headshots.Count - 1) * step) + (borderWidth * 2);
                    using var shadowPaint = new SKPaint
                    {
                        Color = new SKColor(0, 0, 0, 100),
                        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, avatarSize * 0.3f),
                        IsAntialias = true,
                    };

                    float shadowPad = avatarSize * 0.15f;
                    var shadowRect = SKRect.Create(
                        marginX - borderWidth - shadowPad,
                        sourceBitmap.Height - marginY - avatarSize - borderWidth - shadowPad,
                        totalWidth + (shadowPad * 2f),
                        (avatarSize + (borderWidth * 2)) + (shadowPad * 2f));
                    canvas.DrawRoundRect(new SKRoundRect(shadowRect, avatarSize / 2f), shadowPaint);

                    for (int i = headshots.Count - 1; i >= 0; i--)
                    {
                        var headshot = headshots[i];
                        float cx = marginX + (i * step) + (avatarSize / 2f);
                        float cy = sourceBitmap.Height - marginY - (avatarSize / 2f);
                        float r = avatarSize / 2f;

                        using var borderPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
                        canvas.DrawCircle(cx, cy, r + borderWidth, borderPaint);

                        canvas.Save();
                        using var clipPath = new SKPath();
                        clipPath.AddCircle(cx, cy, r);
                        canvas.ClipPath(clipPath, SKClipOperation.Intersect, antialias: true);
                        // Cover: scale to fill the circle, centered, maintaining aspect ratio
                        float scaleX = avatarSize / (float)headshot.Width;
                        float scaleY = avatarSize / (float)headshot.Height;
                        float scale = Math.Max(scaleX, scaleY);
                        float scaledW = headshot.Width * scale;
                        float scaledH = headshot.Height * scale;
                        float offsetX = (avatarSize - scaledW) / 2f;
                        float offsetY = (avatarSize - scaledH) / 2f;
                        canvas.DrawBitmap(headshot, SKRect.Create(cx - r + offsetX, cy - r + offsetY, scaledW, scaledH));
                        canvas.Restore();
                    }

                    using var image = surface.Snapshot();
                    using var encoded = image.Encode(SKEncodedImageFormat.Png, 95);
                    File.WriteAllBytes(destPath, encoded.ToArray());
                },
                cancellationToken);
        }
    }
}
