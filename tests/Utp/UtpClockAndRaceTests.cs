using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Ru7100Probe;
using Ru7100Probe.Utp;
using Xunit;

namespace Ru7100.Tests.Utp
{
    public class UtpClockAndRaceTests
    {
        [Theory]
        [InlineData(1_000_000_000L)]   // 1 GHz (ARM Linux): ticks * 1e6 used to wrap after ~2 h 34 min
        [InlineData(24_000_000L)]
        [InlineData(10_000_000L)]
        [InlineData(1_000_000L)]
        public void MicrosecondsStayMonotonicAcrossTheOldOverflowPoint(long frequency)
        {
            var boundary = long.MaxValue / 1_000_000;   // the first tick count whose * 1e6 overflowed
            long previous = -1;
            foreach (var ticks in new[] { boundary - frequency, boundary - 1, boundary, boundary + 1, boundary + frequency, boundary * 4 })
            {
                var micros = UtpTime.ToMicros(ticks, frequency);
                Assert.True(micros >= previous && micros >= 0, $"{ticks} ticks at {frequency} Hz gave {micros}");
                Assert.Equal((long)((decimal)ticks * 1_000_000 / frequency), micros);
                previous = micros;
            }
            var day = 86_400L * frequency;
            Assert.Equal(86_400_000_000L, UtpTime.ToMicros(day, frequency));
            Assert.Equal(1_500_000L, UtpTime.ToMicros(frequency * 3 / 2, frequency));
        }

        [Fact]
        public void ExpectedTeardownIsKeptOutOfTheCrashFile()
        {
            Assert.True(CrashFilter.IsExpected(new AggregateException(new SocketException((int)SocketError.OperationAborted))));
            Assert.True(CrashFilter.IsExpected(new AggregateException(new TaskCanceledException(), new ObjectDisposedException("x"))));
            Assert.True(CrashFilter.IsExpected(new AggregateException(new System.IO.IOException("read", new SocketException((int)SocketError.OperationAborted)))));
            Assert.False(CrashFilter.IsExpected(new AggregateException(new SocketException((int)SocketError.OperationAborted), new ArgumentNullException("key"))));
            Assert.False(CrashFilter.IsExpected(new AggregateException(new SocketException((int)SocketError.ConnectionReset))));
            Assert.False(CrashFilter.IsExpected(new ArgumentNullException("collection")));
        }

        /// TCP wins against a silent uTP peer: the disposed uTP leg faults with OperationAborted and must be observed.
        [Fact]
        public async Task TheLosingUtpLegIsObserved()
        {
            var aborted = 0;
            EventHandler<UnobservedTaskExceptionEventArgs> watch = (s, e) =>
            {
                foreach (var inner in e.Exception.Flatten().InnerExceptions)
                    if (inner is SocketException socket && socket.SocketErrorCode == SocketError.OperationAborted)
                        Interlocked.Increment(ref aborted);
            };
            TaskScheduler.UnobservedTaskException += watch;
            try
            {
                var diagnostics = new ConnectionDiagnostics();
                using (var client = new UtpTransport(new IPEndPoint(IPAddress.Any, 0), diagnostics))
                {
                    // A fresh port per round: the transport remembers a peer whose TCP connection closed before the
                    // BitTorrent handshake and would race uTP alone next time.
                    for (var i = 0; i < 5; i++)
                    {
                        var tcp = new TcpListener(IPAddress.Loopback, 0);
                        tcp.Start();
                        try
                        {
                            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
                            using (var outgoing = new RacingPeerConnection(new Uri("ipv4://127.0.0.1:" + port), client, diagnostics))
                            {
                                var accept = tcp.AcceptSocketAsync();
                                await outgoing.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                                Assert.Equal("tcp", outgoing.Transport);
                                (await accept).Dispose();
                            }
                        }
                        finally { tcp.Stop(); }
                    }
                }
                await Task.Delay(200);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Assert.Equal(0, aborted);
            }
            finally { TaskScheduler.UnobservedTaskException -= watch; }
        }
    }
}
