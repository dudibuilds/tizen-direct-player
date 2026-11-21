using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.App
{
    /// IKeyValueStore over files: one UTF-8 file per key under a root directory (TV: DirectoryInfo.Data/store).
    /// Keys may contain '/' to group documents ("playback/session" -> playback/session.json); each segment is
    /// limited to letters, digits, '-', '_' and '.', and may not start with '.', so a key can never leave the root.
    /// Writes are atomic: the value goes to a temp file in the same directory, is flushed to disk, then renamed over
    /// the old file. A reader sees the old value or the new one, never a partial file, even after a power cut.
    /// Writes to one key are serialized so a slow earlier write never lands after a newer one.
    public sealed class FileKeyValueStore : IKeyValueStore
    {
        private const string TempSuffix = ".tmp";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private readonly string root;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        public FileKeyValueStore(string root)
        {
            if (string.IsNullOrEmpty(root))
                throw new ArgumentException("A root directory is required.", nameof(root));
            this.root = Path.GetFullPath(root);
            Directory.CreateDirectory(this.root);
            SweepTempFiles();
        }

        public string Root => root;

        public async Task<string> ReadAsync(string key, CancellationToken token)
        {
            var path = PathFor(key);
            var gate = locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Off the caller's thread: the remote UI calls the store from the EFL main loop.
                return await Task.Run(() => File.Exists(path) ? File.ReadAllText(path, Utf8) : null).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        public async Task WriteAsync(string key, string value, CancellationToken token)
        {
            var path = PathFor(key);
            var gate = locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await Task.Run(() => WriteFile(path, value)).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        private static void WriteFile(string path, string value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + TempSuffix;
            try
            {
                var bytes = Utf8.GetBytes(value ?? "");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }
        }

        /// Maps a key to its file, rejecting anything that could escape the root.
        public string PathFor(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 200)
                throw new ArgumentException("Unsupported store key: " + key, nameof(key));
            var segments = key.Split('/');
            foreach (var segment in segments)
            {
                if (segment.Length == 0 || segment[0] == '.')
                    throw new ArgumentException("Unsupported store key: " + key, nameof(key));
                foreach (var c in segment)
                {
                    var allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.';
                    if (!allowed)
                        throw new ArgumentException("Unsupported store key: " + key, nameof(key));
                }
            }
            var path = Path.Combine(root, Path.Combine(segments) + ".json");
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("Unsupported store key: " + key, nameof(key));
            return full;
        }

        /// Temp files left by a write interrupted by a crash or power cut are never valid values.
        private void SweepTempFiles()
        {
            try
            {
                foreach (var file in Directory.GetFiles(root, "*" + TempSuffix, SearchOption.AllDirectories))
                    TryDelete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
