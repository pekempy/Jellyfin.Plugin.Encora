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
        private const string BackupName = "thumb.original.png";
        private const string ThumbName = "thumb.png";
        private const string StateName = "thumb.overlay-state.json";

        private static readonly JsonSerializerOptions JsonOpts =
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

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
        /// avatar stack onto thumb.png, and writes a <c>thumb.overlay-state.json</c> sidecar.
        /// No-ops when no sources have any image or when thumb.png does not exist yet.
        /// </summary>
        /// <param name="logger">Logger for diagnostics.</param>
        /// <param name="episodeDir">Directory containing the episode's thumb.png.</param>
        /// <param name="sources">Ordered headshot sources (local-first, URL fallback).</param>
        /// <param name="httpClientFactory">Used only when a source has no local path.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous overlay operation.</returns>
        public static async Task OverlayAsync(
            ILogger logger,
            string episodeDir,
            IReadOnlyList<HeadshotSource> sources,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken)
        {
            var valid = sources.Where(s => s.HasImage).ToList();
            if (valid.Count == 0)
            {
                return;
            }

            var thumbPath = Path.Combine(episodeDir, ThumbName);
            var backupPath = Path.Combine(episodeDir, BackupName);

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

                if (bmp == null && src.RemoteUrl != null)
                {
                    try
                    {
                        var bytes = await http.GetByteArrayAsync(new Uri(src.RemoteUrl), cancellationToken).ConfigureAwait(false);
                        bmp = SKBitmap.Decode(bytes);
                        if (bmp != null)
                        {
                            logger.LogDebug("[Encora] [CastOverlay] Used StageMedia fallback for {Name}", src.PerformerName);
                        }
                    }
                    catch (Exception ex)
                    {
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
                await SaveOverlayStateAsync(episodeDir, cancellationToken).ConfigureAwait(false);
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
        /// Returns true when <c>thumb.overlay-state.json</c> is absent or older than
        /// <paramref name="staleness"/>, indicating a StageMedia re-check is due.
        /// </summary>
        /// <param name="episodeDir">Episode directory to check.</param>
        /// <param name="staleness">Maximum age before state is considered stale.</param>
        /// <returns>True when a re-check is warranted.</returns>
        public static bool IsOverlayStale(string episodeDir, TimeSpan staleness)
        {
            var statePath = Path.Combine(episodeDir, StateName);
            if (!File.Exists(statePath))
            {
                return true;
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
        /// Downloads the headshot at <paramref name="performerUrl"/> and compares its SHA-256
        /// against the locally cached file at <paramref name="localPath"/>.
        /// </summary>
        /// <param name="http">HTTP client for the download.</param>
        /// <param name="localPath">Path to the currently cached local image.</param>
        /// <param name="performerUrl">Current StageMedia URL for this performer.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The downloaded bytes when the remote image differs; null when unchanged or on error.</returns>
        public static async Task<byte[]?> FetchIfChangedAsync(
            HttpClient http,
            string localPath,
            string performerUrl,
            CancellationToken cancellationToken)
        {
            try
            {
                var remoteBytes = await http.GetByteArrayAsync(new Uri(performerUrl), cancellationToken).ConfigureAwait(false);
                var remoteHash = SHA256.HashData(remoteBytes);
                var localHash = SHA256.HashData(await File.ReadAllBytesAsync(localPath, cancellationToken).ConfigureAwait(false));
                return remoteHash.SequenceEqual(localHash) ? null : remoteBytes;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Writes or updates the overlay state sidecar with the current UTC timestamp.</summary>
        /// <param name="episodeDir">Episode directory to write the sidecar into.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous write.</returns>
        public static async Task SaveOverlayStateAsync(string episodeDir, CancellationToken cancellationToken)
        {
            var statePath = Path.Combine(episodeDir, StateName);
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

                    var avatarSize = Math.Clamp((int)(sourceBitmap.Height * 0.08), 48, 96);
                    var borderWidth = Math.Max(2, avatarSize / 20);
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
                        canvas.DrawBitmap(headshot, SKRect.Create(cx - r, cy - r, avatarSize, avatarSize));
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
