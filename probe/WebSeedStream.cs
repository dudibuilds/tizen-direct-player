using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace Ru7100Probe
{
    // Prefer the peer stream. If its current piece is slow, ask the torrent's published
    // web seed for the complete piece and verify it against the same torrent metadata.
    internal sealed class WebSeedStream : Stream
    {
        private const int CacheLimit = 8 * 1024 * 1024;
        private readonly Stream peers;
        private readonly TorrentManager torrent;
        private readonly Torrent metadata;
        private readonly ITorrentFile video;
        private readonly Uri[] origins;
        private readonly IPieceHashes hashes;
        private readonly HttpClient http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        private readonly Queue<int> pieceOrder = new Queue<int>();
        private readonly Dictionary<int, byte[]> pieces = new Dictionary<int, byte[]>();
        private int cacheBytes;
        private long position;
        private int webPieces;
        private volatile string seedStatus = "pending";

        public WebSeedStream(Stream peers, TorrentManager torrent, ITorrentFile video)
        {
            this.peers = peers;
            this.torrent = torrent;
            this.video = video;
            metadata = torrent.Torrent;
            hashes = metadata.CreatePieceHashes();
            origins = metadata.HttpSeeds.Where(uri => uri.Scheme == Uri.UriSchemeHttps
                && string.IsNullOrEmpty(uri.UserInfo)).ToArray();
        }

        public int WebPieces => Volatile.Read(ref webPieces);
        public string SeedStatus => seedStatus;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => video.Length;
        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public static bool IsAvailable(Torrent metadata) => metadata.InfoHashes.V1 != null && metadata.PieceLength <= CacheLimit &&
            metadata.HttpSeeds.Any(uri => uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo));

        public bool IsCached(long offset)
        {
            var piece = PieceIndex(video.OffsetInTorrent + offset);
            return torrent.Bitfield[piece] || pieces.ContainsKey(piece);
        }

        private int PieceIndex(long offset) => (int)(offset / metadata.PieceLength);

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (count == 0 || position == Length)
                return 0;

            var absolute = video.OffsetInTorrent + position;
            var index = PieceIndex(absolute);
            var offsetInPiece = (int)(absolute % metadata.PieceLength);
            count = (int)Math.Min(Math.Min(count, Length - position), metadata.PieceLength - offsetInPiece);
            if (!pieces.TryGetValue(index, out var piece))
            {
                peers.Seek(position, SeekOrigin.Begin);
                using (var peerWait = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    peerWait.CancelAfter(TimeSpan.FromMilliseconds(500));
                    try
                    {
                        var read = await peers.ReadAsync(buffer, offset, count, peerWait.Token).ConfigureAwait(false);
                        position += read;
                        return read;
                    }
                    catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
                }

                piece = await FetchPieceAsync(index, cancellation).ConfigureAwait(false);
                if (piece == null)
                {
                    var read = await peers.ReadAsync(buffer, offset, count, cancellation).ConfigureAwait(false);
                    position += read;
                    return read;
                }
                while (cacheBytes + piece.Length > CacheLimit && pieceOrder.Count > 0)
                {
                    var oldest = pieceOrder.Dequeue();
                    cacheBytes -= pieces[oldest].Length;
                    pieces.Remove(oldest);
                }
                pieces.Add(index, piece);
                pieceOrder.Enqueue(index);
                cacheBytes += piece.Length;
                Interlocked.Increment(ref webPieces);
            }
            Buffer.BlockCopy(piece, offsetInPiece, buffer, offset, count);
            position += count;
            return count;
        }

        private async Task<byte[]> FetchPieceAsync(int index, CancellationToken cancellation)
        {
            var start = (long)index * metadata.PieceLength;
            var lastFile = metadata.Files[metadata.Files.Count - 1];
            var totalLength = lastFile.OffsetInTorrent + lastFile.Length + lastFile.Padding;
            var end = Math.Min(totalLength, start + metadata.PieceLength);
            foreach (var origin in origins)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var piece = new byte[end - start];
                    foreach (var file in metadata.Files)
                    {
                        var firstByte = Math.Max(start, file.OffsetInTorrent);
                        var endByte = Math.Min(end, file.OffsetInTorrent + file.Length);
                        if (endByte <= firstByte)
                            continue;
                        await DownloadRangeAsync(FileUri(origin, file), file.Length, firstByte - file.OffsetInTorrent,
                            piece, (int)(firstByte - start), (int)(endByte - firstByte), timeout.Token).ConfigureAwait(false);
                    }
                    using var sha1 = SHA1.Create();
                    if (!hashes.GetHash(index).V1Hash.Span.SequenceEqual(sha1.ComputeHash(piece)))
                        throw new InvalidDataException("Piece hash mismatch");
                    seedStatus = "HTTP 206 verified";
                    return piece;
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                {
                    seedStatus = "Web seed timed out";
                }
                catch (Exception error) when (error is IOException || error is HttpRequestException || error is InvalidDataException)
                {
                    seedStatus = error.Message.Replace('|', '/');
                }
            }
            return null;
        }

        private Uri FileUri(Uri origin, ITorrentFile file)
        {
            if (metadata.Files.Count == 1 && !origin.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
                return origin;
            if (!origin.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
                throw new InvalidDataException("Multi-file web seed URL must end in /");

            var segments = file.Path.Split('/', '\\');
            if (metadata.Name == "." || metadata.Name == ".." || string.IsNullOrEmpty(metadata.Name)
                || segments.Any(segment => segment == "." || segment == ".." || segment.Length == 0))
                throw new InvalidDataException("Invalid torrent file path");
            var path = metadata.Files.Count == 1
                ? Uri.EscapeDataString(metadata.Name)
                : Uri.EscapeDataString(metadata.Name) + "/" + string.Join("/", segments.Select(Uri.EscapeDataString));
            return new Uri(origin, path);
        }

        private async Task DownloadRangeAsync(Uri uri, long fileLength, long start, byte[] buffer, int offset,
            int count, CancellationToken cancellation)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(start, start + count - 1);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation)
                .ConfigureAwait(false);
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != start
                || range.To != start + count - 1 || range.Length != fileLength)
                throw new InvalidDataException($"Invalid web seed range response HTTP {(int)response.StatusCode}");
            using var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            while (count > 0)
            {
                var read = await body.ReadAsync(buffer, offset, count, cancellation).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("Truncated web seed piece");
                offset += read;
                count -= read;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0 || target > Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            return position = target;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                peers.Dispose();
                http.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
