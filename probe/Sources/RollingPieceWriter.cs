using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.PieceWriter;
using ReusableTasks;

namespace Ru7100Probe
{
    /// Bounded storage for streaming torrents on the TV, whose internal storage is too small for a whole movie.
    ///
    /// MonoTorrent's default writer stores every downloaded byte at its file offset, so a 6 GB movie needs 6 GB.
    /// This writer keeps each registered torrent in ONE cache file of fixed size, cut into piece-sized slots, and
    /// maps torrent pieces to slots on demand. When the cache is full it evicts the piece that matters least for
    /// playback (already watched, far behind the playhead; else furthest ahead), and clears that piece from the
    /// torrent's "have" bitfields so MonoTorrent downloads it again if the player seeks back to it.
    ///
    /// The playhead is the global byte offset the player last read (set through <see cref="Cache.ReadPosition"/>).
    /// The first and last pieces of the file being played are pinned: MKV/MP4 keep their index there.
    ///
    /// Torrents that are not registered (by download directory) go to the fallback writer unchanged.
    internal sealed class RollingPieceWriter : IPieceWriter
    {
        private readonly IPieceWriter fallback;
        private readonly Action<string> log;
        private readonly List<Cache> caches = new List<Cache>();

        public RollingPieceWriter(IPieceWriter fallback, Action<string> log = null)
        {
            this.fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
            this.log = log;
        }

        public int OpenFiles => fallback.OpenFiles;
        public int MaximumOpenFiles => fallback.MaximumOpenFiles;

        /// Routes every file under <paramref name="directory"/> to a bounded cache file at <paramref name="cachePath"/>.
        /// The torrent's piece geometry is read from <paramref name="manager"/> when the first block arrives (after metadata).
        public Cache Register(string directory, TorrentManager manager, string cachePath, long capacityBytes)
        {
            var cache = new Cache(this, Normalize(directory), manager, cachePath, capacityBytes);
            lock (caches)
                caches.Add(cache);
            return cache;
        }

        public void Unregister(Cache cache)
        {
            if (cache == null)
                return;
            lock (caches)
                caches.Remove(cache);
            cache.Dispose();
        }

        private Cache Find(ITorrentManagerFile file)
        {
            var path = file?.FullPath;
            if (path == null)
                return null;
            lock (caches)
                foreach (var cache in caches)
                    if (path.StartsWith(cache.Directory, StringComparison.Ordinal))
                        return cache;
            return null;
        }

        private static string Normalize(string directory) =>
            directory.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? directory : directory + Path.DirectorySeparatorChar;

        public async ReusableTask WriteAsync(ITorrentManagerFile file, long offset, ReadOnlyMemory<byte> buffer)
        {
            var cache = Find(file);
            if (cache == null)
            {
                await fallback.WriteAsync(file, offset, buffer);
                return;
            }
            cache.Write(file, offset, buffer);
        }

        public async ReusableTask<int> ReadAsync(ITorrentManagerFile file, long offset, Memory<byte> buffer)
        {
            var cache = Find(file);
            if (cache == null)
                return await fallback.ReadAsync(file, offset, buffer);
            return cache.Read(file, offset, buffer);
        }

        public async ReusableTask<bool> ExistsAsync(ITorrentManagerFile file)
        {
            var cache = Find(file);
            if (cache == null)
                return await fallback.ExistsAsync(file);
            return cache.HasData;
        }

        public async ReusableTask CloseAsync(ITorrentManagerFile file)
        {
            if (Find(file) == null)
                await fallback.CloseAsync(file);
        }

        public async ReusableTask FlushAsync(ITorrentManagerFile file)
        {
            var cache = Find(file);
            if (cache == null)
                await fallback.FlushAsync(file);
            else
                cache.Flush();
        }

        public async ReusableTask MoveAsync(ITorrentManagerFile file, string fullPath, bool overwrite)
        {
            // Streaming torrents never move their files; keep the fallback's behaviour for everything else.
            if (Find(file) == null)
                await fallback.MoveAsync(file, fullPath, overwrite);
        }

        public ReusableTask SetMaximumOpenFilesAsync(int maximumOpenFiles) => fallback.SetMaximumOpenFilesAsync(maximumOpenFiles);

        public void Dispose()
        {
            lock (caches)
            {
                foreach (var cache in caches)
                    cache.Dispose();
                caches.Clear();
            }
            fallback.Dispose();
        }

        private void Log(string message) => log?.Invoke("cache: " + message);

        // ---------------- one torrent ----------------

        internal sealed class Cache : IDisposable
        {
            private readonly RollingPieceWriter owner;
            private readonly TorrentManager manager;
            private readonly string path;
            private readonly long capacityBytes;
            private readonly object gate = new object();
            private FileStream stream;      // disk-backed when a path is given
            private byte[][] memory;        // RAM-backed otherwise (one buffer per slot, allocated on first use)
            private int pieceLength;
            private int slotCount;
            private int[] slotPiece;                 // slot -> piece (-1 free)
            private Dictionary<int, int> pieceSlot;  // piece -> slot
            private Stack<int> free;
            private readonly HashSet<int> pinned = new HashSet<int>();
            private long readPosition = -1;
            private int lastWritten = -1;
            private bool disposed;

            public Cache(RollingPieceWriter owner, string directory, TorrentManager manager, string path, long capacityBytes)
            {
                this.owner = owner;
                Directory = directory;
                this.manager = manager;
                this.path = path;
                this.capacityBytes = capacityBytes;
            }

            public string Directory { get; }

            /// Global byte offset (torrent space) the player last read; -1 until playback starts.
            public long ReadPosition
            {
                get => Interlocked.Read(ref readPosition);
                set => Interlocked.Exchange(ref readPosition, value);
            }

            /// Bytes kept behind the playhead for quick rewinds; the rest of the cache buffers ahead.
            public long BehindBytes { get; set; } = 256L * 1024 * 1024;

            public long CapacityBytes => capacityBytes;
            public int Evictions { get; private set; }
            public int Refetches { get; private set; }

            public bool HasData { get { lock (gate) return pieceSlot != null && pieceSlot.Count > 0; } }

            /// Bytes this cache holds right now: allocated RAM slot buffers (they are reused, not freed, on eviction), or
            /// stored pieces in disk mode.
            public long AllocatedBytes
            {
                get
                {
                    lock (gate)
                    {
                        if (pieceLength == 0)
                            return 0;
                        if (memory != null)
                            return (long)memory.Count(slot => slot != null) * pieceLength;
                        return (long)(pieceSlot?.Count ?? 0) * pieceLength;
                    }
                }
            }

            /// Pieces currently stored, and stored pieces at or ahead of the playhead.
            public (int stored, int ahead, int slots) Occupancy()
            {
                lock (gate)
                {
                    if (pieceSlot == null)
                        return (0, 0, 0);
                    var head = PlayheadPiece();
                    return (pieceSlot.Count, head < 0 ? pieceSlot.Count : pieceSlot.Keys.Count(p => p >= head), slotCount);
                }
            }

            /// Slots available for buffering ahead of the playhead (capacity minus the rewind window and pins).
            public int AheadSlots
            {
                get
                {
                    lock (gate)
                    {
                        if (pieceLength == 0)
                            return 0;
                        var behind = (int)Math.Min(slotCount / 4, BehindBytes / pieceLength);
                        return Math.Max(1, slotCount - behind - pinned.Count - 2);
                    }
                }
            }

            /// Keeps the first and last <paramref name="bytes"/> of the file being played (container index).
            public void Pin(ITorrentManagerFile file, long bytes) => Pin(file, bytes, false);

            /// As Pin; <paramref name="keepExisting"/> adds to the pins instead of replacing them (a prefetched next file).
            public void Pin(ITorrentManagerFile file, long bytes, bool keepExisting)
            {
                lock (gate)
                {
                    EnsureGeometry();
                    if (!keepExisting)
                        pinned.Clear();
                    if (pieceLength == 0)
                        return;
                    var count = (int)Math.Max(1, bytes / pieceLength);
                    for (var i = 0; i < count; i++)
                    {
                        pinned.Add(file.StartPieceIndex + i);
                        pinned.Add(file.EndPieceIndex - i);
                    }
                }
            }

            public void Write(ITorrentManagerFile file, long offset, ReadOnlyMemory<byte> buffer)
            {
                lock (gate)
                {
                    if (disposed)
                        return;
                    EnsureGeometry();
                    var global = file.OffsetInTorrent + offset;
                    var data = AsArray(buffer, out var start, out var length);
                    while (length > 0)
                    {
                        var piece = (int)(global / pieceLength);
                        var inPiece = (int)(global % pieceLength);
                        var chunk = Math.Min(length, pieceLength - inPiece);
                        var slot = SlotFor(piece);
                        if (memory != null)
                            Buffer.BlockCopy(data, start, memory[slot] ?? (memory[slot] = new byte[pieceLength]), inPiece, chunk);
                        else
                        {
                            stream.Seek((long)slot * pieceLength + inPiece, SeekOrigin.Begin);
                            stream.Write(data, start, chunk);
                        }
                        lastWritten = piece;
                        global += chunk;
                        start += chunk;
                        length -= chunk;
                    }
                }
            }

            public int Read(ITorrentManagerFile file, long offset, Memory<byte> buffer)
            {
                lock (gate)
                {
                    if (disposed || pieceSlot == null)
                        return 0;
                    var global = file.OffsetInTorrent + offset;
                    var total = 0;
                    var remaining = buffer.Length;
                    // Disk mode only: RAM mode copies straight from the piece buffers (no per-read allocation under the lock).
                    var scratch = memory == null ? new byte[Math.Min(remaining, pieceLength)] : null;
                    while (remaining > 0)
                    {
                        var piece = (int)(global / pieceLength);
                        if (!pieceSlot.TryGetValue(piece, out var slot))
                        {
                            // Evicted (or never written): the bitfield is cleared on the engine thread; report a short
                            // read so a hash check fails and the piece is downloaded again.
                            Refetches++;
                            return total;
                        }
                        var inPiece = (int)(global % pieceLength);
                        var chunk = Math.Min(remaining, pieceLength - inPiece);
                        int read;
                        if (memory != null)
                        {
                            var bytes = memory[slot];
                            if (bytes == null)
                                return total;
                            bytes.AsSpan(inPiece, chunk).CopyTo(buffer.Span.Slice(total));
                            read = chunk;
                        }
                        else
                        {
                            stream.Seek((long)slot * pieceLength + inPiece, SeekOrigin.Begin);
                            read = ReadFully(stream, scratch, chunk);
                            scratch.AsSpan(0, read).CopyTo(buffer.Span.Slice(total));
                        }
                        total += read;
                        if (read < chunk)
                            return total;
                        global += chunk;
                        remaining -= chunk;
                    }
                    return total;
                }
            }

            public void Flush()
            {
                lock (gate)
                    stream?.Flush();
            }

            /// Forgets every stored piece (the torrent's bitfield is cleared so they are downloaded again) and frees the
            /// slot buffers or cache file, but stays registered: the next play of this torrent refills it. Used for a
            /// stopped torrent kept for a quick replay. Call only while the torrent is stopped (no writes in flight).
            public void Release()
            {
                lock (gate)
                {
                    if (disposed || pieceSlot == null)
                        return;
                    foreach (var piece in pieceSlot.Keys.ToList())
                        BitfieldAccess.ClearPiece(manager, piece, owner.Log);
                    pieceSlot = null;
                    slotPiece = null;
                    free = null;
                    memory = null;
                    pinned.Clear();
                    pieceLength = 0;
                    slotCount = 0;
                    lastWritten = -1;
                    Interlocked.Exchange(ref readPosition, -1);
                    try { stream?.Dispose(); } catch (Exception) { }
                    stream = null;
                    if (path != null)
                        try { File.Delete(path); } catch (Exception) { }
                }
            }

            public void Dispose()
            {
                lock (gate)
                {
                    if (disposed)
                        return;
                    disposed = true;
                    memory = null;
                    try { stream?.Dispose(); } catch (Exception) { }
                    if (path != null)
                        try { File.Delete(path); } catch (Exception) { }
                }
            }

            // Must hold gate.
            private void EnsureGeometry()
            {
                if (pieceLength != 0)
                    return;
                var torrent = manager.Torrent ?? throw new InvalidOperationException("Torrent metadata is not available yet.");
                pieceLength = torrent.PieceLength;
                var pieces = (int)((torrent.Size + pieceLength - 1) / pieceLength);
                slotCount = (int)Math.Max(4, Math.Min(pieces, capacityBytes / pieceLength));
                slotPiece = Enumerable.Repeat(-1, slotCount).ToArray();
                pieceSlot = new Dictionary<int, int>();
                free = new Stack<int>(Enumerable.Range(0, slotCount).Reverse());
                if (path == null)
                    memory = new byte[slotCount][];   // RAM: the TV's app partition is ~1 GB with ~100 MB free
                else
                {
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
                    // Sparse: disk use grows with the slots written, never past the capacity.
                    stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 64 * 1024);
                    stream.SetLength((long)slotCount * pieceLength);
                }
                owner.Log($"{Path.GetFileName(Directory.TrimEnd(Path.DirectorySeparatorChar))} {(memory != null ? "memory" : "disk")} piece={pieceLength} slots={slotCount} capacity={(long)slotCount * pieceLength / (1024 * 1024)} MB (torrent {torrent.Size / (1024 * 1024)} MB)");
            }

            // Must hold gate.
            private int SlotFor(int piece)
            {
                if (pieceSlot.TryGetValue(piece, out var slot))
                    return slot;
                if (free.Count == 0)
                    Evict(Victim(piece));
                slot = free.Pop();
                slotPiece[slot] = piece;
                pieceSlot[piece] = slot;
                return slot;
            }

            // Must hold gate.
            private int PlayheadPiece()
            {
                var position = ReadPosition;
                return position < 0 || pieceLength == 0 ? -1 : (int)(position / pieceLength);
            }

            /// The stored piece that matters least: furthest behind the rewind window, then furthest ahead of the
            /// buffer, then the furthest behind the playhead. Pinned pieces and the piece being written are kept.
            // Must hold gate.
            private int Victim(int incoming)
            {
                var head = PlayheadPiece();
                if (head < 0)
                    head = incoming;
                var behindPieces = (int)Math.Min(slotCount / 4, BehindBytes / pieceLength);
                var best = -1;
                long bestScore = long.MinValue;
                foreach (var piece in pieceSlot.Keys)
                {
                    // The pieces under the read cursor are never evicted: a reader may have passed the bitfield check
                    // already, and the bit is cleared only later on the engine's loop.
                    if (pinned.Contains(piece) || piece == lastWritten || piece == incoming || (piece >= head && piece <= head + 1))
                        continue;
                    long score;
                    if (piece < head - behindPieces)
                        score = 3_000_000L + (head - piece);       // watched long ago
                    else if (piece > head + AheadSlotsLocked())
                        score = 2_000_000L + (piece - head);       // beyond the buffer
                    else if (piece < head)
                        score = 1_000_000L + (head - piece);       // inside the rewind window
                    else
                        score = piece - head;                      // in the buffer: furthest first
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = piece;
                    }
                }
                if (best < 0)
                    best = pieceSlot.Keys.First(p => p != incoming);
                return best;
            }

            // Must hold gate.
            private int AheadSlotsLocked()
            {
                var behind = (int)Math.Min(slotCount / 4, BehindBytes / pieceLength);
                return Math.Max(1, slotCount - behind - pinned.Count - 2);
            }

            // Must hold gate.
            private void Evict(int piece)
            {
                var slot = pieceSlot[piece];
                // RAM slots keep their buffer for the next piece (no reallocation churn).
                pieceSlot.Remove(piece);
                slotPiece[slot] = -1;
                free.Push(slot);
                Evictions++;
                BitfieldAccess.ClearPiece(manager, piece, owner.Log);
            }

            private static byte[] AsArray(ReadOnlyMemory<byte> buffer, out int start, out int length)
            {
                if (MemoryMarshal.TryGetArray(buffer, out var segment))
                {
                    start = segment.Offset;
                    length = segment.Count;
                    return segment.Array;
                }
                start = 0;
                length = buffer.Length;
                return buffer.ToArray();
            }

            private static int ReadFully(Stream stream, byte[] buffer, int count)
            {
                var total = 0;
                while (total < count)
                {
                    var read = stream.Read(buffer, total, count - total);
                    if (read == 0)
                        break;
                    total += read;
                }
                return total;
            }
        }

        // ---------------- MonoTorrent internals ----------------

        /// MonoTorrent 3.0.2 has no public way to "forget" a verified piece. Its torrent-wide and per-file bitfields
        /// are reachable by reflection and must change on the engine's main loop (a SynchronizationContext).
        internal static class BitfieldAccess
        {
            private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            private static readonly PropertyInfo TorrentBits = typeof(TorrentManager).GetProperty("MutableBitField", Any);
            private static readonly FieldInfo MainLoop = typeof(ClientEngine).GetField("MainLoop", Any);
            private static readonly Type FileInfoType = typeof(TorrentManager).Assembly.GetType("MonoTorrent.Client.TorrentFileInfo");
            private static readonly PropertyInfo FileBits = FileInfoType?.GetProperty("BitField", Any);

            public static bool Available => TorrentBits != null && MainLoop != null && FileBits != null;

            public static void ClearPiece(TorrentManager manager, int piece, Action<string> log)
            {
                if (!Available)
                {
                    log?.Invoke("bitfield internals missing; evicted piece " + piece + " stays marked as present");
                    return;
                }
                void Clear(object _)
                {
                    try
                    {
                        if (TorrentBits.GetValue(manager) is BitField bits && piece < bits.Length)
                            bits.Set(piece, false);
                        foreach (var file in manager.Files)
                        {
                            if (piece < file.StartPieceIndex || piece > file.EndPieceIndex || !FileInfoType.IsInstanceOfType(file))
                                continue;
                            if (FileBits.GetValue(file) is BitField fileBits)
                            {
                                var index = piece - file.StartPieceIndex;
                                if (index >= 0 && index < fileBits.Length)
                                    fileBits.Set(index, false);
                            }
                        }
                    }
                    catch (Exception error) { log?.Invoke("clear piece " + piece + ": " + error.Message); }
                }
                if (MainLoop.GetValue(null) is SynchronizationContext loop)
                    loop.Post(Clear, null);
                else
                    Clear(null);
            }
        }
    }
}
