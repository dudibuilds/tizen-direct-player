// Based on MonoTorrent 3.0.2 StreamingPieceRequester.cs (MIT License)
// Copyright (C) 2021 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
// persons to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
// Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
// COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.PiecePicking;

namespace Ru7100Probe
{
    /// MonoTorrent's streaming requester plus "edge" ranges fetched at high priority alongside the playhead.
    ///
    /// The stock requester has a single priority index (the stream's read position), so the file's tail (MKV cues, MP4
    /// moov) is only requested when the player seeks there; AVPlay does that during prepare and then waits on the swarm,
    /// and moving the index there also cancels the head's requests. Here the tail range is requested right after the
    /// head's first pieces, last piece first, without moving the read position, and it survives seeks (never cancelled).
    /// A range stops costing anything once all its pieces are present. Request bookkeeping stays in one shared picker,
    /// so validation of received blocks is unchanged.
    internal sealed class EdgeStreamingPieceRequester : IStreamingPieceRequester
    {
        private struct Range
        {
            public int Start;
            public int End;
        }

        private bool refreshAfterSeeking;
        private IMessageEnqueuer enqueuer;
        private IPieceRequesterData torrentData;
        private ReadOnlyBitField[] ignoringBitfields;
        private IPiecePicker lowPriorityPicker;
        private IPiecePicker highPriorityPicker;
        private BitField temp;
        private PieceSegment[] pickBuffer = new PieceSegment[16];
        private ReadOnlyBitField[] otherAvailableCache = Array.Empty<ReadOnlyBitField>();
        private PieceSegment[] cancellationsCache = Array.Empty<PieceSegment>();
        private volatile Range[] edges = Array.Empty<Range>();

        public bool InEndgameMode => false;

        /// Piece index of the data the player is consuming.
        public int HighPriorityPieceIndex { get; private set; }

        /// Pieces kept buffered ahead of the playhead (MonoTorrent's default).
        internal int HighPriorityCount { get; set; } = 15;

        private int LowPriorityCount => HighPriorityCount * 2;

        /// Fill each peer's whole request pipeline (MonoTorrent sizes MaxPendingRequests by the peer's speed) for the
        /// playhead window and the edges. Upstream allows 2-4 outstanding 16 KiB blocks per peer there, which caps a peer
        /// at ~64 KiB per round trip: a 4 MiB first piece then takes seconds even from fast peers. Off = upstream.
        public bool FullPipeline { get; set; } = true;

        private int Pipeline(IRequester peer, int upstream) => FullPipeline ? Math.Max(upstream, peer.MaxPendingRequests) : upstream;

        /// Requests [start, end] (torrent piece indices, inclusive) at high priority until present. Replaces earlier edges.
        /// Thread-safe: the main loop reads a snapshot.
        public void SetEdges(params (int Start, int End)[] ranges) =>
            edges = (ranges ?? Array.Empty<(int, int)>()).Where(r => r.Start >= 0 && r.End >= r.Start)
                .Select(r => new Range { Start = r.Start, End = r.End }).ToArray();

        public void ClearEdges() => edges = Array.Empty<Range>();

        /// Adds ranges to the current edges (next-episode prefetch beside the playing file).
        public void AddEdges(params (int Start, int End)[] ranges)
        {
            lock (edgeGate)
                edges = edges.Concat((ranges ?? Array.Empty<(int, int)>()).Where(r => r.Start >= 0 && r.End >= r.Start)
                    .Select(r => new Range { Start = r.Start, End = r.End })).ToArray();
        }

        /// Removes ranges added with AddEdges (exact matches).
        public void RemoveEdges(params (int Start, int End)[] ranges)
        {
            lock (edgeGate)
                edges = edges.Where(e => !(ranges ?? Array.Empty<(int, int)>()).Any(r => r.Start == e.Start && r.End == e.End)).ToArray();
        }

        private readonly object edgeGate = new object();

        /// Request only the edge ranges (a prefetched torrent with no stream): no playhead window, no background pieces,
        /// so a prefetch costs the head and tail it asked for and little more RAM. Cleared when a stream opens.
        public bool EdgesOnly { get; set; }

        public void Initialise(IPieceRequesterData torrentData, IMessageEnqueuer enqueuer, ReadOnlySpan<ReadOnlyBitField> ignoringBitfields)
        {
            this.torrentData = torrentData;
            this.enqueuer = enqueuer;
            this.ignoringBitfields = ignoringBitfields.ToArray();
            temp = new BitField(torrentData.PieceCount);

            var standardPicker = new StandardPicker();
            highPriorityPicker = IgnoringPicker.Wrap(new PriorityPicker(standardPicker), this.ignoringBitfields);
            IPiecePicker low = new RandomisedPicker(standardPicker);
            low = new RarestFirstPicker(low);
            low = new PriorityPicker(low);
            lowPriorityPicker = IgnoringPicker.Wrap(low, this.ignoringBitfields);
            lowPriorityPicker.Initialise(torrentData);
            highPriorityPicker.Initialise(torrentData);
        }

        public void AddRequests(ReadOnlySpan<(IRequester Peer, ReadOnlyBitField Available)> peers)
        {
            if (!refreshAfterSeeking || torrentData == null || enqueuer == null)
                return;
            refreshAfterSeeking = false;
            var allPeers = peers.ToArray().OrderBy(t => -t.Peer.DownloadSpeed).ToArray();

            var start = HighPriorityPieceIndex;
            var end = Math.Min(torrentData.PieceCount - 1, start + HighPriorityCount - 1);
            var bitfield = GenerateAlreadyHaves();
            if (bitfield.FirstFalse(start, end) != -1)
            {
                // Cancel what lies outside the new playhead window, except the edges.
                foreach (var peer in allPeers.Where(p => p.Peer.CanCancelRequests))
                {
                    if (HighPriorityPieceIndex > 0)
                        CancelOutsideEdges(peer.Peer, 0, HighPriorityPieceIndex - 1);
                    if (HighPriorityPieceIndex + HighPriorityCount < bitfield.Length)
                        CancelOutsideEdges(peer.Peer, HighPriorityPieceIndex + HighPriorityCount, bitfield.Length - 1);
                }
            }

            if (otherAvailableCache.Length < peers.Length)
                otherAvailableCache = new ReadOnlyBitField[peers.Length];
            var otherAvailable = otherAvailableCache.AsSpan(0, peers.Length);
            for (int i = 0; i < otherAvailable.Length; i++)
                otherAvailable[i] = peers[i].Available;

            var fastestPeers = allPeers.Where(t => t.Peer.DownloadSpeed > 50 * 1024).ToArray();
            foreach (var supportsFastPeer in new[] { true, false })
                for (int i = 0; i < 4; i++)
                    foreach (var peer in fastestPeers.Where(p => p.Peer.SupportsFastPeer == supportsFastPeer))
                        AddRequests(peer.Peer, peer.Available, otherAvailable, HighPriorityPieceIndex,
                            Math.Min(HighPriorityPieceIndex + 1, bitfield.Length - 1), 2, (i + 1) * 2);

            foreach (var peer in peers)
                AddRequests(peer.Peer, peer.Available, otherAvailable);
        }

        public void AddRequests(IRequester peer, ReadOnlyBitField available, ReadOnlySpan<ReadOnlyBitField> allPeers)
        {
            if (torrentData == null)
                return;
            var pieceCount = torrentData.PieceCount;
            if (EdgesOnly)
            {
                AddEdgeRequests(peer, available, allPeers);
                return;
            }
            // The first two pieces of the playhead window are requested from several peers for fast delivery.
            for (int i = HighPriorityPieceIndex; i < pieceCount && i <= HighPriorityPieceIndex + 1; i++)
                AddRequests(peer, available, allPeers, HighPriorityPieceIndex, HighPriorityPieceIndex, 2, FullPipeline ? Math.Max(4, peer.MaxPendingRequests / 2) : 4);

            AddEdgeRequests(peer, available, allPeers);

            var lowPriorityEnd = Math.Min(pieceCount - 1, HighPriorityPieceIndex + LowPriorityCount - 1);
            AddRequests(peer, available, allPeers, HighPriorityPieceIndex, lowPriorityEnd, 1, Pipeline(peer, 3));
            AddRequests(peer, available, allPeers, 0, pieceCount - 1, 1, 2);
        }

        /// Edge pieces, last piece of each range first, at most 4 outstanding requests per peer for them.
        private void AddEdgeRequests(IRequester peer, ReadOnlyBitField available, ReadOnlySpan<ReadOnlyBitField> allPeers)
        {
            var snapshot = edges;
            if (snapshot.Length == 0 || peer.IsChoking && !(peer.SupportsFastPeer && peer.IsAllowedFastPieces.Count > 0))
                return;
            var haves = GenerateAlreadyHaves();
            // Edges share the pipeline with the playhead: at most half of it (upstream-sized 4 without FullPipeline).
            var maxRequests = FullPipeline ? Math.Max(4, peer.MaxPendingRequests / 2 + peer.AmRequestingPiecesCount) : 4;
            foreach (var range in snapshot)
            {
                var end = Math.Min(range.End, torrentData.PieceCount - 1);
                if (range.Start > end || haves.FirstFalse(range.Start, end) == -1)
                    continue;
                for (var piece = end; piece >= range.Start; piece--)
                {
                    if (haves[piece] || !available[piece])
                        continue;
                    while (peer.CanRequestMorePieces && peer.AmRequestingPiecesCount < Math.Min(maxRequests, peer.MaxPendingRequests))
                    {
                        if (!peer.IsChoking && highPriorityPicker.ContinueAnyExistingRequest(peer, available, piece, piece, 2, out var existing))
                        {
                            enqueuer.EnqueueRequest(peer, existing);
                            continue;
                        }
                        var filtered = new BitField(haves).Not().And(available);
                        var room = Math.Min(maxRequests, peer.MaxPendingRequests) - peer.AmRequestingPiecesCount;
                        if (room <= 0)
                            break;
                        if (pickBuffer.Length < room)
                            pickBuffer = new PieceSegment[room];
                        var buffer = pickBuffer.AsSpan(0, room);
                        var requested = highPriorityPicker.PickPiece(peer, filtered, allPeers, piece, piece, buffer);
                        if (requested <= 0)
                            break;
                        enqueuer.EnqueueRequests(peer, buffer.Slice(0, requested));
                    }
                    if (!peer.CanRequestMorePieces || peer.AmRequestingPiecesCount >= Math.Min(maxRequests, peer.MaxPendingRequests))
                        return;
                }
            }
        }

        private void AddRequests(IRequester peer, ReadOnlyBitField available, ReadOnlySpan<ReadOnlyBitField> allPeers, int startPieceIndex, int endPieceIndex, int maxDuplicates, int preferredMaxRequests)
        {
            if (!peer.CanRequestMorePieces || torrentData == null || enqueuer == null)
                return;
            int maxTotalRequests = Math.Min(preferredMaxRequests, peer.MaxPendingRequests);
            if (!peer.IsChoking)
            {
                while (peer.CanRequestMorePieces && peer.AmRequestingPiecesCount < maxTotalRequests)
                {
                    if (lowPriorityPicker.ContinueAnyExistingRequest(peer, available, startPieceIndex, endPieceIndex, maxDuplicates, out PieceSegment request))
                        enqueuer.EnqueueRequest(peer, request);
                    else
                        break;
                }
            }
            if (peer.SupportsFastPeer && peer.IsChoking)
            {
                while (peer.CanRequestMorePieces && peer.AmRequestingPiecesCount < maxTotalRequests)
                {
                    if (lowPriorityPicker.ContinueExistingRequest(peer, startPieceIndex, endPieceIndex, out PieceSegment segment))
                        enqueuer.EnqueueRequest(peer, segment);
                    else
                        break;
                }
            }
            if (!peer.IsChoking || (peer.SupportsFastPeer && peer.IsAllowedFastPieces.Count > 0))
            {
                BitField filtered = null;
                while (peer.CanRequestMorePieces && peer.AmRequestingPiecesCount < maxTotalRequests)
                {
                    if (filtered == null)
                        filtered = GenerateAlreadyHaves().Not().And(available);
                    var room = maxTotalRequests - peer.AmRequestingPiecesCount;
                    if (pickBuffer.Length < room)
                        pickBuffer = new PieceSegment[room];
                    var buffer = pickBuffer.AsSpan(0, room);
                    int requested = PriorityPick(peer, filtered, allPeers, startPieceIndex, endPieceIndex, buffer);
                    if (requested > 0)
                        enqueuer.EnqueueRequests(peer, buffer.Slice(0, requested));
                    else
                        break;
                }
            }
        }

        private BitField GenerateAlreadyHaves()
        {
            temp.From(ignoringBitfields[0]);
            for (int i = 1; i < ignoringBitfields.Length; i++)
                temp.Or(ignoringBitfields[i]);
            return temp;
        }

        private int PriorityPick(IRequester peer, ReadOnlyBitField available, ReadOnlySpan<ReadOnlyBitField> otherPeers, int startIndex, int endIndex, Span<PieceSegment> requests)
        {
            int requestCount;
            if (HighPriorityPieceIndex >= startIndex && HighPriorityPieceIndex <= endIndex)
            {
                var start = HighPriorityPieceIndex;
                var end = Math.Min(endIndex, HighPriorityPieceIndex + HighPriorityCount - 1);
                for (int prioritised = start; prioritised <= start + 1 && prioritised <= end; prioritised++)
                {
                    if (available[prioritised])
                    {
                        if ((requestCount = highPriorityPicker.PickPiece(peer, available, otherPeers, prioritised, prioritised, requests)) > 0)
                            return requestCount;
                        if (highPriorityPicker.ContinueAnyExistingRequest(peer, available, prioritised, prioritised, 3, out requests[0]))
                            return 1;
                    }
                }
                if ((requestCount = highPriorityPicker.PickPiece(peer, available, otherPeers, start, end, requests)) > 0)
                    return requestCount;
            }
            var lowPriorityEndIndex = Math.Min(HighPriorityPieceIndex + LowPriorityCount, endIndex);
            if ((requestCount = lowPriorityPicker.PickPiece(peer, available, otherPeers, HighPriorityPieceIndex, lowPriorityEndIndex, requests)) > 0)
                return requestCount;
            if (peer.AmRequestingPiecesCount < 2)
                return lowPriorityPicker.PickPiece(peer, available, otherPeers, startIndex, endIndex, requests);
            return 0;
        }

        public void SeekToPosition(ITorrentManagerFile file, long position)
        {
            var oldIndex = HighPriorityPieceIndex;
            ReadToPosition(file, position);
            refreshAfterSeeking |= oldIndex != HighPriorityPieceIndex;
        }

        public void ReadToPosition(ITorrentManagerFile file, long position)
        {
            if (torrentData != null)
                HighPriorityPieceIndex = position >= file.Length
                    ? file.EndPieceIndex
                    : torrentData.ByteOffsetToPieceIndex(position + file.OffsetInTorrent);
        }

        public bool ValidatePiece(IRequester peer, PieceSegment blockInfo, out bool pieceComplete, HashSet<IRequester> peersInvolved)
            => highPriorityPicker.ValidatePiece(peer, blockInfo, out pieceComplete, peersInvolved);

        public bool IsInteresting(IRequester peer, ReadOnlyBitField bitfield)
            => highPriorityPicker.IsInteresting(peer, bitfield);

        /// Cancels [startIndex, endIndex] minus the edge ranges.
        private void CancelOutsideEdges(IRequester peer, int startIndex, int endIndex)
        {
            var from = startIndex;
            foreach (var range in edges.OrderBy(r => r.Start))
            {
                if (range.End < from || range.Start > endIndex)
                    continue;
                if (range.Start > from)
                    CancelRequests(peer, from, range.Start - 1);
                from = range.End + 1;
            }
            if (from <= endIndex)
                CancelRequests(peer, from, endIndex);
        }

        public void CancelRequests(IRequester peer, int startIndex, int endIndex)
        {
            if (highPriorityPicker == null || enqueuer == null)
                return;
            if (cancellationsCache.Length < peer.AmRequestingPiecesCount)
                cancellationsCache = new PieceSegment[peer.AmRequestingPiecesCount];
            var cancellations = cancellationsCache.AsSpan(0, peer.AmRequestingPiecesCount);
            var cancelled = highPriorityPicker.CancelRequests(peer, startIndex, endIndex, cancellations);
            enqueuer.EnqueueCancellations(peer, cancellations.Slice(0, cancelled));
        }

        public void RequestRejected(IRequester peer, PieceSegment blockInfo)
            => highPriorityPicker.RequestRejected(peer, blockInfo);

        public int CurrentRequestCount()
            => highPriorityPicker.CurrentRequestCount();

        /// The requester behind a manager's StreamProvider (a private property in MonoTorrent 3.0.2), or null.
        public static EdgeStreamingPieceRequester Of(TorrentManager manager)
        {
            try
            {
                var provider = manager?.StreamProvider;
                var property = provider?.GetType().GetProperty("PieceRequester", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                return property?.GetValue(provider) as EdgeStreamingPieceRequester;
            }
            catch (Exception) { return null; }
        }
    }
}
