// Remote UI dev mode: the wire protocol shared by the Mac dev host (devhost/) and the TV thin client
// (probe/Tizen/RemoteUi, compiled only into packages built with -p:RemoteUi=true). Never part of a release package.
//
// One TCP connection, opened by the TV. Each message is a 4-byte big-endian length followed by UTF-8 JSON with a
// "t" (type) field:
//   TV -> Mac   hello {token, version}        key {name, seq}                  stats {...}    painted {fid, applyPaintMs}
//               ps {player snapshot}           cue {text, startMs, durationMs, embedded}
//               src {id, rate, peers}          result {id, ok, error?, value?, ps?}
//   Mac -> TV   welcome {}                     frame {fid, diff, ack?}          exit {}
//               rpc {id, op, args}             cancel {id}
// Frames carry the layout's TARGET frame (UiLayout output) as a diff against the previous one; the TV runs the same
// UiAnimator, lowering and ElmFramePainter locally, so animation needs no traffic.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ru7100.Core.Ui;

namespace Ru7100.RemoteUi
{
    public static class RemoteUiProtocol
    {
        public const int Version = 1;
        public const int DefaultPort = 45560;
        public const int MaxMessageBytes = 32 * 1024 * 1024;
    }

    /// {"host": "192.168.1.10", "port": 45560, "token": "<random>"}: where the TV dials, and the shared secret.
    /// Lives in artifacts/ru7100-remote-ui.json (git-ignored); scripts/tv-dev.sh --remote-ui creates it and embeds it.
    public sealed class RemoteUiConfig
    {
        public string Host { get; set; }
        public int Port { get; set; } = RemoteUiProtocol.DefaultPort;
        public string Token { get; set; }

        public static RemoteUiConfig Parse(string json)
        {
            var config = JsonConvert.DeserializeObject<RemoteUiConfig>(json) ?? throw new FormatException("Empty remote UI config");
            if (string.IsNullOrEmpty(config.Host) || string.IsNullOrEmpty(config.Token) || config.Token.Length < 16)
                throw new FormatException("The remote UI config needs host and a token of at least 16 characters");
            return config;
        }

        /// Constant-time comparison for the hello token.
        public bool TokenMatches(string token)
        {
            if (token == null || token.Length != Token.Length) return false;
            var difference = 0;
            for (var i = 0; i < token.Length; i++) difference |= token[i] ^ Token[i];
            return difference == 0;
        }
    }

    /// Length-prefixed JSON messages. Send is thread-safe; one reader at a time.
    public sealed class RemoteUiChannel : IDisposable
    {
        private readonly Stream stream;
        private readonly object writeGate = new object();
        private readonly byte[] header = new byte[4];

        public RemoteUiChannel(Stream stream) => this.stream = stream;

        public long BytesSent;
        public long BytesReceived;

        public void Send(JObject message)
        {
            var body = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
            var prefix = new byte[4];
            prefix[0] = (byte)(body.Length >> 24);
            prefix[1] = (byte)(body.Length >> 16);
            prefix[2] = (byte)(body.Length >> 8);
            prefix[3] = (byte)body.Length;
            lock (writeGate)
            {
                stream.Write(prefix, 0, 4);
                stream.Write(body, 0, body.Length);
                stream.Flush();
                BytesSent += body.Length + 4;
            }
        }

        /// Null when the peer closed the connection.
        public async Task<JObject> ReceiveAsync(CancellationToken token)
        {
            if (!await FillAsync(header, 4, token).ConfigureAwait(false)) return null;
            var length = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            if (length < 0 || length > RemoteUiProtocol.MaxMessageBytes) throw new InvalidDataException("Bad message length " + length);
            var body = new byte[length];
            if (!await FillAsync(body, length, token).ConfigureAwait(false)) return null;
            BytesReceived += length + 4;
            return JObject.Parse(Encoding.UTF8.GetString(body));
        }

        private async Task<bool> FillAsync(byte[] buffer, int count, CancellationToken token)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = await stream.ReadAsync(buffer, offset, count - offset, token).ConfigureAwait(false);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }

        public void Dispose() => stream.Dispose();
    }

    /// Diffs of UiLayout target frames by node key. Changed nodes are sent with only their changed properties; the
    /// order list is sent only when the key order changes. Nodes are copied on write, so frames handed to the
    /// animator are never mutated (it compares old and new targets).
    public static class UiFrameDiff
    {
        private static readonly PropertyInfo[] NodeProperties = typeof(UiNode).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0).ToArray();
        private static readonly Dictionary<string, PropertyInfo> NodeByName = NodeProperties.ToDictionary(p => p.Name, StringComparer.Ordinal);
        private static readonly UiNode Defaults = new UiNode();
        private static readonly PropertyInfo[] FrameProperties = typeof(UiFrame).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0).ToArray();

        /// Diff from previous (null: full frame) to next. Duplicate keys are dropped, as UiAnimator does.
        public static JObject Make(UiFrame previous, UiFrame next)
        {
            var message = new JObject();
            var frameProps = new JObject();
            foreach (var p in FrameProperties)
            {
                var value = p.GetValue(next);
                if (previous == null || !Equals(p.GetValue(previous), value))
                    frameProps[p.Name] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
            }
            if (frameProps.Count > 0) message["frame"] = frameProps;

            var old = previous == null ? new Dictionary<string, UiNode>() : Index(previous);
            var nextIndex = new Dictionary<string, UiNode>(StringComparer.Ordinal);
            var order = new List<string>(next.Nodes.Count);
            var set = new JObject();
            foreach (var node in next.Nodes)
            {
                if (node.Key == null || nextIndex.ContainsKey(node.Key)) continue;
                nextIndex[node.Key] = node;
                order.Add(node.Key);
                old.TryGetValue(node.Key, out var before);
                var props = DiffNode(before, node);
                if (props != null) set[node.Key] = props;
            }
            if (set.Count > 0) message["set"] = set;

            var removed = old.Keys.Where(k => !nextIndex.ContainsKey(k)).ToList();
            if (removed.Count > 0) message["del"] = new JArray(removed);
            var previousOrder = previous == null ? null : Order(previous);
            if (previousOrder == null || !previousOrder.SequenceEqual(order, StringComparer.Ordinal))
                message["order"] = EncodeOrder(previousOrder, order);
            return message;
        }

        /// The new key order as runs of the previous order ([start, count]) and literal keys, so moving one node
        /// to the top costs a few tokens instead of the whole list.
        private static JArray EncodeOrder(List<string> previous, List<string> order)
        {
            var result = new JArray();
            if (previous == null)
            {
                foreach (var key in order) result.Add(key);
                return result;
            }
            var position = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < previous.Count; i++) position[previous[i]] = i;
            for (var i = 0; i < order.Count;)
            {
                if (!position.TryGetValue(order[i], out var start))
                {
                    result.Add(order[i++]);
                    continue;
                }
                var length = 1;
                while (i + length < order.Count && start + length < previous.Count && previous[start + length] == order[i + length]) length++;
                if (length == 1) result.Add(order[i]);
                else result.Add(new JArray(start, length));
                i += length;
            }
            return result;
        }

        private static IEnumerable<string> DecodeOrder(List<string> previous, JArray encoded)
        {
            foreach (var item in encoded)
            {
                if (item is JArray run)
                {
                    var start = (int)run[0];
                    var length = (int)run[1];
                    for (var i = start; i < start + length; i++) yield return previous[i];
                }
                else yield return (string)item;
            }
        }

        /// True when the diff changes nothing.
        public static bool IsEmpty(JObject diff) => !diff.Properties().Any();

        /// Applies a diff to current (null: empty) and returns a new frame; current and its nodes are not modified.
        public static UiFrame Apply(UiFrame current, JObject diff)
        {
            var next = new UiFrame();
            foreach (var p in FrameProperties)
                if (current != null) p.SetValue(next, p.GetValue(current));
            if (diff["frame"] is JObject frameProps)
            {
                foreach (var prop in frameProps.Properties())
                {
                    var info = FrameProperties.FirstOrDefault(p => p.Name == prop.Name);
                    info?.SetValue(next, prop.Value.Type == JTokenType.Null ? null : prop.Value.ToObject(info.PropertyType));
                }
            }

            var previousOrder = current == null ? new List<string>() : Order(current);
            var nodes = current == null ? new Dictionary<string, UiNode>(StringComparer.Ordinal) : Index(current);
            if (diff["del"] is JArray deleted)
                foreach (var key in deleted) nodes.Remove((string)key);
            if (diff["set"] is JObject set)
            {
                foreach (var entry in set.Properties())
                {
                    var node = nodes.TryGetValue(entry.Name, out var existing) ? existing.Clone() : new UiNode();
                    foreach (var prop in ((JObject)entry.Value).Properties())
                    {
                        if (!NodeByName.TryGetValue(prop.Name, out var info)) continue;   // a newer core: ignore unknown props
                        info.SetValue(node, prop.Value.Type == JTokenType.Null ? null : prop.Value.ToObject(info.PropertyType));
                    }
                    node.Key = entry.Name;
                    nodes[entry.Name] = node;
                }
            }
            var order = diff["order"] is JArray list ? DecodeOrder(previousOrder, list) : previousOrder;
            foreach (var key in order)
                if (nodes.TryGetValue(key, out var node)) next.Nodes.Add(node);
            return next;
        }

        private static JObject DiffNode(UiNode before, UiNode after)
        {
            JObject props = null;
            var reference = before ?? Defaults;
            foreach (var p in NodeProperties)
            {
                if (p.Name == nameof(UiNode.Key)) continue;
                var value = p.GetValue(after);
                if (Equals(p.GetValue(reference), value)) continue;
                (props ?? (props = new JObject()))[p.Name] = value == null ? JValue.CreateNull() : JToken.FromObject(value);
            }
            return before == null ? props ?? new JObject() : props;
        }

        private static Dictionary<string, UiNode> Index(UiFrame frame)
        {
            var index = new Dictionary<string, UiNode>(StringComparer.Ordinal);
            foreach (var node in frame.Nodes)
                if (node.Key != null && !index.ContainsKey(node.Key)) index[node.Key] = node;
            return index;
        }

        private static List<string> Order(UiFrame frame)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return frame.Nodes.Where(n => n.Key != null && seen.Add(n.Key)).Select(n => n.Key).ToList();
        }
    }

    /// Player state as the TV sends it: after every state change and RPC, and every 250 ms while it changes.
    public static class PlayerWire
    {
        public static JObject Snapshot(Ru7100.Core.IPlayerBackend player) => new JObject
        {
            ["t"] = "ps",
            ["state"] = (int)player.State,
            ["pos"] = player.PositionMs,
            ["dur"] = player.DurationMs,
            ["audio"] = JToken.FromObject(player.AudioTracks ?? new List<Ru7100.Core.TrackInfo>()),
            ["subs"] = JToken.FromObject(player.EmbeddedSubtitleTracks ?? new List<Ru7100.Core.TrackInfo>()),
            ["sel"] = (player as Ru7100.Core.Playback.ISubtitleCapablePlayer)?.SelectedAudio is int selected ? (JToken)selected : JValue.CreateNull(),
        };
    }
}
