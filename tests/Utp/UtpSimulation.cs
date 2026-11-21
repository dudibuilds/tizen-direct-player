using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Ru7100Probe.Utp;

namespace Ru7100.Tests.Utp
{
    /// Link behaviour for one direction of a simulated path.
    internal sealed class SimLink
    {
        public long DelayMicros { get; set; } = 25_000;
        public long JitterMicros { get; set; }
        public double Loss { get; set; }
        public double Duplicate { get; set; }
        /// Bottleneck rate; 0 = unlimited. Packets queue behind it (FIFO), which is what LEDBAT measures.
        public long BytesPerSecond { get; set; }
        /// Tail-drop limit of the bottleneck queue; 0 = unlimited.
        public long QueueLimitBytes { get; set; }
        /// Drop everything (a peer that vanished).
        public bool Down { get; set; }
        internal long BusyUntil;
    }

    /// A deterministic network on a simulated clock: datagrams are delivered by an event queue with per-direction
    /// delay, jitter (reordering), loss, duplication and bottleneck queuing. Single-threaded, seeded.
    internal sealed class SimNetwork : IUtpClock
    {
        private readonly PriorityQueue<Delivery, (long, long)> events = new PriorityQueue<Delivery, (long, long)>();
        private readonly Dictionary<IPEndPoint, UtpContext> hosts = new Dictionary<IPEndPoint, UtpContext>();
        private readonly Dictionary<(IPEndPoint, IPEndPoint), SimLink> links = new Dictionary<(IPEndPoint, IPEndPoint), SimLink>();
        private readonly Random random;
        private long order;
        private long nextTick;

        public SimNetwork(int seed = 1, long startMicros = 1_000_000)
        {
            random = new Random(seed);
            NowMicros = startMicros;
            nextTick = startMicros;
        }

        public long NowMicros { get; private set; }
        public long TickMicros { get; set; } = 10_000;
        public long Datagrams;
        public long Dropped;
        public long QueueDropped;
        /// Called for each delivered datagram (inspection in tests).
        public Action<IPEndPoint, IPEndPoint, byte[]> Observer { get; set; }
        /// Queuing delay seen by the last datagram that crossed a bottleneck.
        public long LastQueuingMicros;

        public UtpContext AddHost(IPEndPoint endPoint, UtpOptions options = null, int seed = 0)
        {
            var context = new UtpContext(this, new Sink(this, endPoint), options, new Random(seed == 0 ? hosts.Count + 7 : seed));
            hosts.Add(endPoint, context);
            return context;
        }

        public SimLink Link(IPEndPoint from, IPEndPoint to)
        {
            if (!links.TryGetValue((from, to), out var link))
                links[(from, to)] = link = new SimLink();
            return link;
        }

        public void SetLinks(IPEndPoint a, IPEndPoint b, Action<SimLink> configure)
        {
            configure(Link(a, b));
            configure(Link(b, a));
        }

        /// Runs until done() holds (checked after every step) or the simulated time limit passes; returns done().
        public bool RunUntil(Func<bool> done, double seconds, Action step = null)
        {
            var limit = NowMicros + (long)(seconds * 1_000_000);
            while (NowMicros < limit)
            {
                if (done())
                    return true;
                var next = nextTick;
                if (events.TryPeek(out _, out var priority) && priority.Item1 < next)
                    next = priority.Item1;
                NowMicros = Math.Max(NowMicros, next);
                var delivered = new HashSet<UtpContext>();
                while (events.TryPeek(out var delivery, out var when) && when.Item1 <= NowMicros)
                {
                    events.Dequeue();
                    Observer?.Invoke(delivery.From, delivery.To, delivery.Data);
                    if (hosts.TryGetValue(delivery.To, out var host))
                    {
                        host.Receive(delivery.Data, 0, delivery.Data.Length, delivery.From);
                        delivered.Add(host);
                    }
                }
                foreach (var host in delivered)
                    host.Flush();
                if (NowMicros >= nextTick)
                {
                    foreach (var host in hosts.Values)
                        host.Tick();
                    nextTick += TickMicros;
                }
                step?.Invoke();
            }
            return done();
        }

        public void Run(double seconds, Action step = null) => RunUntil(() => false, seconds, step);

        private void Enqueue(IPEndPoint from, IPEndPoint to, byte[] buffer, int offset, int count)
        {
            Datagrams++;
            var link = Link(from, to);
            if (link.Down || (link.Loss > 0 && random.NextDouble() < link.Loss))
            {
                Dropped++;
                return;
            }
            var copies = link.Duplicate > 0 && random.NextDouble() < link.Duplicate ? 2 : 1;
            var data = new byte[count];
            Buffer.BlockCopy(buffer, offset, data, 0, count);
            var departure = NowMicros;
            if (link.BytesPerSecond > 0)
            {
                var start = Math.Max(link.BusyUntil, NowMicros);
                var queued = (start - NowMicros) * link.BytesPerSecond / 1_000_000;
                if (link.QueueLimitBytes > 0 && queued > link.QueueLimitBytes)
                {
                    QueueDropped++;
                    return;
                }
                link.BusyUntil = start + count * 1_000_000L / link.BytesPerSecond;
                departure = link.BusyUntil;
                LastQueuingMicros = start - NowMicros;
            }
            for (var i = 0; i < copies; i++)
            {
                var jitter = link.JitterMicros > 0 ? (long)(random.NextDouble() * link.JitterMicros) : 0;
                events.Enqueue(new Delivery { From = from, To = to, Data = data }, (departure + link.DelayMicros + jitter, order++));
            }
        }

        private sealed class Delivery
        {
            public IPEndPoint From;
            public IPEndPoint To;
            public byte[] Data;
        }

        private sealed class Sink : IUtpDatagramSink
        {
            private readonly SimNetwork network;
            private readonly IPEndPoint self;

            public Sink(SimNetwork network, IPEndPoint self)
            {
                this.network = network;
                this.self = self;
            }

            public void Send(byte[] buffer, int offset, int count, IPEndPoint remote) => network.Enqueue(self, remote, buffer, offset, count);
        }
    }

    /// Pumps a byte array through a connection with the synchronous API and collects what the other side reads.
    internal sealed class Transfer
    {
        private readonly UtpConnection sender;
        private readonly byte[] data;
        private int written;
        private readonly byte[] scratch = new byte[64 * 1024];
        public readonly MemoryStream Received = new MemoryStream();
        public UtpConnection Receiver;
        public bool CloseWhenDone;
        public bool ReceiverReads = true;

        public Transfer(UtpConnection sender, byte[] data)
        {
            this.sender = sender;
            this.data = data;
        }

        public bool AllWritten => written == data.Length;
        public bool Complete => Received.Length == data.Length;

        public void Step()
        {
            if (written < data.Length && sender.State == UtpConnectionState.Connected)
            {
                written += sender.Write(data.AsSpan(written));
                if (written == data.Length && CloseWhenDone)
                    sender.Close();
            }
            if (Receiver != null && ReceiverReads)
            {
                int n;
                while ((n = Receiver.Read(scratch)) > 0)
                    Received.Write(scratch, 0, n);
            }
        }

        public static byte[] Pattern(int length, int seed)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }
    }
}
