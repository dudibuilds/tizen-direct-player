using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core.App;
using Xunit;

namespace Ru7100.Tests.App
{
    public sealed class FileKeyValueStoreTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ru7100-kv-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }

        [Fact]
        public async Task RoundTripsKeysWithSlashesAndReplacesValues()
        {
            var store = new FileKeyValueStore(root);
            Assert.Null(await store.ReadAsync("playback/session", CancellationToken.None));
            await store.WriteAsync("playback/session", "{\"a\":1}", CancellationToken.None);
            await store.WriteAsync("playback/session", "{\"a\":2}", CancellationToken.None);
            await store.WriteAsync("addons.installed", "[]", CancellationToken.None);
            Assert.Equal("{\"a\":2}", await store.ReadAsync("playback/session", CancellationToken.None));
            Assert.Equal("[]", await new FileKeyValueStore(root).ReadAsync("addons.installed", CancellationToken.None));
            Assert.True(File.Exists(Path.Combine(root, "playback", "session.json")));
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        }

        [Theory]
        [InlineData("../escape")]
        [InlineData("playback/../../escape")]
        [InlineData(".hidden")]
        [InlineData("a//b")]
        [InlineData("/absolute")]
        [InlineData("a\\b")]
        [InlineData("")]
        [InlineData("spaces are bad")]
        public async Task RejectsKeysThatCouldLeaveTheRoot(string key)
        {
            var store = new FileKeyValueStore(root);
            await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(key, "x", CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync(key, CancellationToken.None));
        }

        [Fact]
        public async Task ConcurrentWritersNeverExposeAPartialValue()
        {
            var store = new FileKeyValueStore(root);
            var values = Enumerable.Range(0, 24).Select(i => new string((char)('a' + i), 200_000 + i)).ToList();
            await store.WriteAsync("big", values[0], CancellationToken.None);
            var allowed = new HashSet<string>(values);
            // A second instance has its own per-key locks, like another process: only the atomic rename protects it.
            var other = new FileKeyValueStore(root);
            var stop = new CancellationTokenSource();
            var reads = 0;
            var reader = Task.Run(async () =>
            {
                do
                {
                    var seen = await other.ReadAsync("big", CancellationToken.None);
                    Assert.Contains(seen, allowed);
                    reads++;
                }
                while (!stop.IsCancellationRequested);
            });
            await Task.WhenAll(values.Select(v => Task.Run(() => store.WriteAsync("big", v, CancellationToken.None))));
            stop.Cancel();
            await reader;
            Assert.True(reads > 0);
            Assert.Contains(await store.ReadAsync("big", CancellationToken.None), allowed);
            Assert.Empty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        }

        [Fact]
        public async Task TempFileLeftByACrashIsIgnoredAndSwept()
        {
            var store = new FileKeyValueStore(root);
            await store.WriteAsync("playback/queue", "good", CancellationToken.None);
            var orphan = Path.Combine(root, "playback", "queue.json.0123456789abcdef.tmp");
            File.WriteAllText(orphan, "half-writ");
            var reopened = new FileKeyValueStore(root);
            Assert.Equal("good", await reopened.ReadAsync("playback/queue", CancellationToken.None));
            Assert.False(File.Exists(orphan));
        }
    }
}
