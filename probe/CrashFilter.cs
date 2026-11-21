using System;
using System.Linq;
using System.Net.Sockets;

namespace Ru7100Probe
{
    /// Unobserved task exceptions that are expected teardown, not crashes: a disposed connection's aborted socket call
    /// (the losing leg of the TCP/uTP race, an engine connect timeout) or a cancellation. They are counted, not
    /// written to last-crash.txt, so the crash history keeps the failures that matter.
    internal static class CrashFilter
    {
        public static bool IsExpected(Exception error)
        {
            if (error is AggregateException aggregate)
            {
                var inner = aggregate.Flatten().InnerExceptions;
                return inner.Count > 0 && inner.All(IsExpected);
            }
            switch (error)
            {
                case OperationCanceledException _:
                case ObjectDisposedException _:
                    return true;
                case SocketException socket:
                    return socket.SocketErrorCode == SocketError.OperationAborted || socket.SocketErrorCode == SocketError.Interrupted;
                case System.IO.IOException io when io.InnerException != null:
                    return IsExpected(io.InnerException);
                default:
                    return false;
            }
        }
    }
}
