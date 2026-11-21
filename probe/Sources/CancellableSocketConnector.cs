using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent.Connections;
using ReusableTasks;

namespace Ru7100Probe
{
    /// Outgoing TCP connect that honours cancellation on every runtime. On the TV (Tizen 5, .NET Core 2.x) the
    /// stock MonoTorrent connector's cancellation did not abort a pending connect: attempts to dead peers sat
    /// for the kernel's SYN timeout (~2 min) and held every half-open slot, so live peers were never tried.
    /// Here a cancel closes the socket and the attempt is abandoned at once.
    internal sealed class CancellableSocketConnector : ISocketConnector
    {
        public async ReusableTask<Socket> ConnectAsync(Uri uri, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var endPoint = new IPEndPoint(IPAddress.Parse(uri.Host.Trim('[', ']')), uri.Port);
            var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var connect = socket.ConnectAsync(endPoint);
                using (token.Register(() =>
                {
                    cancelled.TrySetResult(true);
                    try { socket.Dispose(); } catch (Exception) { }
                }))
                {
                    var first = await Task.WhenAny(connect, cancelled.Task).ConfigureAwait(false);
                    if (first != connect)
                    {
                        _ = connect.ContinueWith(t => { var _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        throw new OperationCanceledException(token);
                    }
                    await connect.ConfigureAwait(false);
                }
                return socket;
            }
            catch
            {
                try { socket.Dispose(); } catch (Exception) { }
                if (token.IsCancellationRequested)
                    throw new OperationCanceledException(token);
                throw;
            }
        }
    }
}
