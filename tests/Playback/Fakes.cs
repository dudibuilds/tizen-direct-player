using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Playback;

namespace Ru7100.Tests.Playback
{
    internal sealed class ManualClock : IClock
    {
        private readonly object gate = new object();
        private readonly List<Timer> timers = new List<Timer>();
        private DateTimeOffset now = new DateTimeOffset(2026, 11, 18, 20, 0, 0, TimeSpan.Zero);

        private sealed class Timer
        {
            public DateTimeOffset Due;
            public TaskCompletionSource<bool> Completion;
            public long Order;
        }

        private long order;

        public DateTimeOffset UtcNow { get { lock (gate) return now; } }

        public Task Delay(TimeSpan delay, CancellationToken token)
        {
            if (token.IsCancellationRequested)
                return Task.FromCanceled(token);
            if (delay <= TimeSpan.Zero)
                return Task.CompletedTask;
            // Synchronous continuations: a timer's awaiter runs inline up to its next Delay, so timer chains
            // (hedging, deadlines) re-register before AdvanceAsync looks for the next due timer.
            var timer = new Timer { Completion = new TaskCompletionSource<bool>() };
            lock (gate)
            {
                timer.Due = now + delay;
                timer.Order = order++;
                timers.Add(timer);
            }
            if (token.CanBeCanceled)
                token.Register(() =>
                {
                    lock (gate)
                        timers.Remove(timer);
                    timer.Completion.TrySetCanceled(token);
                });
            return timer.Completion.Task;
        }

        /// Moves time forward, firing timers in due order and letting continuations run between them.
        public async Task AdvanceAsync(TimeSpan by)
        {
            DateTimeOffset target;
            lock (gate)
                target = now + by;
            while (true)
            {
                await Settle();
                Timer next;
                lock (gate)
                {
                    next = timers.Where(t => t.Due <= target).OrderBy(t => t.Due).ThenBy(t => t.Order).FirstOrDefault();
                    if (next == null)
                    {
                        now = target;
                        break;
                    }
                    timers.Remove(next);
                    if (next.Due > now)
                        now = next.Due;
                }
                next.Completion.TrySetResult(true);
            }
            await Settle();
        }

        public static async Task Settle()
        {
            for (var i = 0; i < 3; i++)
                await Task.Delay(5).ConfigureAwait(false);
        }
    }

    internal static class Wait
    {
        public static async Task Until(Func<bool> condition, string what = "condition", int timeoutMs = 5000)
        {
            var started = Environment.TickCount;
            while (!condition())
            {
                if (Environment.TickCount - started > timeoutMs)
                    throw new TimeoutException("Timed out waiting for " + what);
                await Task.Delay(5).ConfigureAwait(false);
            }
        }
    }

    internal sealed class MemoryStore : IKeyValueStore
    {
        public readonly ConcurrentDictionary<string, string> Values = new ConcurrentDictionary<string, string>();
        public Task<string> ReadAsync(string key, CancellationToken token) =>
            Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);
        public Task WriteAsync(string key, string value, CancellationToken token)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
    }

    internal sealed class FakeCatalog : ICatalog
    {
        public readonly Dictionary<string, MetaDetail> Metas = new Dictionary<string, MetaDetail>();
        public List<MetaSummary> SearchResults = new List<MetaSummary>();

        public Task<IReadOnlyList<MetaSummary>> SearchAsync(string query, MediaType? type, int limit, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<MetaSummary>>(SearchResults);

        public Task<MetaDetail> GetMetaAsync(MediaType type, string id, CancellationToken token)
        {
            if (Metas.TryGetValue(id, out var meta) && meta.Summary.Type == type)
                return Task.FromResult(meta);
            throw new InvalidOperationException("No meta " + id + " " + type);
        }

        public Task<IReadOnlyList<MetaSummary>> GetCatalogAsync(string addonId, string catalogId, MediaType type, int skip, CancellationToken token) =>
            throw new NotSupportedException();

        public static MetaDetail Series(string id, string name, int seasons, int episodes, string genre = "Drama", string country = "USA")
        {
            var videos = new List<VideoInfo>();
            for (var s = 1; s <= seasons; s++)
                for (var e = 1; e <= episodes; e++)
                    videos.Add(new VideoInfo
                    {
                        Id = id + ":" + s + ":" + e, Season = s, Episode = e, Title = "Episode " + e,
                        Released = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(s * 100 + e),
                    });
            return new MetaDetail
            {
                Summary = new MetaSummary { Id = id, Type = MediaType.Series, Name = name },
                Genres = new[] { genre }, Country = country, Videos = videos,
            };
        }

        public static MetaDetail Movie(string id, string name) => new MetaDetail
        {
            Summary = new MetaSummary { Id = id, Type = MediaType.Movie, Name = name },
        };
    }

    internal sealed class FakeLibrary : ILibrary
    {
        public readonly Dictionary<string, LibraryEntry> Entries = new Dictionary<string, LibraryEntry>();
        public readonly HashSet<string> Watched = new HashSet<string>();

        public Task<IReadOnlyList<LibraryEntry>> GetAllAsync(CancellationToken token) =>
            Task.FromResult<IReadOnlyList<LibraryEntry>>(Entries.Values.ToList());
        public Task<LibraryEntry> GetAsync(string id, CancellationToken token) =>
            Task.FromResult(Entries.TryGetValue(id, out var entry) ? entry : null);
        public Task AddAsync(MetaDetail meta, CancellationToken token) => Task.CompletedTask;
        public Task RemoveAsync(string id, CancellationToken token) => Task.CompletedTask;
        public Task SetNotificationsAsync(string id, bool enabled, CancellationToken token) => Task.CompletedTask;
        public Task MarkWatchedAsync(MetaDetail meta, string videoId, bool watched, CancellationToken token) => Task.CompletedTask;
        public bool IsWatched(LibraryEntry entry, MetaDetail meta, string videoId) => Watched.Contains(videoId);

        public VideoInfo NextVideo(LibraryEntry entry, MetaDetail meta)
        {
            var current = meta.Videos.FirstOrDefault(v => v.Id == entry.State.VideoId);
            if (current != null && !Watched.Contains(current.Id))
                return current;
            return meta.Videos.FirstOrDefault(v => !Watched.Contains(v.Id) && v.Season > 0);
        }
    }

    internal sealed class FakeResolver : ISourceResolver
    {
        public Func<string, IReadOnlyList<StreamCandidate>> Streams = _ => Array.Empty<StreamCandidate>();
        public IReadOnlyList<SubtitleCandidate> Subtitles = Array.Empty<SubtitleCandidate>();
        public Task<IReadOnlyList<SubtitleCandidate>> SubtitlesTask;
        public readonly List<(string VideoId, SourceOptions Options)> Requests = new List<(string, SourceOptions)>();

        public Task<IReadOnlyList<StreamCandidate>> GetRankedAsync(MediaType type, string videoId, SourceOptions options, CancellationToken token)
        {
            lock (Requests)
                Requests.Add((videoId, options));
            return Task.FromResult(Streams(videoId));
        }

        /// Answers the session's second query, made with the opened file's name, size and hash.
        public Func<StreamCandidate, IReadOnlyList<SubtitleCandidate>> SubtitlesForFile;
        /// Holds the file query's answer until the test completes it.
        public Task FileGate;
        public readonly List<StreamCandidate> SubtitleQueries = new List<StreamCandidate>();

        public Task<IReadOnlyList<SubtitleCandidate>> GetSubtitlesAsync(MediaType type, string videoId, StreamCandidate source, CancellationToken token)
        {
            lock (SubtitleQueries)
                SubtitleQueries.Add(source);
            if (source != null && SubtitlesForFile != null)
                return FileGate == null
                    ? Task.FromResult(SubtitlesForFile(source))
                    : FileGate.ContinueWith(_ => SubtitlesForFile(source), TaskScheduler.Default);
            return SubtitlesTask ?? Task.FromResult(Subtitles);
        }
    }

    /// A source that also serves the file's ends (for the OpenSubtitles hash), like the TV's torrent source.
    internal sealed class FakeSourceWithEnds : FakeSource, IFileEnds
    {
        public FakeSourceWithEnds(string url, byte[] head, byte[] tail) : base(url) { Head = head; Tail = tail; }

        public bool TryReadEnds(int count, out byte[] head, out byte[] tail)
        {
            head = Head;
            tail = Tail;
            return Head != null && Tail != null;
        }
    }

    internal class FakeSource : IOpenedSource, IPlayheadSource
    {
        /// The file's first and last bytes (FakeSourceWithEnds only).
        public byte[] Head, Tail;

        public FakeSource(string url, long length = 1_000_000_000) { PlayerUrl = url; LengthBytes = length; }
        public string PlayerUrl { get; }
        public long LengthBytes { get; }
        public double DownloadBytesPerSecond { get; set; } = 2_000_000;
        public int Peers { get; set; } = 10;
        public long ContiguousBytesAhead { get; set; } = -1;
        public int FirstMissingPiece { get; set; } = -1;
        public long ReadyThroughBytes { get; set; } = -1;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    internal sealed class OpenCall
    {
        public StreamCandidate Candidate;
        public DateTimeOffset At;
        public CancellationToken Token;
        public TaskCompletionSource<IOpenedSource> Completion;
        /// Set when the session opened through IReportingSourceOpener; tests call it to report swarm health.
        public Action<OpenProgress> Progress;
    }

    /// Opens instantly unless an identity is marked Blocked (then the test completes it) or Failing.
    internal sealed class FakeOpener : IReportingSourceOpener
    {
        private readonly ManualClock clock;
        public readonly List<OpenCall> Calls = new List<OpenCall>();
        public readonly HashSet<string> Blocked = new HashSet<string>();
        public readonly Dictionary<string, string> Failing = new Dictionary<string, string>();
        public readonly List<FakeSource> Opened = new List<FakeSource>();
        /// The file ends every opened source serves (null: sources without IFileEnds, like most tests).
        public byte[] Head, Tail;

        public FakeOpener(ManualClock clock) { this.clock = clock; }

        public int CallCount { get { lock (Calls) return Calls.Count; } }

        public OpenCall Call(string identityPrefix)
        {
            lock (Calls)
                return Calls.LastOrDefault(c => c.Candidate.Name == identityPrefix);
        }

        public Task<IOpenedSource> OpenAsync(StreamCandidate candidate, CancellationToken token) => OpenAsync(candidate, null, token);

        public Task<IOpenedSource> OpenAsync(StreamCandidate candidate, Action<OpenProgress> progress, CancellationToken token)
        {
            var call = new OpenCall
            {
                Candidate = candidate, At = clock.UtcNow, Token = token, Progress = progress,
                Completion = new TaskCompletionSource<IOpenedSource>(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            lock (Calls)
                Calls.Add(call);
            token.Register(() => call.Completion.TrySetCanceled(token));
            if (Failing.TryGetValue(candidate.Name, out var error))
                call.Completion.TrySetException(new InvalidOperationException(error));
            else if (!Blocked.Contains(candidate.Name))
                Complete(call);
            return call.Completion.Task;
        }

        public FakeSource Complete(OpenCall call)
        {
            var url = "http://127.0.0.1/" + call.Candidate.Name;
            var source = Head != null ? new FakeSourceWithEnds(url, Head, Tail) : new FakeSource(url);
            lock (Opened)
                Opened.Add(source);
            call.Completion.TrySetResult(source);
            return source;
        }
    }

    internal sealed class FakePlayer : IPlayerBackend, ISubtitleCapablePlayer, IInterruptiblePlayer, IVideoFormatSource
    {
        public int? VideoFps { get; set; }
        public string Interruption;
        public string TakeInterruption() { var reason = Interruption; Interruption = null; return reason; }

        public PlayerState State { get; set; } = PlayerState.Idle;
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public IReadOnlyList<TrackInfo> AudioTracks { get; set; } = Array.Empty<TrackInfo>();
        public IReadOnlyList<TrackInfo> EmbeddedSubtitleTracks { get; set; } = Array.Empty<TrackInfo>();
        public Func<string, IReadOnlyList<TrackInfo>> AudioFor = _ => Array.Empty<TrackInfo>();
        public Func<string, IReadOnlyList<TrackInfo>> SubtitlesFor = _ => Array.Empty<TrackInfo>();
        public readonly List<(string Url, long StartMs)> Opens = new List<(string, long)>();
        public int Stops;
        public int? SelectedAudio { get; private set; }
        public int? SelectedSubtitle { get; private set; } = -1;
        public int SubtitleOffset;
        public event EventHandler<PlayerState> StateChanged;
        public event EventHandler<SubtitleCue> SubtitleCue;

        public string LastUrl { get { lock (Opens) return Opens.Count == 0 ? null : Opens[Opens.Count - 1].Url; } }
        public int OpenCount { get { lock (Opens) return Opens.Count; } }

        /// Runs instead of the instant open after the call is recorded (a prepare that hangs or fails).
        public Func<string, long, CancellationToken, Task> OpenHook;

        public Task OpenAsync(string url, long startMs, CancellationToken token)
        {
            lock (Opens)
                Opens.Add((url, startMs));
            if (OpenHook != null)
                return OpenHook(url, startMs, token);
            AudioTracks = AudioFor(url);
            EmbeddedSubtitleTracks = SubtitlesFor(url);
            PositionMs = startMs;
            State = PlayerState.Paused;
            return Task.CompletedTask;
        }

        public void Play() { State = PlayerState.Playing; StateChanged?.Invoke(this, State); }
        public void Pause() { State = PlayerState.Paused; }
        /// Replaces the instant seek (e.g. one that never completes and ignores its token, like a wedged native seek).
        public Func<long, CancellationToken, Task> SeekHook;
        public Task SeekAsync(long positionMs, CancellationToken token)
        {
            if (SeekHook != null)
                return SeekHook(positionMs, token);
            PositionMs = positionMs;
            return Task.CompletedTask;
        }
        public void Stop() { Stops++; State = PlayerState.Stopped; }
        public void SelectAudio(int index) => SelectedAudio = index;
        public void SelectEmbeddedSubtitle(int? index) => SelectedSubtitle = index;
        public void SetEmbeddedSubtitleOffset(int offsetMs) => SubtitleOffset = offsetMs;
        public void RaiseCue(SubtitleCue cue) => SubtitleCue?.Invoke(this, cue);
    }

    internal sealed class FakeProgress : IProgressSink
    {
        public readonly List<string> Events = new List<string>();
        private void Add(string value) { lock (Events) Events.Add(value); }
        public Task PlaybackStartedAsync(MetaDetail meta, VideoInfo video, long resumeMs, CancellationToken token) { Add($"start {video.Id} {resumeMs}"); return Task.CompletedTask; }
        public Task ProgressAsync(MetaDetail meta, VideoInfo video, long positionMs, long durationMs, CancellationToken token) { Add($"progress {video.Id} {positionMs}"); return Task.CompletedTask; }
        public Task PausedAsync(MetaDetail meta, VideoInfo video, long positionMs, long durationMs, CancellationToken token) { Add($"paused {video.Id}"); return Task.CompletedTask; }
        public Task CompletedAsync(MetaDetail meta, VideoInfo video, CancellationToken token) { Add($"completed {video.Id}"); return Task.CompletedTask; }
        public Task StoppedAsync(MetaDetail meta, VideoInfo video, long positionMs, long durationMs, bool markedWatched, CancellationToken token) { Add($"stopped {video.Id} {markedWatched}"); return Task.CompletedTask; }
        public string[] Snapshot() { lock (Events) return Events.ToArray(); }
    }

    internal sealed class FakeSubtitleLoader : ISubtitleLoader
    {
        public readonly Dictionary<string, string> Files = new Dictionary<string, string>();
        /// Replaces the instant load (a server that sends headers and then stalls, ignoring the token).
        public Func<string, CancellationToken, Task<string>> Hook;
        public Task<string> LoadTextAsync(string url, CancellationToken token) => Hook != null ? Hook(url, token) : Task.FromResult(Files[url]);
    }

    /// Wires a PlaybackSession to fakes.
    internal sealed class Harness : IDisposable
    {
        public readonly ManualClock Clock = new ManualClock();
        public readonly FakeCatalog Catalog = new FakeCatalog();
        public readonly FakeLibrary Library = new FakeLibrary();
        public readonly FakeResolver Resolver = new FakeResolver();
        public readonly FakePlayer Player = new FakePlayer();
        public readonly FakeProgress Progress = new FakeProgress();
        public readonly FakeSubtitleLoader Subtitles = new FakeSubtitleLoader();
        public readonly MemoryStore Store;
        public readonly FakeOpener Opener;
        public readonly List<string> Log = new List<string>();
        public PlaybackSession Session;
        public readonly PlaybackOptions Options = new PlaybackOptions { AutoMonitor = false };

        public Harness(MemoryStore store = null)
        {
            Store = store ?? new MemoryStore();
            Opener = new FakeOpener(Clock);
        }

        public async Task<PlaybackSession> StartAsync()
        {
            Session = new PlaybackSession(new PlaybackServices
            {
                Catalog = Catalog, Library = Library, Resolver = Resolver, Opener = Opener, Player = Player, Progress = Progress,
                Store = Store, Clock = Clock, Subtitles = Subtitles, Log = message => { lock (Log) Log.Add(message); },
            }, Options);
            await Session.InitializeAsync(CancellationToken.None);
            return Session;
        }

        public static StreamCandidate Torrent(string name, int tier = 7, int band = 2, int? fileIndex = null)
        {
            // Stable per name, so repeated resolver calls return the same identities.
            var digest = System.Security.Cryptography.SHA1.Create().ComputeHash(System.Text.Encoding.UTF8.GetBytes(name));
            var hash = string.Concat(digest.Select(b => b.ToString("x2")));
            return new StreamCandidate { Name = name, InfoHash = hash, FileIndex = fileIndex, LanguageTier = tier, HealthBand = band, Quality = "1080p" };
        }

        /// Simulates `count` monitor ticks, 5 s apart, with the playhead advancing by `stepMs` each tick.
        public async Task PlayTicksAsync(int count, long stepMs = 5000)
        {
            for (var i = 0; i < count; i++)
            {
                await Clock.AdvanceAsync(TimeSpan.FromSeconds(5));
                Player.PositionMs += stepMs;
                await Session.TickAsync();
            }
        }

        public void Dispose() => Session?.Dispose();
    }
}
