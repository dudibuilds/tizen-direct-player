using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Ru7100Probe.Utp
{
    internal enum UtpConnectionState
    {
        SynSent,
        Connected,
        Closed,
    }

    internal enum UtpCloseReason
    {
        None,
        /// Closed by us after the FIN exchange (or linger) ended.
        Closed,
        /// The peer sent RESET.
        Reset,
        /// The peer answered our SYN with RESET.
        Refused,
        /// Retransmissions went unanswered.
        TimedOut,
        /// Closed locally before or without a FIN (Abort, or Dispose during connect).
        Aborted,
    }

    /// One uTP connection (BEP 29, wire compatible with libutp / libtorrent). Sans-IO: packets come in through
    /// UtpContext.Receive, time through the context clock, and every send goes to the context's sink. Thread-safe:
    /// the receive path, the timer and application calls each take the connection lock; waiting tasks complete
    /// asynchronously, never inline under the lock.
    internal sealed class UtpConnection : IDisposable
    {
        private const int RingSize = 1024;
        private const int RingMask = RingSize - 1;
        private const int MaxSelectiveAckBytes = 32;

        internal sealed class OutPacket
        {
            public byte[] Buffer;
            public int Length;
            public int Payload;
            public int Type;
            public ushort Seq;
            public long SentAt;
            public int Transmissions;
            public bool NeedResend;
            public bool Acked;
            public bool FastResent;

            public void Reset()
            {
                Buffer = null;
                Length = Payload = Type = Transmissions = 0;
                Seq = 0;
                SentAt = 0;
                NeedResend = Acked = FastResent = false;
            }
        }

        private struct Segment
        {
            public byte[] Buffer;
            public int Offset;
            public int Count;
            public bool Present;
            public bool Fin;
        }

        private readonly UtpContext context;
        private readonly UtpOptions options;
        private readonly object gate = new object();
        private readonly int mss;
        private UtpConnectionState state;
        private UtpCloseReason closeReason;

        // ---- send side ----
        private ushort seqNr;                // next sequence number to send
        private readonly OutPacket[] outRing = new OutPacket[RingSize];
        private int curPackets;              // packets in outRing, oldest = seqNr - curPackets
        private long inFlightBytes;          // payload sent and neither acked nor waiting for resend
        private int needResendCount;
        private long peerWindow;
        private long maxWindow;              // congestion window, bytes
        private bool slowStart = true;
        private long ssthresh;
        private long lastMaxedOutWindow;
        private long lastLossCut;
        private int dupAcks;
        private readonly DelayHistory ourDelay = new DelayHistory();
        private long zeroWindowSince;
        private ushort synSeq;

        // ---- timers ----
        private long rtt;
        private long rttVar;
        private long rto;
        private bool rttValid;
        private long rtoDeadline;
        private int consecutiveTimeouts;
        private long lastSend;
        private long lastReceive;
        private bool peerSpoke;              // incoming: a packet other than the SYN arrived

        // ---- receive side ----
        private ushort ackNr;                // last in-order sequence number received
        private uint replyMicro;
        private Segment[] inRing;            // out-of-order packets, index seq & RingMask
        private int reorderCount;
        private long reorderBytes;
        private Segment[] queue = new Segment[16];
        private int queueHead;
        private int queueCount;
        private long bufferedBytes;
        private long lastAdvertised;
        private int pendingAcks;
        private long ackDeadline;            // a lone pending ACK goes out by then (delayed ACK)
        private long readDeadline;           // a partly filled read completes by then
        private bool flushScheduled;
        private bool finReceived;
        private ushort eofSeq;
        private bool readEof;
        private readonly byte[] control = new byte[UtpWire.HeaderSize + 2 + MaxSelectiveAckBytes];

        // ---- application waiters ----
        private Memory<byte> readBuffer;
        private int readFilled;
        private TaskCompletionSource<int> readWaiter;
        private ReadOnlyMemory<byte> writeBuffer;
        private int writeOffset;
        private TaskCompletionSource<bool> writeWaiter;
        private readonly TaskCompletionSource<bool> connectWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool closeRequested;
        private bool finSent;
        private bool finAcked;
        private bool disposed;
        private long disposedAt;

        // ---- statistics (read without the lock; approximate) ----
        public long BytesSent;
        public long BytesReceived;
        public long PacketsSent;
        public long PacketsReceived;
        public long Retransmits;
        public long Timeouts;
        public long FastResends;
        public long DuplicatePackets;

        internal UtpConnection(UtpContext context, IPEndPoint remote, ushort receiveId, ushort sendId, ushort initialSeq, bool incoming)
        {
            this.context = context;
            options = context.Options;
            mss = options.MaxPayload;
            RemoteEndPoint = remote;
            ReceiveId = receiveId;
            SendId = sendId;
            IsIncoming = incoming;
            seqNr = initialSeq;
            maxWindow = Math.Max(1, options.InitialWindowPackets) * (long)mss;
            ssthresh = options.MaxSendWindowBytes;
            peerWindow = mss;
            rto = options.InitialRtoMicros;
            var now = context.NowMicros;
            lastMaxedOutWindow = now;
            lastReceive = now;
            lastSend = now;
        }

        public IPEndPoint RemoteEndPoint { get; }
        public ushort ReceiveId { get; }
        public ushort SendId { get; }
        public bool IsIncoming { get; }
        public UtpConnectionState State { get { lock (gate) return state; } }
        public UtpCloseReason CloseReason { get { lock (gate) return closeReason; } }
        /// Completes when the handshake does; faults with a SocketException (TimedOut, ConnectionRefused, ...).
        public Task ConnectTask => connectWaiter.Task;
        /// All data before the peer's FIN has been read.
        public bool IsEndOfStream { get { lock (gate) return readEof && queueCount == 0; } }
        public int Available { get { lock (gate) return (int)bufferedBytes; } }
        public long RoundTripMicros { get { lock (gate) return rtt; } }
        public long CongestionWindow { get { lock (gate) return maxWindow; } }
        public long BytesInFlight { get { lock (gate) return inFlightBytes; } }
        public long QueuingDelayMicros { get { lock (gate) return ourDelay.HasValue ? ourDelay.Value : 0; } }

        public override string ToString() =>
            $"utp {RemoteEndPoint} id={ReceiveId}/{SendId} {state} rtt={rtt / 1000}ms rto={rto / 1000}ms cwnd={maxWindow} flight={inFlightBytes} " +
            $"sent={BytesSent} recv={BytesReceived} rexmit={Retransmits} timeouts={Timeouts}";

        // =====================================================================================================
        // Handshake
        // =====================================================================================================

        internal void StartConnect(long now)
        {
            lock (gate)
            {
                state = UtpConnectionState.SynSent;
                synSeq = seqNr;
                var packet = context.RentPacket();
                packet.Type = UtpWire.TypeSyn;
                packet.Seq = seqNr;
                packet.Length = UtpWire.HeaderSize;
                outRing[seqNr & RingMask] = packet;
                seqNr++;
                curPackets++;
                SendPacket(packet, now);
            }
        }

        internal void AcceptSyn(in UtpHeader syn, long now)
        {
            lock (gate)
            {
                state = UtpConnectionState.Connected;
                ackNr = syn.SeqNr;
                peerWindow = syn.WindowSize;
                replyMicro = (uint)now - syn.TimestampMicros;
                lastReceive = now;
                connectWaiter.TrySetResult(true);
                SendState(now);
            }
        }

        // =====================================================================================================
        // Incoming packets
        // =====================================================================================================

        internal void Receive(in UtpHeader header, byte[] buffer, long now)
        {
            lock (gate)
            {
                if (state == UtpConnectionState.Closed)
                    return;
                PacketsReceived++;
                lastReceive = now;
                replyMicro = (uint)now - header.TimestampMicros;

                if (header.Type == UtpWire.TypeReset)
                {
                    Terminate(state == UtpConnectionState.SynSent ? UtpCloseReason.Refused : UtpCloseReason.Reset);
                    return;
                }
                if (header.Type == UtpWire.TypeSyn)
                {
                    // Our SYN-ACK was lost and the peer retried: answer again.
                    if (IsIncoming)
                        SendState(now);
                    return;
                }
                peerSpoke = true;
                if (state == UtpConnectionState.SynSent)
                {
                    if (header.AckNr != synSeq)
                        return;
                    // The SYN-ACK carries the sequence number of the peer's first DATA packet (STATE does not
                    // consume one), so the last "received" packet is the one before it (libutp: ack_nr = seq - 1).
                    ackNr = (ushort)(header.SeqNr - 1);
                    state = UtpConnectionState.Connected;
                    connectWaiter.TrySetResult(true);
                }

                ProcessAck(header, buffer, now);
                if (state == UtpConnectionState.Closed)
                    return;
                if (header.Type == UtpWire.TypeData || header.Type == UtpWire.TypeFin)
                    ProcessData(header, buffer, now);
                TrySendMore(now);
                MaybeFinishLinger();
            }
        }

        private void ProcessAck(in UtpHeader header, byte[] buffer, long now)
        {
            peerWindow = header.WindowSize;
            if (peerWindow >= mss)
                zeroWindowSince = 0;
            else if (zeroWindowSince == 0)
                zeroWindowSince = now;

            var firstUnacked = (ushort)(seqNr - curPackets);
            var acks = Seq.Distance(firstUnacked, (ushort)(header.AckNr + 1));
            if (acks > curPackets)
                acks = 0;   // an old ACK, or one for packets we never sent
            long ackedBytes = 0;
            var minRtt = long.MaxValue;
            for (var i = 0; i < acks; i++)
            {
                var slot = (firstUnacked + i) & RingMask;
                var packet = outRing[slot];
                outRing[slot] = null;
                if (packet == null)
                    continue;
                if (!packet.Acked)
                {
                    ackedBytes += packet.Payload;
                    OnAcked(packet, now, ref minRtt);
                }
                if (packet.Type == UtpWire.TypeFin)
                    finAcked = true;
                context.ReturnPacket(packet);
            }
            curPackets -= acks;

            var loss = false;
            if (header.SelectiveAckLength > 0 && curPackets > 0)
                ackedBytes += ProcessSelectiveAck(header, buffer, now, ref minRtt, ref loss);

            if (acks == 0 && curPackets > 0 && header.Type == UtpWire.TypeState && header.PayloadLength == 0
                && header.AckNr == (ushort)(seqNr - curPackets - 1))
            {
                if (++dupAcks >= 3)
                {
                    var oldest = outRing[(ushort)(seqNr - curPackets) & RingMask];
                    if (oldest != null && MarkLost(oldest))
                        loss = true;
                }
            }
            else if (acks > 0)
                dupAcks = 0;

            if (acks > 0 || ackedBytes > 0)
            {
                consecutiveTimeouts = 0;
                rtoDeadline = curPackets > 0 ? now + rto : 0;
            }
            if (loss && now - lastLossCut > Math.Max(rtt, 100_000))
            {
                maxWindow = Math.Max(maxWindow / 2, mss);
                ssthresh = maxWindow;
                slowStart = false;
                lastLossCut = now;
            }
            if (header.TimestampDifferenceMicros != 0)
                ourDelay.Add(header.TimestampDifferenceMicros, now);
            if (ackedBytes > 0)
                ApplyCongestionControl(ackedBytes, minRtt, now);
        }

        private long ProcessSelectiveAck(in UtpHeader header, byte[] buffer, long now, ref long minRtt, ref bool loss)
        {
            long acked = 0;
            var firstUnacked = (ushort)(seqNr - curPackets);
            var bits = header.SelectiveAckLength * 8;
            // Bit i covers ack_nr + 2 + i; ack_nr + 1 (the first unacked packet) is the hole that caused the SACK.
            var highest = -1;
            for (var bit = 0; bit < bits; bit++)
            {
                if (!UtpHeader.IsSelectivelyAcked(buffer, header, bit))
                    continue;
                var seq = (ushort)(header.AckNr + 2 + bit);
                if (Seq.Distance(firstUnacked, seq) >= curPackets)
                    break;
                highest = bit;
                var packet = outRing[seq & RingMask];
                if (packet == null || packet.Acked)
                    continue;
                acked += packet.Payload;
                OnAcked(packet, now, ref minRtt);
                packet.Acked = true;
                context.Buffers.Return(packet.Buffer);
                packet.Buffer = null;
            }
            // Fast retransmit: a packet with at least three selectively acknowledged packets after it is lost.
            var after = 0;
            for (var bit = highest; bit >= -1; bit--)
            {
                if (bit >= 0 && UtpHeader.IsSelectivelyAcked(buffer, header, bit))
                {
                    after++;
                    continue;
                }
                if (after < 3)
                    continue;
                var seq = (ushort)(header.AckNr + 2 + bit);
                if (Seq.Distance(firstUnacked, seq) >= curPackets)
                    continue;
                var packet = outRing[seq & RingMask];
                if (packet != null && MarkLost(packet))
                    loss = true;
            }
            return acked;
        }

        private void OnAcked(OutPacket packet, long now, ref long minRtt)
        {
            if (packet.NeedResend)
            {
                packet.NeedResend = false;
                needResendCount--;
            }
            else
                inFlightBytes -= packet.Payload;
            // Karn: only packets sent once give an unambiguous RTT sample.
            if (packet.Transmissions == 1)
            {
                var sample = now - packet.SentAt;
                if (sample < minRtt)
                    minRtt = sample;
                UpdateRtt(sample);
            }
        }

        private bool MarkLost(OutPacket packet)
        {
            if (packet.Acked || packet.NeedResend || packet.FastResent || packet.Transmissions == 0)
                return false;
            packet.FastResent = true;
            packet.NeedResend = true;
            needResendCount++;
            inFlightBytes -= packet.Payload;
            FastResends++;
            return true;
        }

        private void UpdateRtt(long sample)
        {
            if (sample < 0)
                return;
            if (!rttValid)
            {
                rtt = sample;
                rttVar = sample / 2;
                rttValid = true;
            }
            else
            {
                var delta = Math.Abs(rtt - sample);
                rttVar += (delta - rttVar) / 4;
                rtt += (sample - rtt) / 8;
            }
            rto = Math.Min(Math.Max(rtt + 4 * rttVar, options.MinRtoMicros), options.MaxRtoMicros);
        }

        /// LEDBAT as in libutp: grow or shrink the window in proportion to how far the queuing delay our packets
        /// see (one-way, measured by the peer and echoed in timestamp_difference) is from the target.
        private void ApplyCongestionControl(long bytesAcked, long minRtt, long now)
        {
            if (!ourDelay.HasValue)
                return;
            long queuing = ourDelay.Value;
            if (minRtt != long.MaxValue && queuing > minRtt)
                queuing = minRtt;   // a one-way queuing delay larger than the round trip is a clock artefact
            var target = (double)options.TargetDelayMicros;
            var offTarget = target - queuing;
            var windowFactor = Math.Min(bytesAcked, maxWindow) / (double)Math.Max(maxWindow, bytesAcked);
            var delayFactor = offTarget / target;
            var gain = options.MaxCwndIncreaseBytesPerRtt * windowFactor * delayFactor;
            // Not using the whole window (application limited): do not grow it.
            if (gain > 0 && now - lastMaxedOutWindow > 1_000_000)
                gain = 0;
            var ledbat = (long)(maxWindow + gain);
            if (slowStart)
            {
                var exponential = maxWindow + bytesAcked;
                if (exponential > ssthresh)
                    slowStart = false;
                else if (queuing > target * 0.9)
                {
                    slowStart = false;
                    ssthresh = maxWindow;
                }
                else
                    ledbat = Math.Max(exponential, ledbat);
            }
            maxWindow = Math.Min(Math.Max(ledbat, mss), options.MaxSendWindowBytes);
        }

        private void ProcessData(in UtpHeader header, byte[] buffer, long now)
        {
            var isFin = header.Type == UtpWire.TypeFin;
            var dist = Seq.Distance((ushort)(ackNr + 1), header.SeqNr);
            if (dist >= 0x8000)
            {
                // Already received: the ACK for it was lost or the network duplicated it.
                DuplicatePackets++;
                SendState(now);
                return;
            }
            if (finReceived && Seq.Distance((ushort)(ackNr + 1), eofSeq) < dist)
                return;   // beyond the FIN
            if (dist >= RingSize - 1)
            {
                SendState(now);
                return;
            }
            var payload = header.PayloadLength;
            if (dist == 0)
            {
                Deliver(buffer, header.PayloadOffset, payload, owned: false, isFin, header.SeqNr);
                while (reorderCount > 0)
                {
                    var slot = (ushort)(ackNr + 1) & RingMask;
                    var next = inRing[slot];
                    if (!next.Present)
                        break;
                    inRing[slot] = default;
                    reorderCount--;
                    reorderBytes -= next.Count;
                    Deliver(next.Buffer, 0, next.Count, owned: true, next.Fin, (ushort)(ackNr + 1));
                }
                if (isFin || readEof || reorderCount > 0 || ++pendingAcks >= options.AckEvery)
                    SendState(now);
                else
                {
                    if (pendingAcks == 1)
                        ackDeadline = now + options.DelayedAckMicros;
                    ScheduleFlush();
                }
                return;
            }

            inRing = inRing ?? new Segment[RingSize];
            var index = header.SeqNr & RingMask;
            if (inRing[index].Present)
            {
                DuplicatePackets++;
                SendState(now);
                return;
            }
            if (bufferedBytes + reorderBytes + payload > options.ReceiveBufferBytes)
            {
                SendState(now);   // beyond the window we advertised; the sender will retry
                return;
            }
            byte[] copy = null;
            if (payload > 0)
            {
                copy = context.Buffers.Rent();
                Buffer.BlockCopy(buffer, header.PayloadOffset, copy, 0, payload);
            }
            inRing[index] = new Segment { Buffer = copy, Count = payload, Present = true, Fin = isFin };
            reorderCount++;
            reorderBytes += payload;
            if (isFin)
            {
                finReceived = true;
                eofSeq = header.SeqNr;
            }
            SendState(now);   // immediately, with the selective ACK, so the sender can repair the hole
        }

        private void Deliver(byte[] buffer, int offset, int count, bool owned, bool fin, ushort seq)
        {
            ackNr = seq;
            if (count > 0)
            {
                BytesReceived += count;
                if (disposed)
                {
                    if (owned)
                        context.Buffers.Return(buffer);
                }
                else
                {
                    var copied = 0;
                    if (readWaiter != null)
                    {
                        copied = Math.Min(count, readBuffer.Length - readFilled);
                        buffer.AsSpan(offset, copied).CopyTo(readBuffer.Span.Slice(readFilled));
                        if (readFilled == 0)
                            readDeadline = context.NowMicros + options.DelayedAckMicros;
                        readFilled += copied;
                        // A full buffer completes at once; a partial one when the data pauses (Flush). MonoTorrent reads
                        // whole messages, so a 16 KB block wakes its reader once instead of once per packet.
                        if (readFilled == readBuffer.Length)
                            CompleteRead(readFilled);
                        else
                            ScheduleFlush();
                    }
                    if (copied < count)
                    {
                        if (!owned)
                        {
                            var copy = context.Buffers.Rent();
                            Buffer.BlockCopy(buffer, offset + copied, copy, 0, count - copied);
                            Enqueue(copy, 0, count - copied);
                        }
                        else
                            Enqueue(buffer, offset + copied, count - copied);
                    }
                    else if (owned)
                        context.Buffers.Return(buffer);
                }
            }
            else if (owned && buffer != null)
                context.Buffers.Return(buffer);
            if (fin)
            {
                finReceived = true;
                eofSeq = seq;
                readEof = true;
                if (readWaiter != null)
                    CompleteRead(readFilled);
            }
        }

        private void Enqueue(byte[] buffer, int offset, int count)
        {
            if (queueCount == queue.Length)
            {
                var grown = new Segment[queue.Length * 2];
                for (var i = 0; i < queueCount; i++)
                    grown[i] = queue[(queueHead + i) % queue.Length];
                queue = grown;
                queueHead = 0;
            }
            queue[(queueHead + queueCount) % queue.Length] = new Segment { Buffer = buffer, Offset = offset, Count = count, Present = true };
            queueCount++;
            bufferedBytes += count;
        }

        // =====================================================================================================
        // Sending
        // =====================================================================================================

        private void SendPacket(OutPacket packet, long now)
        {
            if (packet.NeedResend)
            {
                packet.NeedResend = false;
                needResendCount--;
                inFlightBytes += packet.Payload;
            }
            var connectionId = packet.Type == UtpWire.TypeSyn ? ReceiveId : SendId;
            UtpHeader.Write(packet.Buffer, 0, packet.Type, 0, connectionId, (uint)now, replyMicro, AdvertisedWindow(), packet.Seq, ackNr);
            if (packet.Transmissions > 0)
                Retransmits++;
            packet.Transmissions++;
            packet.SentAt = now;
            PacketsSent++;
            lastSend = now;
            pendingAcks = 0;   // the packet carries our ack_nr
            if (rtoDeadline == 0)
                rtoDeadline = now + rto;
            context.Send(packet.Buffer, 0, packet.Length, RemoteEndPoint);
        }

        private void SendState(long now)
        {
            var length = UtpWire.HeaderSize;
            var extension = UtpWire.ExtensionNone;
            if (reorderCount > 0)
            {
                // Selective ACK: bit i = ack_nr + 2 + i. Up to 256 packets past the hole, in multiples of 4 bytes.
                Array.Clear(control, UtpWire.HeaderSize, control.Length - UtpWire.HeaderSize);
                var maxBit = -1;
                for (var bit = 0; bit < MaxSelectiveAckBytes * 8; bit++)
                {
                    if (!inRing[(ushort)(ackNr + 2 + bit) & RingMask].Present)
                        continue;
                    control[UtpWire.HeaderSize + 2 + (bit >> 3)] |= (byte)(1 << (bit & 7));
                    maxBit = bit;
                }
                if (maxBit >= 0)
                {
                    var bytes = ((maxBit >> 3) + 4) & ~3;
                    extension = UtpWire.ExtensionSelectiveAck;
                    control[UtpWire.HeaderSize] = UtpWire.ExtensionNone;
                    control[UtpWire.HeaderSize + 1] = (byte)bytes;
                    length += 2 + bytes;
                }
            }
            UtpHeader.Write(control, 0, UtpWire.TypeState, extension, SendId, (uint)now, replyMicro, AdvertisedWindow(), seqNr, ackNr);
            PacketsSent++;
            lastSend = now;
            pendingAcks = 0;
            context.Send(control, 0, length, RemoteEndPoint);
        }

        private uint AdvertisedWindow()
        {
            var free = options.ReceiveBufferBytes - bufferedBytes - reorderBytes;
            if (free < 0)
                free = 0;
            lastAdvertised = free;
            return (uint)free;
        }

        /// Puts as much of data into new DATA packets as the congestion and receive windows allow.
        private int Packetize(ReadOnlySpan<byte> data, long now)
        {
            if (state != UtpConnectionState.Connected || closeRequested || needResendCount > 0)
                return 0;
            var consumed = 0;
            while (consumed < data.Length)
            {
                if (curPackets >= RingSize - 2)   // one slot stays free for the FIN
                {
                    lastMaxedOutWindow = now;
                    break;
                }
                var want = Math.Min(mss, data.Length - consumed);
                var avail = Math.Min(maxWindow, peerWindow) - inFlightBytes;
                if (avail < want)
                {
                    lastMaxedOutWindow = now;
                    if (inFlightBytes == 0 && avail > 0)
                        want = (int)avail;
                    else
                        break;
                }
                var packet = context.RentPacket();
                packet.Type = UtpWire.TypeData;
                packet.Seq = seqNr;
                packet.Payload = want;
                packet.Length = UtpWire.HeaderSize + want;
                data.Slice(consumed, want).CopyTo(packet.Buffer.AsSpan(UtpWire.HeaderSize));
                outRing[seqNr & RingMask] = packet;
                seqNr++;
                curPackets++;
                inFlightBytes += want;
                BytesSent += want;
                SendPacket(packet, now);
                consumed += want;
            }
            return consumed;
        }

        private void TrySendMore(long now)
        {
            if (state != UtpConnectionState.Connected)
                return;
            if (needResendCount > 0)
            {
                var window = Math.Min(maxWindow, Math.Max(peerWindow, mss));
                var first = (ushort)(seqNr - curPackets);
                for (var i = 0; i < curPackets && needResendCount > 0; i++)
                {
                    var packet = outRing[(first + i) & RingMask];
                    if (packet == null || !packet.NeedResend)
                        continue;
                    if (inFlightBytes > 0 && inFlightBytes + packet.Payload > window)
                    {
                        lastMaxedOutWindow = now;
                        break;
                    }
                    SendPacket(packet, now);
                }
            }
            if (writeWaiter != null)
            {
                writeOffset += Packetize(writeBuffer.Span.Slice(writeOffset), now);
                if (writeOffset == writeBuffer.Length)
                {
                    var waiter = writeWaiter;
                    writeWaiter = null;
                    writeBuffer = default;
                    waiter.TrySetResult(true);
                }
            }
            if (closeRequested && !finSent && writeWaiter == null && curPackets < RingSize - 1)
                SendFin(now);
        }

        private void SendFin(long now)
        {
            finSent = true;
            var packet = context.RentPacket();
            packet.Type = UtpWire.TypeFin;
            packet.Seq = seqNr;
            packet.Length = UtpWire.HeaderSize;
            outRing[seqNr & RingMask] = packet;
            seqNr++;
            curPackets++;
            SendPacket(packet, now);
        }

        // =====================================================================================================
        // Timers and batching
        // =====================================================================================================

        internal void Tick(long now)
        {
            lock (gate)
            {
                if (state == UtpConnectionState.Closed)
                    return;
                if (rtoDeadline != 0 && now >= rtoDeadline)
                    OnTimeout(now);
                if (state == UtpConnectionState.Closed)
                    return;
                if (state == UtpConnectionState.Connected && now - lastSend >= options.KeepAliveMicros)
                    SendState(now);
                // Zero window: the window update may have been lost. Probe with one packet after a second.
                if (state == UtpConnectionState.Connected && curPackets == 0 && zeroWindowSince != 0 && now - zeroWindowSince > 1_000_000
                    && (writeWaiter != null || (closeRequested && !finSent)))
                {
                    peerWindow = mss;
                    zeroWindowSince = now;
                    TrySendMore(now);
                }
                if (pendingAcks > 0)
                    SendState(now);
                if (disposed && now - disposedAt > options.LingerMicros)
                    Terminate(UtpCloseReason.Closed);
                // An incoming connection whose peer never followed its SYN (gone, or a spoofed source) has nothing in
                // flight, so no retransmission timer would end it; libutp drops such a CS_SYN_RECV socket at its
                // first timeout. Likewise any connection the peer has been silent on for IdleTimeoutMicros.
                else if (IsIncoming && !peerSpoke && now - lastReceive > options.SynReceivedTimeoutMicros)
                    Terminate(UtpCloseReason.TimedOut);
                else if (now - lastReceive > options.IdleTimeoutMicros)
                {
                    context.SendReset(RemoteEndPoint, SendId, ackNr);
                    Terminate(UtpCloseReason.TimedOut);
                }
            }
        }

        private void OnTimeout(long now)
        {
            if (curPackets == 0)
            {
                rtoDeadline = 0;
                return;
            }
            var limit = state == UtpConnectionState.SynSent ? options.SynRetries : options.DataRetries;
            if (consecutiveTimeouts >= limit)
            {
                if (state == UtpConnectionState.Connected)
                    context.SendReset(RemoteEndPoint, SendId, ackNr);
                Terminate(UtpCloseReason.TimedOut);
                return;
            }
            consecutiveTimeouts++;
            Timeouts++;
            rto = Math.Min(rto * 2, options.MaxRtoMicros);
            if (state == UtpConnectionState.Connected)
            {
                ssthresh = Math.Max(maxWindow / 2, 2L * mss);
                maxWindow = mss;
                slowStart = true;
            }
            // Everything in flight is presumed lost; resend the oldest now and the rest as ACKs open the window.
            var first = (ushort)(seqNr - curPackets);
            OutPacket oldest = null;
            for (var i = 0; i < curPackets; i++)
            {
                var packet = outRing[(first + i) & RingMask];
                if (packet == null || packet.Acked)
                    continue;
                if (!packet.NeedResend)
                {
                    packet.NeedResend = true;
                    needResendCount++;
                    inFlightBytes -= packet.Payload;
                }
                packet.FastResent = false;
                oldest = oldest ?? packet;
            }
            rtoDeadline = now + rto;
            if (oldest != null)
                SendPacket(oldest, now);
            rtoDeadline = now + rto;
        }

        /// End of a receive batch, or a timer: sends ACKs that are due (two or more packets, or one held for
        /// DelayedAckMicros) and completes a partly filled read whose deadline passed. Returns true while something is
        /// still held back, so the context asks again.
        internal bool Flush(long now)
        {
            lock (gate)
            {
                flushScheduled = false;
                if (state == UtpConnectionState.Closed)
                    return false;
                if (pendingAcks >= 2 || (pendingAcks > 0 && now >= ackDeadline))
                    SendState(now);
                if (readWaiter != null && readFilled > 0 && now >= readDeadline)
                    CompleteRead(readFilled);
                if (pendingAcks > 0 || (readWaiter != null && readFilled > 0))
                {
                    flushScheduled = true;
                    return true;
                }
                return false;
            }
        }

        private void ScheduleFlush()
        {
            if (flushScheduled)
                return;
            flushScheduled = true;
            context.ScheduleFlush(this);
        }

        // =====================================================================================================
        // Application API
        // =====================================================================================================

        /// Copies buffered data into destination; returns 0 when nothing is buffered (see IsEndOfStream).
        public int Read(Span<byte> destination)
        {
            lock (gate)
                return ReadLocked(destination);
        }

        private int ReadLocked(Span<byte> destination)
        {
            var copied = 0;
            while (copied < destination.Length && queueCount > 0)
            {
                ref var segment = ref queue[queueHead];
                var n = Math.Min(segment.Count, destination.Length - copied);
                segment.Buffer.AsSpan(segment.Offset, n).CopyTo(destination.Slice(copied));
                copied += n;
                segment.Offset += n;
                segment.Count -= n;
                if (segment.Count == 0)
                {
                    context.Buffers.Return(segment.Buffer);
                    segment = default;
                    queueHead = (queueHead + 1) % queue.Length;
                    queueCount--;
                }
            }
            bufferedBytes -= copied;
            // Window update: tell a sender we stopped (window under half the buffer) that it may go on.
            if (copied > 0 && state == UtpConnectionState.Connected && lastAdvertised < options.ReceiveBufferBytes / 2
                && options.ReceiveBufferBytes - bufferedBytes - reorderBytes >= options.ReceiveBufferBytes / 2)
                SendState(context.NowMicros);
            return copied;
        }

        /// Reads at least one byte, or 0 at end of stream / after Dispose. Faults with SocketException on RESET or timeout.
        public Task<int> ReadAsync(Memory<byte> destination)
        {
            lock (gate)
            {
                if (readWaiter != null)
                    throw new InvalidOperationException("A read is already pending on this uTP connection.");
                if (destination.Length == 0)
                    return Task.FromResult(0);
                if (queueCount > 0)
                    return Task.FromResult(ReadLocked(destination.Span));
                if (readEof || disposed)
                    return Task.FromResult(0);
                if (state == UtpConnectionState.Closed)
                    return Task.FromException<int>(Error());
                readBuffer = destination;
                readFilled = 0;
                readWaiter = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                return readWaiter.Task;
            }
        }

        /// Sends as much as the windows allow right now; returns the bytes taken.
        public int Write(ReadOnlySpan<byte> data)
        {
            lock (gate)
            {
                ThrowIfNotWritable();
                if (writeWaiter != null)
                    return 0;
                return Packetize(data, context.NowMicros);
            }
        }

        /// Completes when every byte is in a packet (sent, not necessarily acknowledged), like a socket send.
        public Task WriteAsync(ReadOnlyMemory<byte> data)
        {
            lock (gate)
            {
                try { ThrowIfNotWritable(); }
                catch (Exception error) { return Task.FromException(error); }
                if (writeWaiter != null)
                    throw new InvalidOperationException("A write is already pending on this uTP connection.");
                var taken = Packetize(data.Span, context.NowMicros);
                if (taken == data.Length)
                    return Task.CompletedTask;
                writeBuffer = data;
                writeOffset = taken;
                writeWaiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return writeWaiter.Task;
            }
        }

        private void ThrowIfNotWritable()
        {
            if (disposed)
                throw new ObjectDisposedException(nameof(UtpConnection));
            if (state == UtpConnectionState.Closed)
                throw Error();
            if (closeRequested)
                throw new InvalidOperationException("The uTP connection is closing.");
        }

        /// Graceful close of the sending side: FIN after the data already written. Reading continues.
        public void Close()
        {
            lock (gate)
            {
                if (closeRequested || state == UtpConnectionState.Closed)
                    return;
                closeRequested = true;
                if (state == UtpConnectionState.SynSent)
                {
                    Terminate(UtpCloseReason.Aborted);
                    return;
                }
                TrySendMore(context.NowMicros);
            }
        }

        /// Ends the connection at once with a RESET.
        public void Abort()
        {
            lock (gate)
            {
                if (state == UtpConnectionState.Closed)
                    return;
                if (state == UtpConnectionState.Connected)
                    context.SendReset(RemoteEndPoint, SendId, ackNr);
                Terminate(UtpCloseReason.Aborted);
            }
        }

        /// Close plus release: pending reads return 0, pending writes fail. The connection lingers (without
        /// buffering received data) until its FIN is acknowledged or LingerMicros pass, so the peer sees a clean end.
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                    return;
                disposed = true;
                disposedAt = context.NowMicros;
                if (writeWaiter != null)
                {
                    var waiter = writeWaiter;
                    writeWaiter = null;
                    writeBuffer = default;
                    waiter.TrySetException(new ObjectDisposedException(nameof(UtpConnection)));
                }
                Close();
                while (queueCount > 0)
                {
                    context.Buffers.Return(queue[queueHead].Buffer);
                    queue[queueHead] = default;
                    queueHead = (queueHead + 1) % queue.Length;
                    queueCount--;
                }
                bufferedBytes = 0;
                if (readWaiter != null)
                    CompleteRead(readFilled);
                MaybeFinishLinger();
            }
        }

        private void MaybeFinishLinger()
        {
            if (disposed && finAcked && state != UtpConnectionState.Closed)
                Terminate(UtpCloseReason.Closed);
        }

        private void CompleteRead(int count)
        {
            var waiter = readWaiter;
            readWaiter = null;
            readBuffer = default;
            readFilled = 0;
            waiter.TrySetResult(count);
        }

        private Exception Error()
        {
            switch (closeReason)
            {
                case UtpCloseReason.Reset: return new SocketException((int)SocketError.ConnectionReset);
                case UtpCloseReason.Refused: return new SocketException((int)SocketError.ConnectionRefused);
                case UtpCloseReason.TimedOut: return new SocketException((int)SocketError.TimedOut);
                case UtpCloseReason.Aborted: return new SocketException((int)SocketError.OperationAborted);
                default: return new ObjectDisposedException(nameof(UtpConnection));
            }
        }

        /// Final state: wakes every waiter, returns every buffer and leaves the context's table.
        private void Terminate(UtpCloseReason reason)
        {
            if (state == UtpConnectionState.Closed)
                return;
            state = UtpConnectionState.Closed;
            closeReason = reason;
            rtoDeadline = 0;
            var error = Error();
            connectWaiter.TrySetException(error);
            if (readWaiter != null)
            {
                var waiter = readWaiter;
                readWaiter = null;
                readBuffer = default;
                if (readFilled > 0 || reason == UtpCloseReason.Closed || readEof)
                    waiter.TrySetResult(readFilled);
                else
                    waiter.TrySetException(error);
            }
            if (writeWaiter != null)
            {
                var waiter = writeWaiter;
                writeWaiter = null;
                writeBuffer = default;
                waiter.TrySetException(error);
            }
            for (var i = 0; i < RingSize; i++)
            {
                if (outRing[i] != null)
                {
                    context.ReturnPacket(outRing[i]);
                    outRing[i] = null;
                }
                if (inRing != null && inRing[i].Buffer != null)
                    context.Buffers.Return(inRing[i].Buffer);
            }
            inRing = null;
            curPackets = 0;
            inFlightBytes = 0;
            needResendCount = 0;
            reorderCount = 0;
            reorderBytes = 0;
            if (disposed)
            {
                while (queueCount > 0)
                {
                    context.Buffers.Return(queue[queueHead].Buffer);
                    queue[queueHead] = default;
                    queueHead = (queueHead + 1) % queue.Length;
                    queueCount--;
                }
                bufferedBytes = 0;
            }
            context.Remove(this);
        }

        /// One-way delay of our packets as reported by the peer, against the minimum seen (libutp's DelayHist):
        /// the base is the smallest sample of the last 13 minutes, the current value the smallest of the last 3.
        private sealed class DelayHistory
        {
            private readonly uint[] baseHistory = new uint[13];
            private readonly uint[] current = new uint[3];
            private int baseIndex;
            private long baseTime;
            private uint delayBase;
            private bool initialised;
            private int currentIndex;
            private int currentCount;

            public bool HasValue => currentCount > 0;

            public long Value
            {
                get
                {
                    var min = uint.MaxValue;
                    for (var i = 0; i < currentCount; i++)
                        if (current[i] < min)
                            min = current[i];
                    return min;
                }
            }

            public void Add(uint sample, long now)
            {
                if (!initialised)
                {
                    for (var i = 0; i < baseHistory.Length; i++)
                        baseHistory[i] = sample;
                    delayBase = sample;
                    baseTime = now;
                    initialised = true;
                }
                if (Less(sample, baseHistory[baseIndex]))
                    baseHistory[baseIndex] = sample;
                if (Less(sample, delayBase))
                    delayBase = sample;
                current[currentIndex] = sample - delayBase;
                currentIndex = (currentIndex + 1) % current.Length;
                if (currentCount < current.Length)
                    currentCount++;
                if (now - baseTime > 60_000_000)
                {
                    baseTime = now;
                    baseIndex = (baseIndex + 1) % baseHistory.Length;
                    baseHistory[baseIndex] = sample;
                    delayBase = baseHistory[0];
                    for (var i = 1; i < baseHistory.Length; i++)
                        if (Less(baseHistory[i], delayBase))
                            delayBase = baseHistory[i];
                }
            }

            private static bool Less(uint a, uint b) => (int)(a - b) < 0;
        }
    }
}
