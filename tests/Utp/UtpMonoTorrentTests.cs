using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using MonoTorrent.Connections;
using MonoTorrent.Connections.Peer;
using Ru7100Probe;
using Ru7100Probe.Utp;
using Xunit;

namespace Ru7100.Tests.Utp
{
    public class UtpMonoTorrentTests
    {
        [Fact]
        public async Task RaceFallsToUtpWhenTcpIsRefusedAndListenerRaisesTheUtpConnection()
        {
            var clientDiagnostics = new ConnectionDiagnostics();
            var serverDiagnostics = new ConnectionDiagnostics();
            using var client = new UtpTransport(new IPEndPoint(IPAddress.Any, 0), clientDiagnostics);
            using var server = new UtpTransport(new IPEndPoint(IPAddress.Any, 0), serverDiagnostics);
            // No TCP listener on the server's port: the TCP leg is refused, uTP must carry the connection.
            var tcpListener = new PeerConnectionListener(new IPEndPoint(IPAddress.Loopback, 0));
            var listener = new DualPeerConnectionListener(tcpListener, server, serverDiagnostics);
            var received = new TaskCompletionSource<IPeerConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
            listener.ConnectionReceived += (_, args) => received.TrySetResult(args.Connection);
            listener.Start();
            try
            {
                var uri = new Uri("ipv4://127.0.0.1:" + server.LocalEndPoint.Port);
                using var outgoing = new RacingPeerConnection(uri, client, clientDiagnostics);
                await outgoing.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal("utp", outgoing.Transport);
                var incoming = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(incoming.IsIncoming);
                Assert.False(incoming.CanReconnect);
                Assert.Equal("ipv4", incoming.Uri.Scheme);

                var handshake = new byte[68];
                handshake[0] = 19;
                Encoding.ASCII.GetBytes("BitTorrent protocol").CopyTo(handshake, 1);
                Assert.Equal(68, await outgoing.SendAsync(handshake).AsTask());
                var buffer = new byte[68];
                var read = 0;
                while (read < 68)
                    read += await incoming.ReceiveAsync(buffer.AsMemory(read)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(handshake, buffer);
                await incoming.SendAsync(handshake).AsTask();
                read = 0;
                while (read < 68)
                    read += await outgoing.ReceiveAsync(buffer.AsMemory(read)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Contains("utp=1/1", clientDiagnostics.Describe());
                Assert.Contains("won=tcp:0/utp:1", clientDiagnostics.Describe());
                Assert.Equal(1, clientDiagnostics.UtpPeers);
                Assert.Equal(1, serverDiagnostics.UtpPeers);
                Assert.Contains("utpIn=1", serverDiagnostics.Describe());

                incoming.Dispose();
                Assert.Equal(0, await outgoing.ReceiveAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));   // FIN
            }
            finally { listener.Stop(); }
        }

        [Fact]
        public async Task RaceAgainstADeadAddressFailsWhenDisposed()
        {
            var diagnostics = new ConnectionDiagnostics();
            using var client = new UtpTransport(new IPEndPoint(IPAddress.Any, 0), diagnostics);
            // TEST-NET-1: never answers. The engine's connect timeout disposes the connection.
            var outgoing = new RacingPeerConnection(new Uri("ipv4://192.0.2.1:6881"), client, diagnostics);
            var connect = outgoing.ConnectAsync().AsTask();
            await Task.Delay(300);
            outgoing.Dispose();
            await Assert.ThrowsAnyAsync<Exception>(() => connect.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, client.Context.ConnectionCount);
        }

        [Fact]
        public async Task RefusedTcpGivesUtpOnlyAShortBudget()
        {
            var diagnostics = new ConnectionDiagnostics();
            using var client = new UtpTransport(new IPEndPoint(IPAddress.Any, 0), diagnostics) { UtpAloneBudget = TimeSpan.FromSeconds(1) };
            // A closed port on this host: TCP is refused at once and nothing answers the uTP SYN. The attempt must give
            // up after the budget, not hold an engine half-open slot until the engine's connect timeout.
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var port = ((IPEndPoint)probe.LocalEndPoint).Port;   // UDP bound but silent; TCP closed
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var outgoing = new RacingPeerConnection(new Uri("ipv4://127.0.0.1:" + port), client, diagnostics);
            await Assert.ThrowsAnyAsync<Exception>(() => outgoing.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.InRange(clock.Elapsed.TotalSeconds, 0.5, 3);
        }

        [Fact]
        public async Task SharedSocketHandsBencodedDatagramsToTheDht()
        {
            using var transport = new UtpTransport(new IPEndPoint(IPAddress.Any, 0), null);
            var dht = transport.CreateDhtListener();
            var got = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            dht.MessageReceived += (data, from) => got.TrySetResult(Encoding.ASCII.GetString(data.Span));
            dht.Start();
            Assert.Equal(ListenerStatus.Listening, dht.Status);
            Assert.Equal(transport.LocalEndPoint.Port, dht.LocalEndPoint.Port);
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var target = new IPEndPoint(IPAddress.Loopback, transport.LocalEndPoint.Port);
            await udp.SendAsync(new byte[] { 0x41, 0x01, 0, 0 }, 4, target);                // junk: dropped
            await udp.SendAsync(Encoding.ASCII.GetBytes("d1:y1:qe"), 8, target);
            Assert.Equal("d1:y1:qe", await got.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            // And the DHT's replies leave from the same port.
            await dht.SendAsync(Encoding.ASCII.GetBytes("d1:y1:re"), (IPEndPoint)udp.Client.LocalEndPoint);
            var reply = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(transport.LocalEndPoint.Port, reply.RemoteEndPoint.Port);
            dht.Stop();
        }
    }
}
