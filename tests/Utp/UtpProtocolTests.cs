using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Ru7100Probe.Utp;
using Xunit;

namespace Ru7100.Tests.Utp
{
    public class UtpHeaderTests
    {
        [Fact]
        public void HeaderRoundTripsEveryField()
        {
            var buffer = new byte[64];
            UtpHeader.Write(buffer, 0, UtpWire.TypeData, 0, 0xBEEF, 0xFFFFFFF0, 12345, 1 << 20, 0xFFFF, 7);
            Assert.True(UtpHeader.TryParse(buffer, 0, 20, out var header));
            Assert.Equal(UtpWire.TypeData, header.Type);
            Assert.Equal((ushort)0xBEEF, header.ConnectionId);
            Assert.Equal(0xFFFFFFF0u, header.TimestampMicros);
            Assert.Equal(12345u, header.TimestampDifferenceMicros);
            Assert.Equal(1u << 20, header.WindowSize);
            Assert.Equal((ushort)0xFFFF, header.SeqNr);
            Assert.Equal((ushort)7, header.AckNr);
            Assert.Equal(20, header.PayloadOffset);
            Assert.Equal(0, header.PayloadLength);
            // First byte: type in the high nibble, version 1 in the low one (BEP 29).
            Assert.Equal(0x01, buffer[0]);
        }

        [Fact]
        public void SelectiveAckExtensionIsLocatedAndBitsReadLsbFirst()
        {
            var buffer = new byte[64];
            UtpHeader.Write(buffer, 0, UtpWire.TypeState, UtpWire.ExtensionSelectiveAck, 1, 0, 0, 0, 0, 100);
            buffer[20] = 0;       // no further extension
            buffer[21] = 4;       // 4-byte mask
            buffer[22] = 0b0000_0101;   // ack_nr + 2 and ack_nr + 4
            buffer[25] = 0b1000_0000;   // bit 31: ack_nr + 33
            Assert.True(UtpHeader.TryParse(buffer, 0, 26, out var header));
            Assert.Equal(22, header.SelectiveAckOffset);
            Assert.Equal(4, header.SelectiveAckLength);
            Assert.Equal(26, header.PayloadOffset);
            Assert.True(UtpHeader.IsSelectivelyAcked(buffer, header, 0));
            Assert.False(UtpHeader.IsSelectivelyAcked(buffer, header, 1));
            Assert.True(UtpHeader.IsSelectivelyAcked(buffer, header, 2));
            Assert.True(UtpHeader.IsSelectivelyAcked(buffer, header, 31));
            Assert.False(UtpHeader.IsSelectivelyAcked(buffer, header, 32));
        }

        [Fact]
        public void UnknownExtensionsAreSkipped()
        {
            var buffer = new byte[64];
            UtpHeader.Write(buffer, 0, UtpWire.TypeData, 3, 1, 0, 0, 0, 0, 0);   // 3: libtorrent close reason
            buffer[20] = 0;
            buffer[21] = 4;
            buffer[26] = 0xAA;   // payload
            Assert.True(UtpHeader.TryParse(buffer, 0, 27, out var header));
            Assert.Equal(26, header.PayloadOffset);
            Assert.Equal(1, header.PayloadLength);
            Assert.Equal(0, header.SelectiveAckLength);
        }

        [Fact]
        public void MalformedDatagramsAreRejectedWithoutThrowing()
        {
            var buffer = new byte[64];
            UtpHeader.Write(buffer, 0, UtpWire.TypeData, 1, 1, 0, 0, 0, 0, 0);
            Assert.False(UtpHeader.TryParse(buffer, 0, 19, out _));                 // short header
            Assert.False(UtpHeader.TryParse(buffer, 0, 21, out _));                 // extension header cut
            buffer[20] = 0; buffer[21] = 40;
            Assert.False(UtpHeader.TryParse(buffer, 0, 30, out _));                 // extension body past the end
            buffer[0] = 0x02;
            Assert.False(UtpHeader.TryParse(buffer, 0, 64, out _));                 // version 2
            buffer[0] = 0x51;
            Assert.False(UtpHeader.TryParse(buffer, 0, 64, out _));                 // type 5
            Assert.False(UtpWire.LooksLikeUtp(System.Text.Encoding.ASCII.GetBytes("d1:ad2:id20:aaaaaaaaaaaaaaaaaaaae1:q4:ping1:t2:aa1:y1:qe"), 0, 56));
        }

        [Fact]
        public void RandomGarbageNeverThrows()
        {
            var random = new Random(42);
            var buffer = new byte[200];
            var accepted = 0;
            for (var i = 0; i < 200_000; i++)
            {
                random.NextBytes(buffer);
                buffer[0] = (byte)((buffer[0] & 0xF0) | 1);
                var length = random.Next(0, buffer.Length + 1);
                if (UtpHeader.TryParse(buffer, 0, length, out var header))
                {
                    accepted++;
                    Assert.InRange(header.PayloadOffset, 20, length);
                    Assert.Equal(length - header.PayloadOffset, header.PayloadLength);
                    Assert.InRange(header.SelectiveAckOffset + header.SelectiveAckLength, 0, length);
                }
            }
            Assert.True(accepted > 0);
        }

        [Fact]
        public void GarbageIntoAContextNeverThrowsAndStraysGetReset()
        {
            var network = new SimNetwork();
            var a = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 1000);
            var context = network.AddHost(a);
            var random = new Random(3);
            var buffer = new byte[1500];
            var from = new IPEndPoint(IPAddress.Parse("10.0.0.9"), 9);
            for (var i = 0; i < 50_000; i++)
            {
                random.NextBytes(buffer);
                buffer[0] = (byte)((random.Next(0, 5) << 4) | 1);
                buffer[1] = (byte)random.Next(0, 3);
                context.Receive(buffer, 0, random.Next(0, buffer.Length), from);
            }
            context.Tick();
            Assert.True(context.ResetsSent > 0);
        }
    }

    public class UtpConnectionTests
    {
        private static readonly IPEndPoint A = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 6881);
        private static readonly IPEndPoint B = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 6881);

        private static (SimNetwork net, UtpContext a, UtpContext b, List<UtpConnection> accepted) Pair(UtpOptions options = null, int seed = 1, long start = 1_000_000)
        {
            var net = new SimNetwork(seed, start);
            var a = net.AddHost(A, options);
            var b = net.AddHost(B, options);
            var accepted = new List<UtpConnection>();
            b.Accepted += accepted.Add;
            return (net, a, b, accepted);
        }

        private static UtpConnection Connect(SimNetwork net, UtpContext a, List<UtpConnection> accepted)
        {
            var client = a.Connect(B);
            Assert.True(net.RunUntil(() => client.ConnectTask.IsCompleted && accepted.Count > 0, 10));
            Assert.Equal(TaskStatus.RanToCompletion, client.ConnectTask.Status);
            return client;
        }

        [Fact]
        public void HandshakeFollowsLibutpIdAndSequenceRules()
        {
            var (net, a, b, accepted) = Pair(new UtpOptions { InitialSeqNr = 500 });
            var packets = new List<(IPEndPoint from, UtpHeader header)>();
            net.Observer = (from, to, data) =>
            {
                Assert.True(UtpHeader.TryParse(data, 0, data.Length, out var h));
                packets.Add((from, h));
            };
            var client = Connect(net, a, accepted);
            var server = accepted.Single();

            var syn = packets[0];
            Assert.Equal(UtpWire.TypeSyn, syn.header.Type);
            Assert.Equal(client.ReceiveId, syn.header.ConnectionId);          // SYN carries the initiator's recv id
            Assert.Equal((ushort)(client.ReceiveId + 1), client.SendId);
            Assert.Equal((ushort)500, syn.header.SeqNr);
            Assert.Equal(server.ReceiveId, (ushort)(syn.header.ConnectionId + 1));
            Assert.Equal(server.SendId, syn.header.ConnectionId);

            var synAck = packets[1];
            Assert.Equal(B, synAck.from);
            Assert.Equal(UtpWire.TypeState, synAck.header.Type);
            Assert.Equal(server.SendId, synAck.header.ConnectionId);
            Assert.Equal((ushort)500, synAck.header.AckNr);                   // acks the SYN
            Assert.Equal((ushort)500, synAck.header.SeqNr);                   // responder's first seq (fixed in this test)

            // The first DATA from the responder carries the SYN-ACK's seq_nr; the initiator must accept it in order.
            var t = new Transfer(server, new byte[] { 1, 2, 3 }) { Receiver = client };
            Assert.True(net.RunUntil(() => t.Complete, 5, t.Step));
            var data = packets.First(p => p.header.Type == UtpWire.TypeData);
            Assert.Equal((ushort)500, data.header.SeqNr);
            Assert.Equal(client.ReceiveId, data.header.ConnectionId);
        }

        [Theory]
        [InlineData(0.0, 0, 0.0)]
        [InlineData(0.01, 0, 0.0)]
        [InlineData(0.05, 20_000, 0.01)]
        [InlineData(0.10, 40_000, 0.05)]
        public void BulkTransferIsExactUnderLossReorderingAndDuplication(double loss, long jitter, double duplicate)
        {
            var (net, a, b, accepted) = Pair(seed: 11);
            net.SetLinks(A, B, link => { link.DelayMicros = 30_000; link.JitterMicros = jitter; link.Loss = loss; link.Duplicate = duplicate; });
            var client = Connect(net, a, accepted);
            var server = accepted.Single();
            var up = Transfer.Pattern(1_000_000, 1);
            var down = Transfer.Pattern(700_000, 2);
            var t1 = new Transfer(client, up) { Receiver = server };
            var t2 = new Transfer(server, down) { Receiver = client };
            Assert.True(net.RunUntil(() => t1.Complete && t2.Complete, 300, () => { t1.Step(); t2.Step(); }),
                $"up {t1.Received.Length}/{up.Length} down {t2.Received.Length}/{down.Length} {client} | {server}");
            Assert.Equal(up, t1.Received.ToArray());
            Assert.Equal(down, t2.Received.ToArray());
            if (loss > 0)
                Assert.True(client.Retransmits > 0 && server.Retransmits > 0);
        }

        [Fact]
        public void SequenceNumbersWrapAround()
        {
            var (net, a, b, accepted) = Pair(new UtpOptions { InitialSeqNr = 0xFFF0 }, seed: 5);
            net.SetLinks(A, B, link => { link.Loss = 0.03; link.JitterMicros = 10_000; });
            var client = Connect(net, a, accepted);
            var server = accepted.Single();
            var data = Transfer.Pattern(400_000, 3);   // ~290 packets: crosses 0xFFFF -> 0 on both sides
            var t1 = new Transfer(client, data) { Receiver = server };
            var t2 = new Transfer(server, data) { Receiver = client };
            Assert.True(net.RunUntil(() => t1.Complete && t2.Complete, 120, () => { t1.Step(); t2.Step(); }));
            Assert.Equal(data, t1.Received.ToArray());
            Assert.Equal(data, t2.Received.ToArray());
        }

        [Fact]
        public void TimestampsWrapAroundTheThirtyTwoBitClock()
        {
            var (net, a, b, accepted) = Pair(seed: 6, start: 0xFFFF_FFFFL - 2_000_000);
            net.SetLinks(A, B, link => { link.BytesPerSecond = 500_000; link.DelayMicros = 20_000; });
            var client = Connect(net, a, accepted);
            var server = accepted.Single();
            var data = Transfer.Pattern(3_000_000, 4);
            var t = new Transfer(client, data) { Receiver = server };
            Assert.True(net.RunUntil(() => t.Complete, 60, t.Step));
            Assert.Equal(data, t.Received.ToArray());
            // Delay-based control survived the wrap: still sending at a sane window.
            Assert.InRange(client.QueuingDelayMicros, 0, 1_000_000);
        }

        [Fact]
        public void ConnectToSilentHostTimesOut()
        {
            var (net, a, b, accepted) = Pair();
            net.Link(A, B).Down = true;
            var client = a.Connect(B);
            Assert.True(net.RunUntil(() => client.ConnectTask.IsCompleted, 30));
            var error = Assert.IsType<SocketException>(client.ConnectTask.Exception.InnerException);
            Assert.Equal(SocketError.TimedOut, error.SocketErrorCode);
            Assert.Equal(0, a.ConnectionCount);
            // SYN + 3 retransmissions at 1, 2 and 4 s.
            Assert.InRange(net.NowMicros - 1_000_000, 7_000_000, 16_000_000);
        }

        [Fact]
        public void ConnectToRefusingHostFailsFast()
        {
            var (net, a, b, accepted) = Pair();
            b.AcceptIncoming = false;
            var client = a.Connect(B);
            Assert.True(net.RunUntil(() => client.ConnectTask.IsCompleted, 1));
            var error = Assert.IsType<SocketException>(client.ConnectTask.Exception.InnerException);
            Assert.Equal(SocketError.ConnectionRefused, error.SocketErrorCode);
            Assert.Equal(0, a.ConnectionCount);
        }

        [Fact]
        public void LostSynAckIsRepairedBySynRetransmission()
        {
            var (net, a, b, accepted) = Pair();
            var dropped = 0;
            net.Link(B, A).Down = true;
            var client = a.Connect(B);
            net.Run(0.5);
            net.Link(B, A).Down = false;
            Assert.True(net.RunUntil(() => client.ConnectTask.IsCompleted, 5));
            Assert.Equal(TaskStatus.RanToCompletion, client.ConnectTask.Status);
            Assert.Single(accepted);   // the retried SYN reached the same connection
            Assert.Equal(0, dropped);
        }

        [Fact]
        public void PeerVanishingMidTransferTimesOutWithAnError()
        {
            var (net, a, b, accepted) = Pair(new UtpOptions { DataRetries = 3 });
            net.SetLinks(A, B, link => link.BytesPerSecond = 200_000);
            var client = Connect(net, a, accepted);
            var server = accepted.Single();
            var t = new Transfer(client, Transfer.Pattern(2_000_000, 1)) { Receiver = server };
            net.Run(0.5, t.Step);
            net.SetLinks(A, B, link => link.Down = true);
            var read = client.ReadAsync(new byte[10]);
            Assert.True(net.RunUntil(() => client.State == UtpConnectionState.Closed, 120, t.Step), client.ToString());
            Assert.Equal(UtpCloseReason.TimedOut, client.CloseReason);
            Assert.True(read.IsFaulted);
            Assert.Throws<SocketException>(() => client.Write(new byte[1]));
            Assert.Equal(0, a.ConnectionCount);
        }

        [Fact]
        public void FinDeliversEverythingThenEndOfStream()
        {
            var (net, a, b, accepted) = Pair();
            net.SetLinks(A, B, link => { link.Loss = 0.05; link.JitterMicros = 20_000; });
            var client = Connect(net, a, accepted);
            var server = accepted.Single();
            var data = Transfer.Pattern(300_000, 9);
            var t = new Transfer(client, data) { Receiver = server, CloseWhenDone = true };
            Assert.True(net.RunUntil(() => t.Complete && server.IsEndOfStream, 60, t.Step));
            Assert.Equal(data, t.Received.ToArray());
            var eof = server.ReadAsync(new byte[16]);
            Assert.True(eof.IsCompleted);
            Assert.Equal(0, eof.Result);
            // Both sides close; the FINs are acknowledged and the connections leave their contexts.
            client.Dispose();
            server.Dispose();
            Assert.True(net.RunUntil(() => a.ConnectionCount == 0 && b.ConnectionCount == 0, 30));
            Assert.Equal(UtpCloseReason.Closed, client.CloseReason);
        }

        [Fact]
        public void PendingReadCompletesWithEndOfStreamOnFin()
        {
            var (net, a, b, accepted) = Pair();
            var client = Connect(net, a, accepted);
            var server = accepted.Single();
            var read = server.ReadAsync(new byte[100]);
            client.Close();
            Assert.True(net.RunUntil(() => read.IsCompleted, 5));
            Assert.Equal(0, read.Result);
        }

        [Fact]
        public void ResetFailsPendingOperations()
        {
            var (net, a, b, accepted) = Pair();
            var client = Connect(net, a, accepted);
            var server = accepted.Single();
            var read = server.ReadAsync(new byte[100]);
            client.Abort();
            Assert.True(net.RunUntil(() => read.IsCompleted, 5));
            Assert.True(read.IsFaulted);
            Assert.Equal(SocketError.ConnectionReset, ((SocketException)read.Exception.InnerException).SocketErrorCode);
            Assert.Equal(UtpCloseReason.Reset, server.CloseReason);
            Assert.Equal(0, b.ConnectionCount);
        }

        [Fact]
        public void PacketsForUnknownConnectionsAreAnsweredWithReset()
        {
            var (net, a, b, accepted) = Pair();
            var client = Connect(net, a, accepted);
            var server = accepted.Single();
            server.Abort();   // b forgets the connection without telling a (its RESET is dropped)
            net.Link(B, A).Down = true;
            net.Run(0.2);
            net.Link(B, A).Down = false;
            var t = new Transfer(client, new byte[5000]);
            Assert.True(net.RunUntil(() => client.State == UtpConnectionState.Closed, 5, t.Step));
            Assert.Equal(UtpCloseReason.Reset, client.CloseReason);
            Assert.True(b.ResetsSent >= 1);
        }

        [Fact]
        public void AcceptedConnectionWhosePeerNeverFollowsItsSynIsDropped()
        {
            var (net, a, b, accepted) = Pair();
            var client = Connect(net, a, accepted);   // the initiator says nothing after the handshake
            var server = accepted.Single();
            var read = server.ReadAsync(new byte[68]);
            Assert.True(net.RunUntil(() => b.ConnectionCount == 0, 10));
            Assert.Equal(UtpCloseReason.TimedOut, server.CloseReason);
            Assert.True(read.IsFaulted);   // the engine's pending handshake read ends instead of hanging
        }

        [Fact]
        public void SilentConnectionsAreResetAfterTheIdleTimeout()
        {
            var options = new UtpOptions { IdleTimeoutMicros = 40_000_000 };
            var (net, a, b, accepted) = Pair(options);
            var client = Connect(net, a, accepted);
            var t = new Transfer(client, new byte[1000]) { Receiver = accepted.Single() };
            Assert.True(net.RunUntil(() => t.Complete, 5, t.Step));
            net.SetLinks(A, B, link => link.Down = true);   // nothing in flight: only the idle timer can notice
            Assert.True(net.RunUntil(() => a.ConnectionCount == 0 && b.ConnectionCount == 0, 50));
            Assert.Equal(UtpCloseReason.TimedOut, client.CloseReason);
        }

        [Fact]
        public void IdleConnectionsSendKeepAlives()
        {
            var (net, a, b, accepted) = Pair();
            var client = Connect(net, a, accepted);
            client.Write(new byte[] { 19 });   // a real initiator speaks first (else the acceptor drops it)
            net.Run(1);
            var before = client.PacketsSent;
            net.Run(65);
            Assert.InRange(client.PacketsSent - before, 2, 3);
            Assert.Equal(UtpConnectionState.Connected, client.State);
            Assert.Equal(UtpConnectionState.Connected, accepted.Single().State);
        }

        [Fact]
        public void ZeroWindowStallsTheSenderUntilTheReaderDrains()
        {
            var options = new UtpOptions { ReceiveBufferBytes = 64 * 1024 };
            var (net, a, b, accepted) = Pair(options);
            var client = Connect(net, a, accepted);
            var server = accepted.Single();
            var data = Transfer.Pattern(500_000, 12);
            var t = new Transfer(client, data) { Receiver = server, ReceiverReads = false };
            net.Run(3, t.Step);
            Assert.InRange(server.Available, 1, 64 * 1024);   // bounded by the advertised window
            Assert.False(t.AllWritten);
            t.ReceiverReads = true;
            Assert.True(net.RunUntil(() => t.Complete, 30, t.Step));
            Assert.Equal(data, t.Received.ToArray());
        }

        [Fact]
        public void LedbatKeepsQueuingDelayNearTargetAndFillsTheBottleneck()
        {
            var (net, a, b, accepted) = Pair();
            net.SetLinks(A, B, link => { link.DelayMicros = 20_000; link.BytesPerSecond = 1_000_000; });
            var client = Connect(net, a, accepted);
            var server = accepted.Single();
            var data = Transfer.Pattern(25_000_000, 13);
            var t = new Transfer(client, data) { Receiver = server };
            net.Run(10, t.Step);   // ramp up
            var startBytes = t.Received.Length;
            var delays = new List<long>();
            var start = net.NowMicros;
            net.Run(15, () => { t.Step(); delays.Add(net.LastQueuingMicros); });
            var rate = (t.Received.Length - startBytes) * 1_000_000.0 / (net.NowMicros - start);
            delays.Sort();
            var median = delays[delays.Count / 2];
            Assert.True(rate > 800_000, $"throughput {rate:0} B/s");
            Assert.True(median < 150_000 && median > 20_000, $"median queuing {median} us, cwnd {client.CongestionWindow}");
        }

        [Fact]
        public void LedbatYieldsToQueueBuiltByOthers()
        {
            // A bottleneck already holding 300 ms of someone else's queue: LEDBAT backs off to a small window.
            var (net, a, b, accepted) = Pair();
            net.SetLinks(A, B, link => { link.DelayMicros = 20_000; link.BytesPerSecond = 1_000_000; });
            var client = Connect(net, a, accepted);
            var server = accepted.Single();
            var t = new Transfer(client, Transfer.Pattern(30_000_000, 14)) { Receiver = server };
            net.Run(10, t.Step);
            var unloaded = client.CongestionWindow;
            var link = net.Link(A, B);
            net.Run(10, () =>
            {
                link.BusyUntil = Math.Max(link.BusyUntil, net.NowMicros + 300_000);   // competing traffic
                t.Step();
            });
            Assert.True(client.CongestionWindow < unloaded / 2, $"cwnd {client.CongestionWindow} vs {unloaded}");
        }

        [Fact]
        public void ManyConnectionsShareOneContext()
        {
            var (net, a, b, accepted) = Pair(seed: 21);
            net.SetLinks(A, B, link => { link.Loss = 0.02; link.JitterMicros = 5_000; });
            var clients = Enumerable.Range(0, 20).Select(_ => a.Connect(B)).ToList();
            Assert.True(net.RunUntil(() => accepted.Count == 20 && clients.All(c => c.ConnectTask.IsCompleted), 10));
            var transfers = clients.Select((c, i) => new Transfer(c, Transfer.Pattern(50_000, 100 + i))
            {
                Receiver = accepted.Single(s => s.SendId == c.ReceiveId)
            }).ToList();
            Assert.True(net.RunUntil(() => transfers.All(x => x.Complete), 60, () => transfers.ForEach(x => x.Step())));
            for (var i = 0; i < transfers.Count; i++)
                Assert.Equal(Transfer.Pattern(50_000, 100 + i), transfers[i].Received.ToArray());
        }

        [Fact]
        public void AsyncReadCompletesAtBatchEndAndWriteWaitsForWindow()
        {
            var (net, a, b, accepted) = Pair(new UtpOptions { ReceiveBufferBytes = 32 * 1024 });
            var client = Connect(net, a, accepted);
            var server = accepted.Single();
            var data = Transfer.Pattern(200_000, 30);
            var write = client.WriteAsync(data);
            Assert.False(write.IsCompleted);   // more than the window: waits for ACKs
            var received = new System.IO.MemoryStream();
            var buffer = new byte[16 * 1024];
            Task<int> read = null;
            Assert.True(net.RunUntil(() => received.Length == data.Length, 30, () =>
            {
                if (read != null && read.IsCompleted)
                {
                    received.Write(buffer, 0, read.Result);
                    read = null;
                }
                if (read == null && received.Length < data.Length)
                    read = server.ReadAsync(buffer);
            }));
            Assert.True(write.IsCompleted);
            Assert.Equal(data, received.ToArray());
        }
    }

    public class UtpUdpSocketTests
    {
        [Fact]
        public async Task LoopbackTransferInBothDirectionsOverRealUdp()
        {
            using var server = new UtpUdpSocket(new IPEndPoint(IPAddress.Loopback, 0));
            using var client = new UtpUdpSocket(new IPEndPoint(IPAddress.Loopback, 0));
            var accepted = new TaskCompletionSource<UtpConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.Context.Accepted += c => accepted.TrySetResult(c);
            var other = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            server.OtherDatagram = (buffer, offset, count, from) => other.TrySetResult(System.Text.Encoding.ASCII.GetString(buffer, offset, count));
            server.Start();
            client.Start();

            var outgoing = client.Context.Connect(server.LocalEndPoint);
            await outgoing.ConnectTask.WaitAsync(TimeSpan.FromSeconds(5));
            var incoming = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var up = new UtpStream(outgoing);
            using var down = new UtpStream(incoming);

            var payload = Transfer.Pattern(4_000_000, 77);
            async Task<byte[]> ReadAll(UtpStream stream, int length)
            {
                var result = new byte[length];
                var read = 0;
                while (read < length)
                {
                    var n = await stream.ReadAsync(result, read, Math.Min(64 * 1024, length - read));
                    Assert.True(n > 0);
                    read += n;
                }
                return result;
            }
            var send1 = up.WriteAsync(payload, 0, payload.Length);
            var send2 = down.WriteAsync(payload, 0, payload.Length);
            var got1 = ReadAll(down, payload.Length);
            var got2 = ReadAll(up, payload.Length);
            await Task.WhenAll(send1, send2, got1, got2).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(payload, got1.Result);
            Assert.Equal(payload, got2.Result);

            // Non-uTP datagrams on the same port (the DHT) are handed on untouched.
            await client.SendAsync(System.Text.Encoding.ASCII.GetBytes("d1:y1:qe"), server.LocalEndPoint);
            Assert.Equal("d1:y1:qe", await other.Task.WaitAsync(TimeSpan.FromSeconds(5)));

            outgoing.Close();
            var eof = await down.ReadAsync(new byte[10], 0, 10).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, eof);
        }
    }
}
