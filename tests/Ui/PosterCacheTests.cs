using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    /// The poster cache's UI-thread contract: lookups are answered from memory, eviction and file-time writes run on
    /// a worker without holding the lookup lock, and prefetch never takes the slots visible art needs.
    public class PosterCacheTests : IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "ru7100-poster-cache-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { }
        }

        private sealed class Handler : HttpMessageHandler
        {
            public int Calls;
            public int Active, MaxActive;
            /// When set, every request waits for it (to hold download slots).
            public TaskCompletionSource<bool> Gate;
            public int Length = 1000;
            public readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> InFlight = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Interlocked.Increment(ref Calls);
                InFlight[request.RequestUri.ToString()] = true;
                var now = Interlocked.Increment(ref Active);
                lock (this) MaxActive = Math.Max(MaxActive, now);
                try
                {
                    if (Gate != null)
                        await Gate.Task.ConfigureAwait(false);
                    var bytes = new byte[Length];
                    bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                }
                finally
                {
                    Interlocked.Decrement(ref Active);
                }
            }
        }

        [Fact]
        public void AHitIsAnsweredFromMemoryWithoutTouchingTheDisk()
        {
            var handler = new Handler();
            var cache = new PosterCache(dir, handler);
            var path = cache.GetAsync("https://img/a.jpg", CancellationToken.None).Result;
            Assert.True(File.Exists(path));

            // Removed behind the cache's back: the index still answers (no file probe on the calling thread)...
            File.Delete(path);
            var hit = cache.GetAsync("https://img/a.jpg", CancellationToken.None);
            Assert.True(hit.IsCompleted);
            Assert.Equal(path, hit.Result);
            Assert.Equal(path, cache.PathFor("https://img/a.jpg"));
            Assert.Equal(1, handler.Calls);

            // ...until the painter reports the file as unusable; then the next request downloads it again.
            cache.Invalidate("https://img/a.jpg");
            Assert.Null(cache.PathFor("https://img/a.jpg"));
            Assert.Equal(path, cache.GetAsync("https://img/a.jpg", CancellationToken.None).Result);
            Assert.Equal(2, handler.Calls);
            Assert.True(File.Exists(path));
        }

        [Fact]
        public void EvictionRunsOnAWorkerAndNeverBlocksLookups()
        {
            var handler = new Handler();
            var cache = new PosterCache(dir, handler, maxBytes: 2500, maxFiles: 10);
            var deleting = new ManualResetEventSlim();
            var release = new ManualResetEventSlim();
            var deleter = -1;
            cache.DeleteFile = file =>
            {
                deleter = Thread.CurrentThread.ManagedThreadId;
                deleting.Set();
                release.Wait(TimeSpan.FromSeconds(10));
                File.Delete(file);
            };
            var a = cache.GetAsync("https://img/a.jpg", CancellationToken.None).Result;
            var b = cache.GetAsync("https://img/b.jpg", CancellationToken.None).Result;
            cache.GetAsync("https://img/c.jpg", CancellationToken.None).Wait();   // 3 x 1000 bytes > 2500: `a` goes
            Assert.True(deleting.Wait(TimeSpan.FromSeconds(10)), "eviction did not start");
            try
            {
                // Eviction is stuck deleting `a`: lookups (hit, miss bookkeeping, PathFor) still answer at once.
                Assert.NotEqual(Thread.CurrentThread.ManagedThreadId, deleter);
                var hit = cache.GetAsync("https://img/b.jpg", CancellationToken.None);
                Assert.True(hit.IsCompleted);
                Assert.Equal(b, hit.Result);
                Assert.Null(cache.PathFor("https://img/a.jpg"));   // already out of the index
                Assert.Equal(2, cache.FileCount);
                Assert.True(File.Exists(a));                        // the file itself waits for the worker
            }
            finally
            {
                release.Set();
            }
            cache.FlushAsync().Wait();
            Assert.False(File.Exists(a));
            Assert.Equal(2000, cache.TotalBytes);
        }

        [Fact]
        public void RecencyIsWrittenBackSoTheOrderSurvivesARestart()
        {
            var handler = new Handler();
            var cache = new PosterCache(dir, handler, maxBytes: 10_000, maxFiles: 2);
            var a = cache.GetAsync("https://img/a.jpg", CancellationToken.None).Result;
            var b = cache.GetAsync("https://img/b.jpg", CancellationToken.None).Result;
            File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddHours(-1));
            // `a` is used again: only in memory now, written back in a batch by the worker.
            for (var i = 0; i < 40; i++)
                cache.GetAsync(i % 2 == 0 ? "https://img/a.jpg" : "https://img/b.jpg", CancellationToken.None).Wait();
            cache.GetAsync("https://img/a.jpg", CancellationToken.None).Wait();
            cache.FlushAsync().Wait();
            Assert.True(File.GetLastWriteTimeUtc(a) > DateTime.UtcNow.AddMinutes(-5));

            // A new start loads the index on a worker; the first request waits for it instead of downloading again.
            var restarted = new PosterCache(dir, handler, maxBytes: 10_000, maxFiles: 2, migrateInBackground: true);
            Assert.Equal(a, restarted.GetAsync("https://img/a.jpg", CancellationToken.None).Result);
            Assert.Equal(2, handler.Calls);
            Assert.True(restarted.IndexReady);
        }

        [Fact]
        public void LogoCopiesCountAgainstTheBudgetAndLeaveWithTheirSource()
        {
            var handler = new Handler();
            var cache = new PosterCache(dir, handler, maxBytes: 2500, maxFiles: 10);
            var a = cache.GetAsync("https://img/a.png", CancellationToken.None).Result;
            var copy = a + ".fit320x120.png";
            File.WriteAllBytes(copy, new byte[800]);
            cache.AttachDerived(a, copy, 800);
            Assert.Equal(1800, cache.TotalBytes);
            cache.GetAsync("https://img/b.jpg", CancellationToken.None).Wait();   // 2800 > 2500: `a` and its copy go
            cache.FlushAsync().Wait();
            Assert.False(File.Exists(a));
            Assert.False(File.Exists(copy));
            Assert.Equal(1000, cache.TotalBytes);
        }

        [Fact]
        public void PrefetchNeverTakesTheSlotsVisibleArtNeeds()
        {
            var handler = new Handler { Gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously) };
            var cache = new PosterCache(dir, handler);
            var speculative = new Task<string>[5];
            for (var i = 0; i < speculative.Length; i++)
                speculative[i] = cache.GetAsync("https://img/s" + i + ".jpg", CancellationToken.None, speculative: true);
            SpinWait.SpinUntil(() => Volatile.Read(ref handler.Active) >= 2, TimeSpan.FromSeconds(5));
            Thread.Sleep(100);
            Assert.Equal(2, Volatile.Read(ref handler.Active));   // prefetch holds at most half the slots
            var visible = cache.GetAsync("https://img/v.jpg", CancellationToken.None);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref handler.Active) == 3, TimeSpan.FromSeconds(5)), "visible art waited behind prefetch");
            // A card scrolls into view whose art is still queued as prefetch: the painter's request promotes it.
            var queued = Enumerable.Range(0, speculative.Length).Select(i => "https://img/s" + i + ".jpg").First(u => !handler.InFlight.ContainsKey(u));
            var promoted = cache.GetAsync(queued, CancellationToken.None);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref handler.Active) == 4, TimeSpan.FromSeconds(5)), "visible art joined a queued prefetch and kept waiting");
            Assert.True(handler.InFlight.ContainsKey(queued));
            handler.Gate.SetResult(true);
            Assert.NotNull(promoted.Result);
            Assert.NotNull(visible.Result);
            Task.WaitAll(speculative);
            Assert.Equal(6, handler.Calls);
        }
    }
}
