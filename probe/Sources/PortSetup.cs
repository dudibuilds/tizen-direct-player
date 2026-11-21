using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent.PortForwarding;

namespace Ru7100Probe
{
    /// The engine's listen ports, chosen before the engine is built.
    internal sealed class ListenPorts
    {
        public int Tcp4 { get; set; }
        public int Tcp6 { get; set; }
        public int Udp { get; set; }
        public string Note { get; set; } = "";

        public override string ToString() =>
            Tcp4 == Tcp6 && Tcp4 == Udp ? Tcp4 + Note : $"tcp4 {Tcp4} tcp6 {Tcp6} udp {Udp}{Note}";

        /// The first candidate whose TCP (IPv4 and IPv6) and UDP ports are all free, probed by binding them the way the
        /// engine will (all three open at once). If IPv6 cannot share the IPv4 port (a dual-stack socket owns both) it
        /// takes the next port up. None free: 0, i.e. random ports as before (peers then only reach us through UPnP).
        public static ListenPorts Choose(IReadOnlyList<int> candidates, Action<string> log)
        {
            foreach (var port in candidates ?? Array.Empty<int>())
            {
                if (port <= 0 || port > 65534)
                    continue;
                using (var tcp4 = TryBind(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp, port))
                using (var udp = TryBind(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp, port))
                {
                    if (tcp4 == null || udp == null)
                    {
                        log?.Invoke($"port {port} is taken (tcp4 {(tcp4 == null ? "busy" : "free")}, udp {(udp == null ? "busy" : "free")}); trying the next one");
                        continue;
                    }
                    using (var tcp6 = TryBind(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp, port))
                    {
                        if (tcp6 != null || !Socket.OSSupportsIPv6)
                            return new ListenPorts { Tcp4 = port, Tcp6 = port, Udp = port };
                    }
                    using (var tcp6 = TryBind(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp, port + 1))
                    {
                        if (tcp6 != null)
                            return new ListenPorts { Tcp4 = port, Tcp6 = port + 1, Udp = port, Note = " (ipv6 on the next port)" };
                    }
                    log?.Invoke($"port {port}: no ipv6 port free beside it; trying the next one");
                }
            }
            log?.Invoke("no fixed listen port is free; using random ports");
            return new ListenPorts { Note = " (random)" };
        }

        private static Socket TryBind(AddressFamily family, SocketType type, ProtocolType protocol, int port)
        {
            Socket socket = null;
            try
            {
                socket = new Socket(family, type, protocol);
                socket.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, port));
                if (type == SocketType.Stream)
                    socket.Listen(1);
                return socket;
            }
            catch (Exception)
            {
                socket?.Dispose();
                return null;
            }
        }
    }

    /// UPnP / NAT-PMP port forwarding that can never break the engine. MonoTorrent starts the forwarder inside its own
    /// engine start, after it has already marked itself running, so an exception from Mono.Nat (multicast or socket
    /// restrictions on Tizen) would leave the DHT and the peer listeners unstarted for good. Every call is caught here
    /// and degrades to "no forwarding". Mapping requests return at once; the router answers in the background, so a slow
    /// router never holds the engine (or our engine gate).
    ///
    /// Also the source of the port-mapping outcome in the swarm diagnostics: mapped / failed / no router found, plus
    /// whether the router's external address is public (behind carrier-grade NAT a mapping does not make us reachable).
    internal sealed class SafePortForwarder : IPortForwarder
    {
        private static readonly TimeSpan NoRouterAfter = TimeSpan.FromSeconds(10);
        private readonly IPortForwarder inner;
        private readonly Action<string> log;
        private readonly object gate = new object();
        private string state = "off";
        private string router = "";
        private string external = "";
        private DateTime startedAt;
        private int devices;
        private object device;                                   // Mono.Nat INatDevice (reflection)
        private readonly HashSet<string> verified = new HashSet<string>();   // failed mappings found already in place
        private readonly HashSet<string> checking = new HashSet<string>();
        private string refusal = "";
        private string lastReported = "";

        public SafePortForwarder(Func<IPortForwarder> create, Action<string> log)
        {
            this.log = log;
            try
            {
                inner = create();
                inner.MappingsChanged += (sender, args) =>
                {
                    Report();
                    MappingsChanged?.Invoke(this, args);
                };
                WatchDevices();
            }
            catch (Exception error)
            {
                inner = null;
                state = "unavailable (" + error.GetType().Name + ": " + error.Message + ")";
                Log("nat: port forwarding unavailable: " + error.GetType().Name + " " + error.Message);
            }
        }

        public event EventHandler MappingsChanged;

        /// The router's reported external address (kept out of logs; the Mac harness compares it with the public IP).
        public IPAddress ExternalAddress { get; private set; }

        public bool Active { get { try { return inner?.Active ?? false; } catch (Exception) { return false; } } }

        public Mappings Mappings { get { try { return inner?.Mappings ?? Mappings.Empty; } catch (Exception) { return Mappings.Empty; } } }

        /// One line for the swarm diagnostics, e.g. "mapped tcp 49231, udp 49231 via upnp 192.168.1.1 ext=public".
        public string Describe()
        {
            lock (gate)
            {
                var mappings = Mappings;
                string outcome;
                if (inner == null || state == "off" || state.StartsWith("failed to start", StringComparison.Ordinal))
                    outcome = state;
                else if (mappings.Created.Count > 0 || mappings.Failed.Any(m => verified.Contains(Name(m))))
                {
                    var mapped = mappings.Created.Select(Name).Concat(mappings.Failed.Select(Name).Where(verified.Contains)).ToList();
                    var failed = mappings.Failed.Select(Name).Where(n => !verified.Contains(n)).ToList();
                    outcome = "mapped " + string.Join(", ", mapped) + (failed.Count > 0 ? "; failed " + string.Join(", ", failed) : "");
                }
                else if (mappings.Failed.Count > 0)
                    outcome = "failed " + string.Join(", ", mappings.Failed.Select(Name)) + (refusal.Length > 0 ? " (" + refusal + ")" : "");
                else if (devices == 0)
                    outcome = DateTime.UtcNow - startedAt >= NoRouterAfter ? "no router found" : "searching";
                else
                    outcome = "pending";
                return outcome + (router.Length > 0 ? " via " + router : "") + (external.Length > 0 ? " ext=" + external : "");
            }
        }

        private static string Name(Mapping mapping) => mapping.Protocol.ToString().ToLowerInvariant() + " " + mapping.PublicPort;

        public async Task StartAsync(CancellationToken token)
        {
            if (inner == null)
                return;
            lock (gate)
            {
                startedAt = DateTime.UtcNow;
                state = "on";
            }
            try
            {
                await inner.StartAsync(token).ConfigureAwait(false);
                Log("nat: searching for a UPnP / NAT-PMP router");
                _ = ReportNoRouterAsync();
            }
            catch (Exception error)
            {
                lock (gate)
                    state = "failed to start (" + error.GetType().Name + ": " + error.Message + ")";
                Log("nat: port forwarding failed to start: " + error.GetType().Name + " " + error.Message);
            }
        }

        public Task StopAsync(CancellationToken token) => StopAsync(true, token);

        public async Task StopAsync(bool removeExistingMappings, CancellationToken token)
        {
            if (inner == null)
                return;
            try { await inner.StopAsync(removeExistingMappings, token).ConfigureAwait(false); }
            catch (Exception error) { Log("nat: stop: " + error.Message); }
            lock (gate)
                if (state == "on")
                    state = "off";
        }

        public Task RegisterMappingAsync(Mapping mapping)
        {
            if (inner != null)
                _ = Run(() => inner.RegisterMappingAsync(mapping), "map " + Name(mapping));
            return Task.CompletedTask;
        }

        public Task UnregisterMappingAsync(Mapping mapping, CancellationToken token)
        {
            if (inner != null)
                _ = Run(() => inner.UnregisterMappingAsync(mapping, token), "unmap " + Name(mapping));
            return Task.CompletedTask;
        }

        private async Task Run(Func<Task> action, string what)
        {
            try { await Task.Run(action).ConfigureAwait(false); }
            catch (Exception error) { Log("nat: " + what + ": " + error.GetType().Name + " " + error.Message); }
            Report();
        }

        /// Mono.Nat ships inside the MonoTorrent package for run time only (its compile assets are excluded), so its
        /// discovery event and device are reached by reflection. Missing members only cost the router line in diagnostics.
        private void WatchDevices()
        {
            try
            {
                var utility = Type.GetType("Mono.Nat.NatUtility, Mono.Nat");
                var found = utility?.GetEvent("DeviceFound");
                if (found == null)
                    return;
                var handler = Delegate.CreateDelegate(found.EventHandlerType, this,
                    typeof(SafePortForwarder).GetMethod(nameof(OnDeviceFound), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
                found.AddEventHandler(null, handler);
            }
            catch (Exception error) { Log("nat: device watch unavailable: " + error.Message); }
        }

        private void OnDeviceFound(object sender, EventArgs args)
        {
            try
            {
                var device = args.GetType().GetProperty("Device")?.GetValue(args);
                if (device == null)
                    return;
                var type = device.GetType();
                var protocol = type.GetProperty("NatProtocol")?.GetValue(device)?.ToString().ToLowerInvariant() ?? "nat";
                var endpoint = type.GetProperty("DeviceEndpoint")?.GetValue(device) as IPEndPoint;
                lock (gate)
                {
                    devices++;
                    this.device = device;
                    router = protocol + " " + endpoint?.Address;
                }
                Log("nat: router found: " + router);
                var externalIp = type.GetMethod("GetExternalIPAsync", Type.EmptyTypes) ?? FindInterfaceMethod(type, "GetExternalIPAsync");
                if (externalIp == null)
                    return;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var address = await ((Task<IPAddress>)externalIp.Invoke(device, null)).ConfigureAwait(false);
                        var kind = Classify(address);
                        lock (gate)
                        {
                            external = kind;
                            ExternalAddress = address;
                        }
                        // The address itself stays out of the logs; only whether it is publicly routable.
                        Log("nat: router external address is " + kind);
                    }
                    catch (Exception error) { Log("nat: external address: " + error.GetType().Name + " " + error.Message); }
                });
            }
            catch (Exception error) { Log("nat: device: " + error.Message); }
        }

        private static System.Reflection.MethodInfo FindInterfaceMethod(Type type, string name) =>
            type.GetInterfaces().Select(i => i.GetMethod(name, Type.EmptyTypes)).FirstOrDefault(m => m != null);

        /// public / cgnat (100.64.0.0/10: the ISP's NAT, a router mapping cannot make us reachable) / private / unknown.
        internal static string Classify(IPAddress address)
        {
            if (address == null || address.Equals(IPAddress.Any) || address.Equals(IPAddress.None))
                return "unknown";
            if (address.AddressFamily != AddressFamily.InterNetwork)
                return "ipv6";
            var b = address.GetAddressBytes();
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                return "cgnat";
            if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || b[0] == 127 || (b[0] == 169 && b[1] == 254))
                return "private (double NAT)";
            return "public";
        }

        private async Task ReportNoRouterAsync()
        {
            await Task.Delay(NoRouterAfter).ConfigureAwait(false);
            bool none;
            lock (gate)
                none = devices == 0 && state == "on";
            if (none)
                Log("nat: no UPnP / NAT-PMP router answered within " + NoRouterAfter.TotalSeconds + " s");
        }

        /// Mono.Nat swallows the router's reason for a refused mapping. Once per mapping, ask again ourselves and log the
        /// answer: ConflictInMappingEntry means another device on the LAN holds the port (seen on the Mac while the TV
        /// held 27492). Never deletes an existing entry: it may belong to that other device.
        private void VerifyFailed()
        {
            object current;
            List<Mapping> failed;
            lock (gate)
            {
                current = device;
                failed = Mappings.Failed.Where(m => checking.Add(Name(m))).ToList();   // never removed: one retry each
            }
            if (current == null || failed.Count == 0)
                return;
            _ = Task.Run(async () =>
            {
                var type = current.GetType();
                var protocolType = Type.GetType("Mono.Nat.Protocol, Mono.Nat");
                var mappingType = Type.GetType("Mono.Nat.Mapping, Mono.Nat");
                var create = type.GetMethods().FirstOrDefault(m => m.Name == "CreatePortMapAsync") ?? FindInterfaceMethod(type, "CreatePortMapAsync", 1);
                if (protocolType == null || mappingType == null || create == null)
                    return;
                foreach (var mapping in failed)
                {
                    try
                    {
                        var map = Activator.CreateInstance(mappingType, Enum.Parse(protocolType, mapping.Protocol == Protocol.Tcp ? "Tcp" : "Udp"),
                            mapping.PrivatePort, mapping.PublicPort, 0, $"int. {mapping.PrivatePort} -> ext. {mapping.PublicPort} {mapping.Protocol}");
                        await ((Task)create.Invoke(current, new[] { map })).ConfigureAwait(false);
                        lock (gate)
                            verified.Add(Name(mapping));
                        Log("nat: " + Name(mapping) + " mapped on retry");
                    }
                    catch (Exception error)
                    {
                        var reason = (error.InnerException ?? error).Message;
                        lock (gate)
                            refusal = reason.Contains("Conflict") ? "port held by another device on this network" : reason;
                        Log("nat: router refused " + Name(mapping) + ": " + reason);
                    }
                }
                Report();
            });
        }

        private static System.Reflection.MethodInfo FindInterfaceMethod(Type type, string name, int parameters) =>
            type.GetInterfaces().Select(i => i.GetMethods().FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameters)).FirstOrDefault(m => m != null);

        private void Report()
        {
            VerifyFailed();
            var line = Describe();
            lock (gate)
            {
                if (line == lastReported)
                    return;
                lastReported = line;
            }
            Log("nat: port mapping " + line);
        }

        private void Log(string message)
        {
            try { log?.Invoke(message); } catch (Exception) { }
        }
    }
}
