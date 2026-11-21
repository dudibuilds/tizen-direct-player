using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent.PortForwarding;
using Ru7100Probe;
using Xunit;

namespace Ru7100.Tests.Playback
{
    public class PortSetupTests
    {
        [Fact]
        public void ATakenPortFallsBackToTheNextFixedOne()
        {
            using (var busy = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                busy.Bind(new IPEndPoint(IPAddress.Any, 0));
                busy.Listen(1);
                var taken = ((IPEndPoint)busy.LocalEndPoint).Port;
                var free = FreePort();
                var log = "";
                var ports = ListenPorts.Choose(new[] { taken, free }, line => log += line + "\n");
                Assert.Equal(free, ports.Tcp4);
                Assert.Equal(free, ports.Udp);
                Assert.Contains("port " + taken + " is taken", log);
            }
        }

        [Fact]
        public void NoFreeFixedPortMeansRandomPorts()
        {
            var ports = ListenPorts.Choose(Array.Empty<int>(), null);
            Assert.Equal(0, ports.Tcp4);
            Assert.Equal(0, ports.Udp);
        }

        [Fact]
        public async Task AForwarderThatCannotBeCreatedNeverThrowsIntoTheEngine()
        {
            var forwarder = new SafePortForwarder(() => throw new TypeInitializationException("Mono.Nat.NatUtility", new SocketException()), null);
            await forwarder.StartAsync(CancellationToken.None);
            await forwarder.RegisterMappingAsync(new Mapping(Protocol.Tcp, 27492));
            await forwarder.UnregisterMappingAsync(new Mapping(Protocol.Tcp, 27492), CancellationToken.None);
            await forwarder.StopAsync(CancellationToken.None);
            Assert.False(forwarder.Active);
            Assert.Same(Mappings.Empty, forwarder.Mappings);
            Assert.StartsWith("unavailable", forwarder.Describe());
        }

        [Fact]
        public async Task AForwarderThatThrowsOnStartDegradesToNoForwarding()
        {
            var forwarder = new SafePortForwarder(() => new ThrowingForwarder(), null);
            await forwarder.StartAsync(CancellationToken.None);
            await forwarder.RegisterMappingAsync(new Mapping(Protocol.Udp, 27492));
            Assert.StartsWith("failed to start", forwarder.Describe());
        }

        [Theory]
        [InlineData("100.72.1.9", "cgnat")]
        [InlineData("192.168.1.20", "private (double NAT)")]
        [InlineData("10.0.0.2", "private (double NAT)")]
        [InlineData("203.0.113.10", "public")]
        public void RouterExternalAddressesAreClassified(string address, string kind) =>
            Assert.Equal(kind, SafePortForwarder.Classify(IPAddress.Parse(address)));

        private static int FreePort()
        {
            using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                return ((IPEndPoint)probe.LocalEndPoint).Port;
            }
        }

        private sealed class ThrowingForwarder : IPortForwarder
        {
            public event EventHandler MappingsChanged { add { } remove { } }
            public bool Active => throw new SocketException();
            public Mappings Mappings => throw new SocketException();
            public Task RegisterMappingAsync(Mapping mapping) => throw new SocketException();
            public Task UnregisterMappingAsync(Mapping mapping, CancellationToken token) => throw new SocketException();
            public Task StartAsync(CancellationToken token) => throw new SocketException();
            public Task StopAsync(CancellationToken token) => throw new SocketException();
            public Task StopAsync(bool removeExistingMappings, CancellationToken token) => throw new SocketException();
        }
    }
}
