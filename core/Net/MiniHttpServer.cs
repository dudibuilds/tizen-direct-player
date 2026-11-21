// Small HTTP/1.1 server over plain sockets for the app's loopback endpoints: the torrent media server AVPlay reads
// from, the agent API and the trailer manifest server. It replaces System.Net.HttpListener, whose managed accept path
// on the TV runtime threw ArgumentNullException in HttpEndPointListener.ProcessAccept on a thread-pool callback,
// outside every app catch, and killed the process mid-playback.
//
// Rules:
// - Nothing escapes a connection: every per-connection fault is caught and logged, and the connection is closed.
// - The accept loop ends only on Dispose; an accept error is logged and retried after a short back-off.
// - Every deadline is enforced by closing the socket from a watchdog timer. Old runtimes ignore cancellation tokens
//   on in-flight socket reads and writes, so no deadline relies on a token.
// - A response that ends short of its Content-Length, or a request body left unread, closes the connection instead
//   of keeping it alive. Unread request bytes are drained briefly after the response so the peer gets the response
//   rather than a reset.
// - Headers mirror what HttpListener sent (Server: Microsoft-NetCore/2.0, Date, Content-Length), which is all AVPlay
//   has ever been tested against.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.Net
{
    public sealed class MiniHttpOptions
    {
        /// Name used in log lines (media, api, trailer).
        public string Name { get; set; } = "http";
        public int MaxHeaderBytes { get; set; } = 16 * 1024;
        /// From the first byte of a request to the end of its headers.
        public TimeSpan HeaderTimeout { get; set; } = TimeSpan.FromSeconds(10);
        /// Keep-alive wait for the next request on an open connection (also the wait for the first request).
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(30);
        /// Whole request body, from the first body read.
        public TimeSpan BodyTimeout { get; set; } = TimeSpan.FromSeconds(10);
        /// One send that makes no progress this long closes the connection. Null: never (the media server, whose
        /// reader may pause for as long as the viewer pauses).
        public TimeSpan? WriteTimeout { get; set; } = TimeSpan.FromSeconds(30);
        /// Open connections beyond this get a 503 and are closed.
        public int MaxConnections { get; set; } = 32;
        public string ServerHeader { get; set; } = "Microsoft-NetCore/2.0";
    }

    public delegate Task MiniHttpHandler(MiniHttpRequest request, MiniHttpResponse response);

    /// Counters for diagnostics.
    public sealed class MiniHttpStats
    {
        public long Accepted;
        public long Requests;
        public long Faults;
        public long Timeouts;
        public long Rejected;
        public long AcceptErrors;
        public int Open;
        public string LastFault;
    }

    public sealed class MiniHttpServer : IDisposable
    {
        internal static readonly Stopwatch Clock = Stopwatch.StartNew();
        private readonly IReadOnlyList<(IPEndPoint EndPoint, bool Required)> endpoints;
        private readonly MiniHttpHandler handler;
        private readonly Action<string> log;
        private readonly List<TcpListener> listeners = new List<TcpListener>();
        private readonly ConcurrentDictionary<Connection, byte> connections = new ConcurrentDictionary<Connection, byte>();
        private readonly MiniHttpStats stats = new MiniHttpStats();
        private Timer watchdog;
        private volatile bool disposed;
        private int started;

        public MiniHttpServer(IEnumerable<(IPEndPoint EndPoint, bool Required)> endpoints, MiniHttpHandler handler,
            MiniHttpOptions options = null, Action<string> log = null)
        {
            this.endpoints = (endpoints ?? throw new ArgumentNullException(nameof(endpoints))).ToList();
            if (this.endpoints.Count == 0)
                throw new ArgumentException("At least one endpoint is required.", nameof(endpoints));
            this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
            Options = options ?? new MiniHttpOptions();
            this.log = log;
        }

        public MiniHttpOptions Options { get; }
        public MiniHttpStats Stats => stats;
        /// The endpoints actually bound (port 0 resolves to the assigned port).
        public IReadOnlyList<IPEndPoint> BoundEndPoints { get; private set; } = Array.Empty<IPEndPoint>();

        /// Loopback on IPv4 (required) and IPv6 (best effort): "localhost" may resolve to either on the TV.
        public static IEnumerable<(IPEndPoint, bool)> Loopback(int port) => new[]
        {
            (new IPEndPoint(IPAddress.Loopback, port), true),
            (new IPEndPoint(IPAddress.IPv6Loopback, port), false),
        };

        /// Parses an HttpListener-style prefix ("http://127.0.0.1:45551/", "http://+:8080/", "http://localhost:1/").
        public static IEnumerable<(IPEndPoint, bool)> FromPrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix) || !prefix.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only http:// prefixes are supported: " + prefix, nameof(prefix));
            var rest = prefix.Substring(7);
            var slash = rest.IndexOf('/');
            var authority = slash < 0 ? rest : rest.Substring(0, slash);
            string host;
            var port = 80;
            if (authority.StartsWith("[", StringComparison.Ordinal))
            {
                var close = authority.IndexOf(']');
                host = authority.Substring(1, close - 1);
                if (close + 1 < authority.Length && authority[close + 1] == ':')
                    port = int.Parse(authority.Substring(close + 2), CultureInfo.InvariantCulture);
            }
            else
            {
                var colon = authority.LastIndexOf(':');
                host = colon < 0 ? authority : authority.Substring(0, colon);
                if (colon >= 0)
                    port = int.Parse(authority.Substring(colon + 1), CultureInfo.InvariantCulture);
            }
            if (host == "+" || host == "*" || host == "0.0.0.0")
                return new[] { (new IPEndPoint(IPAddress.Any, port), true) };
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                return Loopback(port);
            if (IPAddress.TryParse(host, out var address))
                return new[] { (new IPEndPoint(address, port), true) };
            throw new ArgumentException("Prefix host must be an IP address, localhost, + or *: " + prefix, nameof(prefix));
        }

        /// Binds every endpoint and starts accepting. Throws when a required endpoint cannot be bound.
        public void Start()
        {
            if (Interlocked.Exchange(ref started, 1) == 1)
                throw new InvalidOperationException("Already started");
            var bound = new List<IPEndPoint>();
            try
            {
                foreach (var (endPoint, required) in endpoints)
                {
                    var listener = new TcpListener(endPoint);
                    try
                    {
                        if (endPoint.AddressFamily == AddressFamily.InterNetworkV6)
                            listener.Server.DualMode = false;
                        listener.Start(64);
                    }
                    catch (Exception error) when (!required)
                    {
                        Log("bind " + endPoint + " skipped: " + error.Message);
                        try { listener.Stop(); } catch (Exception) { }
                        continue;
                    }
                    listeners.Add(listener);
                    bound.Add((IPEndPoint)listener.LocalEndpoint);
                }
            }
            catch
            {
                foreach (var listener in listeners)
                    try { listener.Stop(); } catch (Exception) { }
                listeners.Clear();
                throw;
            }
            BoundEndPoints = bound;
            // Everything an accepted connection touches exists before the first accept (the HttpListener bug was an
            // accept callback running before its connection set was created).
            watchdog = new Timer(_ => Sweep(), null, 500, 500);
            foreach (var listener in listeners)
                _ = AcceptLoopAsync(listener);
            Log("listening on " + string.Join(", ", bound));
            Ru7100.Core.Playback.PlaybackTimeline.Shared.Add("listener", Options.Name + " listening on " + string.Join(", ", bound));
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            foreach (var listener in listeners)
                try { listener.Stop(); } catch (Exception) { }
            try { watchdog?.Dispose(); } catch (Exception) { }
            foreach (var connection in connections.Keys)
                connection.Close("server stopped");
            Log("closed");
            Ru7100.Core.Playback.PlaybackTimeline.Shared.Add("listener", Options.Name + " closed");
        }

        private async Task AcceptLoopAsync(TcpListener listener)
        {
            var backoff = 50;
            while (!disposed)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptSocketAsync().ConfigureAwait(false);
                    backoff = 50;
                }
                catch (Exception error)
                {
                    if (disposed)
                        return;
                    Interlocked.Increment(ref stats.AcceptErrors);
                    Log("accept error: " + error.GetType().Name + " " + error.Message);
                    Ru7100.Core.Playback.PlaybackTimeline.Shared.Add("listener", Options.Name + " accept error: " + error.GetType().Name + " " + error.Message);
                    try { await Task.Delay(backoff).ConfigureAwait(false); } catch (Exception) { }
                    backoff = Math.Min(2000, backoff * 2);
                    continue;
                }
                if (disposed)
                {
                    SafeClose(socket);
                    return;
                }
                Interlocked.Increment(ref stats.Accepted);
                _ = RunConnectionAsync(socket);
            }
        }

        private async Task RunConnectionAsync(Socket socket)
        {
            Connection connection = null;
            try
            {
                socket.NoDelay = true;
                connection = new Connection(this, socket);
                connections[connection] = 0;   // the watchdog bounds every socket call from here on
                if (Interlocked.Increment(ref stats.Open) > Options.MaxConnections)
                {
                    Interlocked.Increment(ref stats.Rejected);
                    await connection.RejectAsync().ConfigureAwait(false);
                    return;
                }
                if (disposed)
                    return;
                await connection.RunAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // Run catches its own faults; this is the last line for anything else (e.g. a socket option on a
                // connection the peer already reset).
                Fault(connection?.Describe() ?? "connection", error);
            }
            finally
            {
                if (connection != null)
                {
                    connections.TryRemove(connection, out _);
                    connection.Close(null);
                }
                else
                    SafeClose(socket);
                Interlocked.Decrement(ref stats.Open);
            }
        }

        private void Sweep()
        {
            try
            {
                var now = Clock.ElapsedMilliseconds;
                foreach (var connection in connections.Keys)
                    connection.CheckDeadlines(now);
            }
            catch (Exception error) { Log("watchdog: " + error.Message); }
        }

        internal void Fault(string where, Exception error)
        {
            Interlocked.Increment(ref stats.Faults);
            stats.LastFault = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + where + ": " + error.GetType().Name + " " + error.Message;
            Log(where + " fault: " + error.GetType().Name + " " + error.Message);
            Ru7100.Core.Playback.PlaybackTimeline.Shared.Add("listener", where + " fault: " + error.GetType().Name + " " + error.Message);
        }

        internal void Log(string message)
        {
            if (log == null)
                return;
            try { log(Options.Name + ": " + message); }
            catch (Exception) { }
        }

        internal Task Handle(MiniHttpRequest request, MiniHttpResponse response)
        {
            Interlocked.Increment(ref stats.Requests);
            return handler(request, response);
        }

        internal static void SafeClose(Socket socket)
        {
            try { socket.Shutdown(SocketShutdown.Both); } catch (Exception) { }
            try { socket.Dispose(); } catch (Exception) { }
        }

        internal static string Reason(int status)
        {
            switch (status)
            {
                case 100: return "Continue";
                case 200: return "OK";
                case 204: return "No Content";
                case 206: return "Partial Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 408: return "Request Timeout";
                case 409: return "Conflict";
                case 413: return "Request Entity Too Large";
                case 416: return "Requested Range Not Satisfiable";
                case 429: return "Too Many Requests";
                case 431: return "Request Header Fields Too Large";
                case 500: return "Internal Server Error";
                case 501: return "Not Implemented";
                case 503: return "Service Unavailable";
                default: return "Status";
            }
        }

        // ------------------------------------------------------------------ connection

        internal sealed class Connection
        {
            private readonly MiniHttpServer server;
            private readonly Socket socket;
            private readonly CancellationTokenSource aborted = new CancellationTokenSource();
            private readonly byte[] buffer;
            private int start, end;
            private long readDeadline, writeDeadline;   // Clock ms; 0 = none
            private string readPhase = "idle";
            private int closed;
            private Task<int> lookahead;
            private bool peerClosed;
            private string closeReason;

            public Connection(MiniHttpServer server, Socket socket)
            {
                this.server = server;
                this.socket = socket;
                buffer = new byte[server.Options.MaxHeaderBytes + 4096];
                try { Remote = socket.RemoteEndPoint as IPEndPoint; } catch (Exception) { }
            }

            public IPEndPoint Remote { get; }
            public CancellationToken Aborted => aborted.Token;
            public bool IsClosed => Volatile.Read(ref closed) == 1;
            public string CloseReason => closeReason;
            public MiniHttpOptions Options => server.Options;

            public string Describe() => server.Options.Name + " " + (Remote?.ToString() ?? "?");

            public async Task RejectAsync()
            {
                var body = Encoding.ASCII.GetBytes("HTTP/1.1 503 Service Unavailable\r\nServer: " + server.Options.ServerHeader +
                    "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                try { await SendAsync(body, 0, body.Length, TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception) { }
                Close("too many connections");
            }

            public async Task RunAsync()
            {
                try
                {
                    while (!IsClosed)
                    {
                        MiniHttpRequest request;
                        try { request = await ReadRequestAsync().ConfigureAwait(false); }
                        catch (BadRequest bad)
                        {
                            server.Log(Describe() + " bad request: " + bad.Message);
                            await SendSimpleAsync(bad.Status).ConfigureAwait(false);
                            return;
                        }
                        if (request == null)
                            return;
                        var response = new MiniHttpResponse(this, request);
                        if (!request.HasBody)
                            StartLookahead();
                        try
                        {
                            await server.Handle(request, response).ConfigureAwait(false);
                        }
                        catch (Exception error)
                        {
                            if (!IsClosed)
                                server.Fault(Describe() + " " + request.Method + " " + request.Path, error);
                            if (!response.HeadersSent && !IsClosed)
                            {
                                response.Reset(500);
                                response.KeepAlive = false;
                            }
                            else
                            {
                                Close("handler fault after headers");
                                return;
                            }
                        }
                        if (IsClosed)
                            return;
                        var keep = await response.FinishAsync().ConfigureAwait(false);
                        if (!keep || !request.BodyConsumed)
                        {
                            await CloseGracefullyAsync(!request.BodyConsumed).ConfigureAwait(false);
                            return;
                        }
                    }
                }
                catch (Exception error)
                {
                    if (!IsClosed && !IsExpected(error))
                        server.Fault(Describe(), error);
                }
                finally { Close(null); }
            }

            private static bool IsExpected(Exception error) =>
                error is IOException || error is SocketException || error is ObjectDisposedException || error is OperationCanceledException;

            /// Reads one request's head. Null when the peer closed (or idled out) between requests.
            private async Task<MiniHttpRequest> ReadRequestAsync()
            {
                if (lookahead != null)
                {
                    // The watchdog closes an idle keep-alive connection, which ends the pending lookahead read.
                    var pending = lookahead;
                    lookahead = null;
                    if (start == end)
                        ArmRead(server.Options.IdleTimeout, "idle");
                    int read;
                    try { read = await pending.ConfigureAwait(false); }
                    catch (Exception) { return null; }
                    finally { DisarmRead(); }
                    if (read <= 0 && start == end)
                        return null;
                }
                if (peerClosed)
                    return null;
                var waitingForFirstByte = start == end;
                ArmRead(waitingForFirstByte ? server.Options.IdleTimeout : server.Options.HeaderTimeout, waitingForFirstByte ? "idle" : "header");
                try
                {
                    while (true)
                    {
                        var headEnd = IndexOfHeadEnd();
                        if (headEnd >= 0)
                        {
                            var request = Parse(start, headEnd);
                            start = headEnd + 4;
                            return request;
                        }
                        if (end - start >= server.Options.MaxHeaderBytes)
                            throw new BadRequest(431, "headers too large");
                        if (end == buffer.Length)
                            Compact();
                        var count = await ReceiveAsync(buffer, end, buffer.Length - end).ConfigureAwait(false);
                        if (count <= 0)
                        {
                            if (start == end)
                                return null;
                            throw new IOException("peer closed mid-header");
                        }
                        if (waitingForFirstByte)
                        {
                            waitingForFirstByte = false;
                            ArmRead(server.Options.HeaderTimeout, "header");
                        }
                        end += count;
                    }
                }
                finally { DisarmRead(); }
            }

            private int IndexOfHeadEnd()
            {
                for (var i = start; i + 3 < end; i++)
                    if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                        return i;
                return -1;
            }

            private void Compact()
            {
                if (start == 0)
                    return;
                Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                end -= start;
                start = 0;
            }

            private MiniHttpRequest Parse(int from, int headEnd)
            {
                // Latin-1: header bytes map one to one onto chars.
                var chars = new char[headEnd - from];
                for (var i = 0; i < chars.Length; i++)
                    chars[i] = (char)buffer[from + i];
                var text = new string(chars);
                var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
                var first = lines[0].Split(' ');
                if (first.Length != 3 || first[0].Length == 0 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                    throw new BadRequest(400, "bad request line");
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 1; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.Length == 0)
                        continue;
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                        throw new BadRequest(400, "bad header line");
                    var name = line.Substring(0, colon).Trim();
                    var value = line.Substring(colon + 1).Trim();
                    headers[name] = headers.TryGetValue(name, out var existing) ? existing + ", " + value : value;
                }
                return new MiniHttpRequest(this, first[0], first[1], first[2], headers);
            }

            // ---- body (called by MiniHttpRequest) ----

            internal async Task<int> ReadBodyAsync(byte[] target, int offset, int count)
            {
                if (start < end)
                {
                    var take = Math.Min(count, end - start);
                    Buffer.BlockCopy(buffer, start, target, offset, take);
                    start += take;
                    return take;
                }
                start = end = 0;
                return await ReceiveAsync(target, offset, count).ConfigureAwait(false);
            }

            internal async Task<string> ReadLineAsync(int max)
            {
                var line = new StringBuilder();
                while (true)
                {
                    while (start < end)
                    {
                        var c = (char)buffer[start++];
                        if (c == '\n')
                            return line.ToString().TrimEnd('\r');
                        if (line.Length >= max)
                            throw new BadRequest(400, "chunk line too long");
                        line.Append(c);
                    }
                    start = end = 0;
                    var count = await ReceiveAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (count <= 0)
                        throw new IOException("peer closed mid-body");
                    end = count;
                }
            }

            internal void ArmBody() => ArmRead(server.Options.BodyTimeout, "body");
            internal void DisarmBody() => DisarmRead();

            /// After the request body is read, watch for the peer closing while the handler works (a media read waiting
            /// for a piece has no socket call of its own to notice it). Pipelined bytes stay in the buffer.
            internal void StartLookahead()
            {
                if (lookahead != null || IsClosed)
                    return;
                Compact();
                if (end == buffer.Length)
                    return;
                var task = ReceiveAsync(buffer, end, buffer.Length - end);
                lookahead = task.ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion && t.Result > 0)
                    {
                        end += t.Result;
                        return t.Result;
                    }
                    var _ = t.Exception;   // observed
                    peerClosed = true;
                    try { aborted.Cancel(); } catch (Exception) { }
                    if (closeReason == null)
                        closeReason = "client closed";
                    return 0;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            // ---- socket I/O ----

            private async Task<int> ReceiveAsync(byte[] target, int offset, int count)
            {
                if (IsClosed)
                    throw new ObjectDisposedException("connection");
                return await socket.ReceiveAsync(new ArraySegment<byte>(target, offset, count), SocketFlags.None).ConfigureAwait(false);
            }

            internal async Task SendAsync(byte[] data, int offset, int count, TimeSpan? timeout)
            {
                while (count > 0)
                {
                    if (IsClosed)
                        throw new IOException("connection closed: " + (closeReason ?? "closed"));
                    ArmWrite(timeout);
                    int sent;
                    try { sent = await socket.SendAsync(new ArraySegment<byte>(data, offset, count), SocketFlags.None).ConfigureAwait(false); }
                    finally { Volatile.Write(ref writeDeadline, 0); }
                    if (sent <= 0)
                        throw new IOException("send returned " + sent);
                    offset += sent;
                    count -= sent;
                }
            }

            internal Task SendAsync(byte[] data, int offset, int count) => SendAsync(data, offset, count, server.Options.WriteTimeout);

            private void ArmRead(TimeSpan timeout, string phase)
            {
                readPhase = phase;
                Volatile.Write(ref readDeadline, Clock.ElapsedMilliseconds + Math.Max(1, (long)timeout.TotalMilliseconds));
            }

            private void DisarmRead() => Volatile.Write(ref readDeadline, 0);

            private void ArmWrite(TimeSpan? timeout) =>
                Volatile.Write(ref writeDeadline, timeout.HasValue ? Clock.ElapsedMilliseconds + Math.Max(1, (long)timeout.Value.TotalMilliseconds) : 0);

            public void CheckDeadlines(long now)
            {
                var read = Volatile.Read(ref readDeadline);
                var write = Volatile.Read(ref writeDeadline);
                if (read != 0 && now >= read)
                {
                    if (readPhase != "idle")
                        Interlocked.Increment(ref server.stats.Timeouts);
                    Close(readPhase + " timeout");
                }
                else if (write != 0 && now >= write)
                {
                    Interlocked.Increment(ref server.stats.Timeouts);
                    Close("write timeout");
                }
            }

            private async Task SendSimpleAsync(int status)
            {
                var head = "HTTP/1.1 " + status + " " + Reason(status) + "\r\nServer: " + server.Options.ServerHeader + "\r\nDate: " +
                    DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture) + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                var bytes = Encoding.ASCII.GetBytes(head);
                try { await SendAsync(bytes, 0, bytes.Length, TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (Exception) { }
                await CloseGracefullyAsync(true).ConfigureAwait(false);
            }

            /// Ends the connection after a response. With unread request bytes, shuts down sending first and drains
            /// briefly so the peer reads the response instead of a reset.
            internal async Task CloseGracefullyAsync(bool drain)
            {
                if (IsClosed)
                    return;
                if (drain)
                {
                    try
                    {
                        socket.Shutdown(SocketShutdown.Send);
                        ArmRead(TimeSpan.FromSeconds(1), "drain");
                        var scratch = new byte[8192];
                        var total = 0;
                        while (total < 256 * 1024)
                        {
                            var count = await socket.ReceiveAsync(new ArraySegment<byte>(scratch), SocketFlags.None).ConfigureAwait(false);
                            if (count <= 0)
                                break;
                            total += count;
                        }
                    }
                    catch (Exception) { }
                }
                Close(null);
            }

            public void Close(string reason)
            {
                if (Interlocked.Exchange(ref closed, 1) == 1)
                    return;
                if (reason != null && closeReason == null)
                    closeReason = reason;
                if (reason != null && reason != "server stopped" && !reason.StartsWith("idle", StringComparison.Ordinal))
                    server.Log(Describe() + " closed: " + reason);
                try { aborted.Cancel(); } catch (Exception) { }
                SafeClose(socket);
            }
        }

        private sealed class BadRequest : Exception
        {
            public BadRequest(int status, string message) : base(message) { Status = status; }
            public int Status { get; }
        }

        internal static Exception NewBadRequest(int status, string message) => new BadRequest(status, message);
    }

    public sealed class MiniHttpRequest
    {
        private readonly MiniHttpServer.Connection connection;
        private readonly Dictionary<string, string> headers;
        private long remaining;
        private bool chunkedDone;

        internal MiniHttpRequest(MiniHttpServer.Connection connection, string method, string target, string version,
            Dictionary<string, string> headers)
        {
            this.connection = connection;
            this.headers = headers;
            Method = method;
            RawTarget = target;
            Version = version;
            var pathAndQuery = target;
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
                    pathAndQuery = absolute.PathAndQuery;
            }
            var q = pathAndQuery.IndexOf('?');
            Path = q < 0 ? pathAndQuery : pathAndQuery.Substring(0, q);
            Query = q < 0 ? "" : pathAndQuery.Substring(q + 1);
            Chunked = (Header("Transfer-Encoding") ?? "").IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0;
            var length = Header("Content-Length");
            if (Chunked)
                ContentLength = -1;
            else if (length == null)
                ContentLength = 0;
            else if (!long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                throw MiniHttpServer.NewBadRequest(400, "bad Content-Length");
            else
                ContentLength = parsed;
            remaining = Math.Max(0, ContentLength);
            var connectionHeader = Header("Connection") ?? "";
            KeepAliveRequested = version == "HTTP/1.1"
                ? connectionHeader.IndexOf("close", StringComparison.OrdinalIgnoreCase) < 0
                : connectionHeader.IndexOf("keep-alive", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public string Method { get; }
        public string RawTarget { get; }
        /// Path as sent (not percent-decoded), without the query.
        public string Path { get; }
        public string Query { get; }
        public string Version { get; }
        /// -1 for a chunked body, 0 when there is none.
        public long ContentLength { get; }
        public bool Chunked { get; }
        public bool HasBody => Chunked || ContentLength > 0;
        public bool KeepAliveRequested { get; }
        public IPEndPoint RemoteEndPoint => connection.Remote;
        public IEnumerable<KeyValuePair<string, string>> Headers => headers;
        public string Header(string name) => headers.TryGetValue(name, out var value) ? value : null;

        internal bool BodyConsumed => !HasBody || (Chunked ? chunkedDone : remaining == 0);

        /// Reads the whole body within the server's body deadline. Null when it exceeds maxBytes (the connection is
        /// then closed after the response).
        public async Task<byte[]> ReadBodyAsync(int maxBytes)
        {
            if (!HasBody)
                return new byte[0];
            if (ContentLength > maxBytes)
                return null;
            if ((Header("Expect") ?? "").IndexOf("100-continue", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var proceed = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                await connection.SendAsync(proceed, 0, proceed.Length, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            connection.ArmBody();
            try
            {
                using (var body = new MemoryStream())
                {
                    var chunk = new byte[8192];
                    if (!Chunked)
                    {
                        while (remaining > 0)
                        {
                            var read = await connection.ReadBodyAsync(chunk, 0, (int)Math.Min(chunk.Length, remaining)).ConfigureAwait(false);
                            if (read <= 0)
                                throw new IOException("peer closed mid-body");
                            body.Write(chunk, 0, read);
                            remaining -= read;
                        }
                    }
                    else
                    {
                        while (true)
                        {
                            var line = await connection.ReadLineAsync(256).ConfigureAwait(false);
                            var semi = line.IndexOf(';');
                            var hex = (semi < 0 ? line : line.Substring(0, semi)).Trim();
                            if (!long.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var size) || size < 0)
                                throw MiniHttpServer.NewBadRequest(400, "bad chunk size");
                            if (size == 0)
                            {
                                while ((await connection.ReadLineAsync(8192).ConfigureAwait(false)).Length > 0) { }
                                chunkedDone = true;
                                break;
                            }
                            if (body.Length + size > maxBytes)
                                return null;
                            var left = size;
                            while (left > 0)
                            {
                                var read = await connection.ReadBodyAsync(chunk, 0, (int)Math.Min(chunk.Length, left)).ConfigureAwait(false);
                                if (read <= 0)
                                    throw new IOException("peer closed mid-body");
                                body.Write(chunk, 0, read);
                                left -= read;
                            }
                            await connection.ReadLineAsync(2).ConfigureAwait(false);
                        }
                    }
                    connection.StartLookahead();
                    return body.ToArray();
                }
            }
            finally { connection.DisarmBody(); }
        }
    }

    public sealed class MiniHttpResponse
    {
        private readonly MiniHttpServer.Connection connection;
        private readonly MiniHttpRequest request;
        private readonly List<KeyValuePair<string, string>> headers = new List<KeyValuePair<string, string>>();
        private long written;

        internal MiniHttpResponse(MiniHttpServer.Connection connection, MiniHttpRequest request)
        {
            this.connection = connection;
            this.request = request;
            KeepAlive = request.KeepAliveRequested;
        }

        public int StatusCode { get; set; } = 200;
        public string ContentType { get; set; }
        /// Required before writing a body that should keep the connection alive; without it the body ends at close.
        public long? ContentLength { get; set; }
        public bool KeepAlive { get; set; }
        public bool HeadersSent { get; private set; }
        public long BytesWritten => written;
        /// Cancelled when the connection closes: the peer left, a deadline expired, Abort, or the server stopped.
        public CancellationToken Aborted => connection.Aborted;
        public string CloseReason => connection.CloseReason;

        public void SetHeader(string name, string value)
        {
            headers.RemoveAll(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));
            headers.Add(new KeyValuePair<string, string>(name, value));
        }

        internal void Reset(int status)
        {
            StatusCode = status;
            ContentType = null;
            ContentLength = 0;
            headers.Clear();
        }

        /// Closes the connection now; a write in progress fails.
        public void Abort(string reason = "aborted") => connection.Close(reason);

        public async Task WriteAsync(byte[] data, int offset, int count)
        {
            if (!HeadersSent)
                await SendHeadersAsync().ConfigureAwait(false);
            if (request.Method == "HEAD" || count <= 0)
                return;
            if (ContentLength.HasValue && written + count > ContentLength.Value)
                throw new InvalidOperationException("Response body exceeds its Content-Length.");
            await connection.SendAsync(data, offset, count).ConfigureAwait(false);
            written += count;
        }

        public Task WriteAsync(byte[] data) => WriteAsync(data, 0, data.Length);

        private async Task SendHeadersAsync()
        {
            HeadersSent = true;
            if (!ContentLength.HasValue)
                KeepAlive = false;   // the body ends when the connection closes
            var head = new StringBuilder(256);
            head.Append("HTTP/1.1 ").Append(StatusCode.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(MiniHttpServer.Reason(StatusCode)).Append("\r\n");
            if (ContentType != null)
                head.Append("Content-Type: ").Append(ContentType).Append("\r\n");
            foreach (var header in headers)
                head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            head.Append("Server: ").Append(connection.Options.ServerHeader).Append("\r\n");
            head.Append("Date: ").Append(DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
            if (ContentLength.HasValue)
                head.Append("Content-Length: ").Append(ContentLength.Value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            if (!KeepAlive)
                head.Append("Connection: close\r\n");
            head.Append("\r\n");
            var bytes = Encoding.ASCII.GetBytes(head.ToString());
            await connection.SendAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }

        /// Sends the head if nothing was written. True when the connection may serve another request.
        internal async Task<bool> FinishAsync()
        {
            if (!HeadersSent)
            {
                if (!ContentLength.HasValue)
                    ContentLength = 0;
                await SendHeadersAsync().ConfigureAwait(false);
            }
            if (request.Method != "HEAD" && ContentLength.HasValue && written != ContentLength.Value)
                return false;   // a short body: the peer cannot find the next response
            return KeepAlive;
        }
    }
}
