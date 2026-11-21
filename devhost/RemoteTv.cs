using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Ru7100.Core;
using Ru7100.Core.Playback;
using Ru7100.RemoteUi;

namespace Ru7100.DevHost
{
    /// The connected TV as seen by the Mac: request/response calls over the remote UI channel.
    internal sealed class RemoteTv
    {
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JObject>> pending = new ConcurrentDictionary<long, TaskCompletionSource<JObject>>();
        private long nextId;
        private volatile RemoteUiChannel channel;

        public RemoteTv()
        {
            Player = new RemotePlayer(this);
            Opener = new RemoteOpener(this);
        }

        public RemotePlayer Player { get; }
        public RemoteOpener Opener { get; }
        public bool Connected => channel != null;

        public void Attach(RemoteUiChannel connected) => channel = connected;

        /// The TV went away: every call in flight fails, and the player reads as idle.
        public void Detach(RemoteUiChannel gone)
        {
            if (channel != gone) return;
            channel = null;
            foreach (var id in pending.Keys)
                if (pending.TryRemove(id, out var call))
                    call.TrySetException(new InvalidOperationException("The TV disconnected"));
            Player.Reset();
            Opener.Reset();
        }

        public void Send(JObject message)
        {
            var ch = channel;
            if (ch == null) return;
            try { ch.Send(message); } catch (Exception) { }
        }

        /// Runs op on the TV. Fire-and-forget callers ignore the task (failures are logged).
        public async Task<JObject> CallAsync(string op, JObject args, CancellationToken token = default)
        {
            var ch = channel ?? throw new InvalidOperationException("The TV is not connected (start the remote UI client)");
            var id = Interlocked.Increment(ref nextId);
            if (op != "source.dispose") Console.WriteLine("tv call " + op);
            var call = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = call;
            using (token.Register(() => Send(new JObject { ["t"] = "cancel", ["id"] = id })))
            {
                ch.Send(new JObject { ["t"] = "rpc", ["id"] = id, ["op"] = op, ["args"] = args ?? new JObject() });
                var result = await call.Task.ConfigureAwait(false);
                if (result["ps"] is JObject snapshot) Player.Apply(snapshot);
                if ((bool?)result["ok"] == true) return result["value"] as JObject;
                if ((bool?)result["cancelled"] == true) throw new OperationCanceledException("TV: " + (string)result["error"], token);
                throw new InvalidOperationException("TV: " + (string)result["error"]);
            }
        }

        public void Fire(string op, JObject args = null) =>
            CallAsync(op, args).ContinueWith(t => Console.WriteLine("tv " + op + ": " + t.Exception.GetBaseException().Message), TaskContinuationOptions.OnlyOnFaulted);

        /// Messages from the TV other than keys, hello and stats.
        public void OnMessage(JObject message)
        {
            switch ((string)message["t"])
            {
                case "result":
                    if (pending.TryRemove((long)message["id"], out var call)) call.TrySetResult(message);
                    break;
                case "ps":
                    Player.Apply(message);
                    break;
                case "cue":
                    Player.OnCue(message);
                    break;
                case "src":
                    Opener.OnStats(message);
                    break;
            }
        }
    }

    /// The TV's TizenPlayerBackend, driven over the network. Getters read the latest snapshot the TV sent (position
    /// is extrapolated between snapshots while playing).
    internal sealed class RemotePlayer : IPlayerBackend, ISubtitleCapablePlayer, IVideoAreaPlayer
    {
        private readonly RemoteTv tv;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly object gate = new object();
        private PlayerState state = PlayerState.Idle;
        private long positionMs, durationMs, snapshotAt;
        private IReadOnlyList<TrackInfo> audio = Array.Empty<TrackInfo>();
        private IReadOnlyList<TrackInfo> subtitles = Array.Empty<TrackInfo>();
        private int? selectedAudio;

        public RemotePlayer(RemoteTv tv) => this.tv = tv;

        public event EventHandler<PlayerState> StateChanged;
        public event EventHandler<SubtitleCue> SubtitleCue;

        public PlayerState State { get { lock (gate) return state; } }

        public long PositionMs
        {
            get
            {
                lock (gate)
                {
                    if (state != PlayerState.Playing) return positionMs;
                    var estimate = positionMs + (clock.ElapsedMilliseconds - snapshotAt);
                    return durationMs > 0 ? Math.Min(estimate, durationMs) : estimate;
                }
            }
        }

        public long DurationMs { get { lock (gate) return durationMs; } }
        public IReadOnlyList<TrackInfo> AudioTracks { get { lock (gate) return audio; } }
        public IReadOnlyList<TrackInfo> EmbeddedSubtitleTracks { get { lock (gate) return subtitles; } }
        public int? SelectedAudio { get { lock (gate) return selectedAudio; } }

        public Task OpenAsync(string url, long startMs, CancellationToken token) =>
            tv.CallAsync("player.open", new JObject { ["url"] = url, ["startMs"] = startMs }, token);

        public void Play() => tv.Fire("player.play");
        public void Pause() => tv.Fire("player.pause");
        public void Stop() => tv.Fire("player.stop");

        public Task SeekAsync(long positionMs, CancellationToken token) =>
            tv.CallAsync("player.seek", new JObject { ["positionMs"] = positionMs }, token);

        public void SelectAudio(int index) => tv.Fire("player.audio", new JObject { ["index"] = index });
        public void SelectEmbeddedSubtitle(int? index) => tv.Fire("player.subtitle", new JObject { ["index"] = index.HasValue ? (JToken)index.Value : JValue.CreateNull() });
        public void SetEmbeddedSubtitleOffset(int offsetMs) => tv.Fire("player.subtitleOffset", new JObject { ["offsetMs"] = offsetMs });
        public void SetFullscreen() => tv.Fire("player.fullscreen");
        public void SetVideoArea(int x, int y, int width, int height) =>
            tv.Fire("player.area", new JObject { ["x"] = x, ["y"] = y, ["w"] = width, ["h"] = height });

        public void Apply(JObject snapshot)
        {
            PlayerState changedTo;
            lock (gate)
            {
                var next = (PlayerState)(int)snapshot["state"];
                positionMs = (long)snapshot["pos"];
                durationMs = (long)snapshot["dur"];
                snapshotAt = clock.ElapsedMilliseconds;
                audio = snapshot["audio"]?.ToObject<List<TrackInfo>>() ?? new List<TrackInfo>();
                subtitles = snapshot["subs"]?.ToObject<List<TrackInfo>>() ?? new List<TrackInfo>();
                selectedAudio = (int?)snapshot["sel"];
                changedTo = next != state ? next : (PlayerState)(-1);
                state = next;
            }
            if (changedTo != (PlayerState)(-1)) StateChanged?.Invoke(this, changedTo);
        }

        public void OnCue(JObject message) => SubtitleCue?.Invoke(this, new SubtitleCue
        {
            Text = (string)message["text"], StartMs = (long)message["startMs"], DurationMs = (long)message["durationMs"], Embedded = (bool)message["embedded"],
        });

        public void Reset()
        {
            bool changed;
            lock (gate)
            {
                changed = state != PlayerState.Idle;
                state = PlayerState.Idle;
                audio = subtitles = Array.Empty<TrackInfo>();
            }
            if (changed) StateChanged?.Invoke(this, PlayerState.Idle);
        }
    }

    /// The TV's TorrentSourceOpener: the torrent engine and its loopback server run on the TV, and the player URL it
    /// returns is a TV-local address that only the TV's player opens.
    internal sealed class RemoteOpener : ISourceOpener
    {
        private readonly RemoteTv tv;
        private readonly ConcurrentDictionary<string, RemoteSource> open = new ConcurrentDictionary<string, RemoteSource>();

        public RemoteOpener(RemoteTv tv) => this.tv = tv;

        public async Task<IOpenedSource> OpenAsync(StreamCandidate candidate, CancellationToken token)
        {
            var value = await tv.CallAsync("opener.open", new JObject { ["candidate"] = JObject.FromObject(candidate) }, token).ConfigureAwait(false);
            var source = new RemoteSource(this, (string)value["id"], (string)value["url"], (long)value["length"]);
            open[source.Id] = source;
            return source;
        }

        public void OnStats(JObject message)
        {
            if (open.TryGetValue((string)message["id"], out var source))
            {
                source.DownloadBytesPerSecond = (double)message["rate"];
                source.Peers = (int)message["peers"];
            }
        }

        public void Reset() => open.Clear();

        private void Closed(RemoteSource source)
        {
            if (open.TryRemove(source.Id, out _))
                tv.Fire("source.dispose", new JObject { ["id"] = source.Id });
        }

        private sealed class RemoteSource : IOpenedSource
        {
            private readonly RemoteOpener owner;

            public RemoteSource(RemoteOpener owner, string id, string url, long length)
            {
                this.owner = owner;
                Id = id;
                PlayerUrl = url;
                LengthBytes = length;
            }

            public string Id { get; }
            public string PlayerUrl { get; }
            public long LengthBytes { get; }
            public double DownloadBytesPerSecond { get; set; }
            public int Peers { get; set; }
            public void Dispose() => owner.Closed(this);
        }
    }
}
