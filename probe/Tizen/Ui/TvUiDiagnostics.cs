using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Ru7100.TvUi
{
    /// Image pipeline counters for the TV, readable over the agent API (`ui` in AppPlatform.Diagnostics) and traced
    /// to shared/trusted/startup.log. Posters (downloaded art) and generated shape assets (rounded rectangles,
    /// corner masks, gradient ramps) are counted apart, so a report tells which of the two pipelines fails.
    ///
    /// The recorders run on the main loop and never touch the disk: poster sizes are read by the worker that
    /// fetched the file, asset sizes only when a snapshot is taken (on the API's thread).
    internal static class TvUiDiagnostics
    {
        private const int RecentLimit = 12;
        private static readonly object Gate = new object();
        private static readonly Queue<object> RecentPosters = new Queue<object>();
        private static readonly Dictionary<string, AssetState> Assets = new Dictionary<string, AssetState>();
        private static int loaded;
        private static int loadFailed;
        private static int downloadFailed;
        private static int assetsLoaded;
        private static int assetsFailed;
        private static string lastLoadError;
        private static string lastDownloadError;
        private static string lastAssetError;

        private sealed class AssetState
        {
            public string Path;
            public bool Ok;
            public int Width, Height;
        }

        /// Off the main loop only (snapshot, traces from workers).
        private static long SizeOf(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : -1;   // -1: the file is missing
            }
            catch (IOException)
            {
                return -2;
            }
        }

        private static void Remember(object item)
        {
            lock (Gate)
            {
                RecentPosters.Enqueue(item);
                while (RecentPosters.Count > RecentLimit)
                    RecentPosters.Dequeue();
            }
        }

        /// A poster, background, logo or still decoded by EFL. `key` is the layout node (e.g. "hero:bg:tt0903747");
        /// `bytes` is the file size read by the worker that fetched it.
        public static void ImageLoaded(string key, string path, int width, int height, long bytes)
        {
            Interlocked.Increment(ref loaded);
            Remember(new { key, ok = true, file = Path.GetFileName(path), bytes, width, height });
        }

        public static void ImageLoadFailed(string key, string path, long bytes)
        {
            var n = Interlocked.Increment(ref loadFailed);
            lastLoadError = "EFL could not decode " + path + " (" + bytes + " bytes, node " + key + ")";
            Remember(new { key, ok = false, file = Path.GetFileName(path), bytes, width = 0, height = 0 });
            if (n <= 5 || n % 50 == 0)
                Ru7100Probe.Program.Trace("ui: image load failed (" + n + "): " + lastLoadError);
        }

        /// A generated shape image (rounded rectangle, corner mask, gradient ramp) loaded from the ui-assets folder.
        public static void AssetLoaded(string path, int width, int height)
        {
            Interlocked.Increment(ref assetsLoaded);
            lock (Gate)
                Assets[Path.GetFileName(path)] = new AssetState { Path = path, Ok = true, Width = width, Height = height };
        }

        public static void AssetLoadFailed(string path)
        {
            var n = Interlocked.Increment(ref assetsFailed);
            lastAssetError = "EFL could not load generated asset " + path;
            lock (Gate)
                Assets[Path.GetFileName(path)] = new AssetState { Path = path, Ok = false };
            if (n <= 10)
                Ru7100Probe.Program.Trace("ui: asset load failed (" + n + "): " + lastAssetError);
        }

        public static void DownloadFailed(string message)
        {
            var n = Interlocked.Increment(ref downloadFailed);
            lastDownloadError = message;
            if (n <= 5 || n % 50 == 0)
                Ru7100Probe.Program.Trace("ui: poster download failed (" + n + "): " + message);
        }

        /// stacking: include the painter's stacking dump (waits for the main loop; dev.diagnostics {"full": true} only).
        public static object Snapshot(TvUiHost host, bool stacking = true)
        {
            // Copy under the lock, then release it before anything slow: the stacking dump waits up to 2 s for the
            // main loop, whose image callbacks take this same lock.
            object[] recent;
            KeyValuePair<string, AssetState>[] assets;
            lock (Gate)
            {
                recent = RecentPosters.ToArray();
                assets = Assets.ToArray();
            }
            return new
            {
                // Posters and other downloaded art.
                imagesLoaded = Volatile.Read(ref loaded),
                imageLoadFailures = Volatile.Read(ref loadFailed),
                lastImageLoadError = lastLoadError,
                downloadFailures = Volatile.Read(ref downloadFailed),
                lastDownloadError,
                recentImages = recent,
                posterCache = host?.PosterStats,
                // Generated shapes: a failure here means rounded cards fall back to square rectangles.
                generatedAssetsLoaded = Volatile.Read(ref assetsLoaded),
                generatedAssetFailures = Volatile.Read(ref assetsFailed),
                lastGeneratedAssetError = lastAssetError,
                generatedAssets = assets.ToDictionary(a => a.Key, a => (object)new { ok = a.Value.Ok, bytes = SizeOf(a.Value.Path), width = a.Value.Width, height = a.Value.Height }),
                assetDirectory = host?.AssetDirectory,
                trailerView = host?.TrailersSupported,
                trailer = (host?.Trailers as Ru7100Probe.Trailer.TizenTrailerPlayer)?.Diagnostics,
                devFlags = TvUiDevFlags.Describe(),
                // Every image and box in stacking order: geometry, colour as set, visibility, layer, inner image
                // state and the nearest object above that covers its centre; plus the probe strip (flag "probe").
                stacking = stacking ? host?.StackingDump() : null
            };
        }
    }
}
