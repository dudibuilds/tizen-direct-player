// Simulated playback engine behind IPlaybackCommands. Position advances with the clock while playing,
// so "active: true and positionMs rising" can be checked end to end without a TV.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.Api;

namespace Ru7100.Fakes
{
    public sealed class FakePlayback : IPlaybackCommands
    {
        private const long DurationMs = 45 * 60 * 1000;
        private readonly TitleResolver titles;
        private readonly ILibrary library;
        private readonly Func<DateTimeOffset> clock;
        private readonly object gate = new object();
        private PlaybackSession session;
        private PlaybackSession lastSession;
        private QueueState queue;
        private DateTimeOffset anchorTime;
        private long anchorPositionMs;

        public FakePlayback(ICatalog catalog, ILibrary library, Func<DateTimeOffset> clock = null)
        {
            titles = new TitleResolver(catalog);
            this.library = library;
            this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        }

        /// Every call as "Method arg1 arg2", for tests.
        public List<string> Calls { get; } = new List<string>();

        public async Task<PlayOutcome> PlayAsync(PlayRequest request, CancellationToken token)
        {
            Record("Play", request.Query, request.Season, request.Episode, request.Confirmed, request.Mode, request.Quality, request.MaxSizeGb, request.PositionSeconds);
            var parsed = TitleResolver.ParseEpisode(request.Query);
            var season = request.Season ?? parsed.Season;
            var episode = request.Episode ?? parsed.Episode;
            var resolved = await titles.ResolveAsync(string.IsNullOrEmpty(parsed.Query) ? request.Query : parsed.Query, token);
            if (resolved.Ambiguous) return new PlayOutcome { Status = "ambiguous", Candidates = resolved.Candidates };
            var meta = resolved.Meta;
            VideoInfo video;
            if (meta.Summary.Type == MediaType.Series)
            {
                if (season != null && episode != null)
                {
                    video = meta.Videos.FirstOrDefault(v => v.Season == season && v.Episode == episode)
                        ?? throw ApiException.NotFound("Episode not found");
                }
                else
                {
                    var entry = await library.GetAsync(meta.Summary.Id, token) ?? new LibraryEntry { Id = meta.Summary.Id };
                    video = library.NextVideo(entry, meta);
                    if (video == null) return new PlayOutcome { Status = "caught_up", Series = meta.Summary.Name };
                    if (!request.Confirmed)
                        return new PlayOutcome
                        {
                            Status = "confirmation_required", Series = meta.Summary.Name, Video = video, Mode = request.Mode,
                            Proposal = "Play S" + video.Season + "E" + video.Episode + "?",
                        };
                }
            }
            else
            {
                video = new VideoInfo { Id = meta.Summary.Id, Title = meta.Summary.Name };
                if (!request.Confirmed)
                    return new PlayOutcome { Status = "confirmation_required", Video = video, Mode = "single", Proposal = "Play " + meta.Summary.Name + "?" };
            }
            var started = Start(meta, video, request.Mode, request.Quality, request.MaxSizeGb, (long)((request.PositionSeconds ?? 0) * 1000));
            return new PlayOutcome { Status = "playing", Series = meta.Summary.Type == MediaType.Series ? meta.Summary.Name : null, Video = video, Mode = request.Mode, ResumeMs = started.PositionMs, Selected = Choice(video.Id), FallbacksTried = new SourceFailure[0], State = PlayerState.Playing, Session = started };
        }

        public Task<ControlResult> PauseAsync(CancellationToken token) => Control("pause", s => SetState(s, PlayerState.Paused));
        public Task<ControlResult> ResumeAsync(CancellationToken token) => Control("resume", s => SetState(s, PlayerState.Playing));

        public Task<ControlResult> StopAsync(CancellationToken token)
        {
            lock (gate)
            {
                Record("Stop");
                if (session == null) return Task.FromResult(new ControlResult { Action = "stop", State = PlayerState.Stopped });
                End("user_stop");
                return Task.FromResult(new ControlResult { Action = "stop", State = PlayerState.Stopped, PositionMs = lastSession.PositionMs, DurationMs = DurationMs });
            }
        }

        public Task<ControlResult> SeekAsync(double positionSeconds, CancellationToken token) =>
            Control("seek", s => { anchorPositionMs = Math.Min(DurationMs, (long)(positionSeconds * 1000)); anchorTime = clock(); }, positionSeconds);

        public Task<ControlResult> SetBingeAsync(bool enabled, CancellationToken token) =>
            Control(enabled ? "enable_binge" : "disable_binge", s => s.Mode = enabled ? "binge" : "single", enabled);

        public Task<ControlResult> SetStopAfterCurrentAsync(bool enabled, CancellationToken token) =>
            Control("stop_after_current", s => s.StopAfterCurrent = enabled, enabled);

        public Task<ControlResult> SelectAudioAsync(int index, CancellationToken token) =>
            Control("select_audio", s => s.SelectedAudio = CheckTrack(s.AudioTracks, index), index);

        public Task<ControlResult> SelectSubtitleAsync(int index, CancellationToken token) =>
            Control("select_subtitle", s => s.SelectedSubtitle = index < 0 ? -1 : CheckTrack(s.SubtitleTracks, index), index);

        public Task<ControlResult> SetSubtitleSizeAsync(int size, CancellationToken token) =>
            Control("subtitle_size", s => s.SubtitleSize = size, size);

        public Task<ControlResult> SetSubtitleOffsetAsync(int offsetMs, CancellationToken token) =>
            Control("subtitle_offset", s => s.SubtitleOffsetMs = offsetMs, offsetMs);

        public async Task<QueueResult> SetQueueAsync(QueueRequest request, CancellationToken token)
        {
            Record("SetQueue", string.Join("|", request.Items.Select(i => i.Query + "/" + i.Season + "/" + i.Episode)), request.Quality, request.MaxSizeGb);
            var entries = new List<QueueEntry>();
            for (var index = 0; index < request.Items.Count; index++)
            {
                var item = request.Items[index];
                var parsed = TitleResolver.ParseEpisode(item.Query);
                var resolved = await titles.ResolveAsync(parsed.Query, token);
                if (resolved.Ambiguous)
                    return new QueueResult { Status = "ambiguous", ItemIndex = index, Query = item.Query, Candidates = resolved.Candidates };
                var meta = resolved.Meta;
                if (meta.Summary.Type != MediaType.Series) throw ApiException.Invalid("Queue item " + (index + 1) + " is not a series");
                var season = item.Season ?? parsed.Season;
                var episode = item.Episode ?? parsed.Episode;
                VideoInfo video;
                if (season != null && episode != null) video = meta.Videos.FirstOrDefault(v => v.Season == season && v.Episode == episode);
                else
                {
                    var entry = await library.GetAsync(meta.Summary.Id, token) ?? new LibraryEntry { Id = meta.Summary.Id };
                    video = library.NextVideo(entry, meta);
                }
                if (video == null) throw ApiException.NotFound("Queue item " + (index + 1) + ": episode not found");
                entries.Add(new QueueEntry
                {
                    MetaId = meta.Summary.Id, MediaType = MediaType.Series, Name = meta.Summary.Name, VideoId = video.Id,
                    Season = video.Season, Episode = video.Episode, Title = video.Title, Position = index + 1,
                });
            }
            lock (gate)
            {
                queue = new QueueState
                {
                    Id = "queue:" + clock().ToUnixTimeMilliseconds(), Status = "ready", CurrentIndex = 0, Quality = request.Quality,
                    MaxSizeGb = Math.Min(request.MaxSizeGb ?? PlaybackLimits.SizeCapGb, PlaybackLimits.SizeCapGb),
                    CreatedAt = clock(), Items = entries,
                };
                return new QueueResult { Action = "set", Queue = queue };
            }
        }

        public async Task<QueueResult> StartQueueAsync(CancellationToken token)
        {
            Record("StartQueue");
            QueueEntry first;
            lock (gate)
            {
                if (queue == null) throw ApiException.Conflict("no_queue", "No Stremio queue. Call queue set first.");
                queue.CurrentIndex = 0;
                first = queue.Items[0];
            }
            await PlayQueueItemAsync(first, token);
            lock (gate) return Started("start", false);
        }

        public Task<QueueResult> ClearQueueAsync(CancellationToken token)
        {
            lock (gate)
            {
                Record("ClearQueue");
                queue = null;
                if (session != null && session.Mode == "queue") session.Mode = "single";
                return Task.FromResult(new QueueResult { Action = "clear", Queue = null });
            }
        }

        public async Task<QueueResult> SkipQueueAsync(CancellationToken token)
        {
            Record("SkipQueue");
            QueueEntry next;
            lock (gate)
            {
                if (queue == null || queue.Status != "playing") throw ApiException.Conflict("no_active_queue", "No active Stremio queue");
                End("queue_skip");
                queue.CurrentIndex++;
                if (queue.CurrentIndex >= queue.Items.Count)
                {
                    queue.CurrentIndex = queue.Items.Count - 1;
                    queue.Status = "completed";
                    queue.CompletedAt = clock();
                    return new QueueResult { Action = "skip", Status = "queue_complete", Skipped = true, Queue = MarkCurrent() };
                }
                next = queue.Items[queue.CurrentIndex];
            }
            await PlayQueueItemAsync(next, token);
            lock (gate) return Started("skip", true);
        }

        public Task<PlaybackSnapshot> GetStatusAsync(CancellationToken token)
        {
            lock (gate)
            {
                Tick();
                return Task.FromResult(new PlaybackSnapshot { Active = session != null, Session = session, LastSession = session == null ? lastSession : null, Queue = MarkCurrent() });
            }
        }

        private async Task PlayQueueItemAsync(QueueEntry item, CancellationToken token)
        {
            var resolved = await titles.ResolveAsync(item.MetaId, token);
            var video = resolved.Meta.Videos.First(v => v.Id == item.VideoId);
            lock (gate)
            {
                Start(resolved.Meta, video, "queue", queue.Quality, queue.MaxSizeGb, 0);
                queue.Status = "playing";
            }
        }

        private PlaybackSession Start(MetaDetail meta, VideoInfo video, string mode, string quality, double? maxSizeGb, long startMs)
        {
            lock (gate)
            {
                if (session != null) End("replaced");
                session = new PlaybackSession
                {
                    Id = meta.Summary.Id, Name = meta.Summary.Name, VideoId = video.Id,
                    Season = meta.Summary.Type == MediaType.Series ? video.Season : (int?)null,
                    Episode = meta.Summary.Type == MediaType.Series ? video.Episode : (int?)null,
                    Mode = mode, State = PlayerState.Playing, StartedAt = clock(), DurationMs = DurationMs, Quality = quality,
                    MaxSizeGb = Math.Min(maxSizeGb ?? PlaybackLimits.SizeCapGb, PlaybackLimits.SizeCapGb),
                    AudioTracks = new[] { new TrackInfo { Index = 0, Language = "eng", Label = "English" }, new TrackInfo { Index = 1, Language = "jpn", Label = "Japanese" } },
                    SubtitleTracks = new[] { new TrackInfo { Index = 0, Language = "spa", Label = "Spanish" }, new TrackInfo { Index = 1, Language = "eng", Label = "English" } },
                    SelectedSubtitle = 0,
                };
                anchorTime = clock();
                anchorPositionMs = startMs;
                session.PositionMs = startMs;
                return session;
            }
        }

        private Task<ControlResult> Control(string action, Action<PlaybackSession> apply, object argument = null)
        {
            lock (gate)
            {
                Record(action, argument);
                if (session == null) throw ApiException.Conflict("no_active_playback", "No active Stremio playback");
                Tick();
                apply(session);
                Tick();
                return Task.FromResult(new ControlResult
                {
                    Action = action, State = session.State, PositionMs = session.PositionMs, DurationMs = session.DurationMs,
                    Mode = session.Mode, Session = session,
                });
            }
        }

        private void SetState(PlaybackSession s, PlayerState state)
        {
            Tick();
            anchorPositionMs = s.PositionMs;
            anchorTime = clock();
            s.State = state;
        }

        private void Tick()
        {
            if (session == null) return;
            if (session.State == PlayerState.Playing)
                session.PositionMs = Math.Min(DurationMs, anchorPositionMs + (long)(clock() - anchorTime).TotalMilliseconds);
            else
                session.PositionMs = anchorPositionMs;
        }

        private void End(string reason)
        {
            Tick();
            session.State = PlayerState.Stopped;
            session.EndedAt = clock();
            session.EndReason = reason;
            lastSession = session;
            session = null;
        }

        private QueueState MarkCurrent()
        {
            if (queue == null) return null;
            foreach (var item in queue.Items) item.Current = item.Position == queue.CurrentIndex + 1;
            return queue;
        }

        private QueueResult Started(string action, bool skipped)
        {
            var item = queue.Items[queue.CurrentIndex];
            return new QueueResult
            {
                Action = action, Status = "playing", Skipped = skipped ? true : (bool?)null, Series = item.Name,
                Video = new VideoInfo { Id = item.VideoId, Season = item.Season, Episode = item.Episode, Title = item.Title },
                ResumeMs = 0, Mode = "queue", Selected = Choice(item.VideoId), FallbacksTried = new SourceFailure[0],
                State = session?.State, Queue = MarkCurrent(), Session = session,
            };
        }

        private static StreamChoice Choice(string videoId) => new StreamChoice
        {
            Addon = "com.stremio.torrentio.addon", Name = "Torrentio\n1080p", Title = videoId + " WEB-DL 1080p", Filename = videoId.Replace(':', '.') + ".mkv",
            Quality = "1080p", Seeders = 120, SizeBytes = 1_500_000_000, Codecs = new CodecInfo { Video = "h264", Audio = "aac" }, Score = 123.5,
        };

        private static int CheckTrack(IReadOnlyList<TrackInfo> tracks, int index)
        {
            if (index >= tracks.Count) throw ApiException.Invalid("No track " + index + "; the session has " + tracks.Count);
            return index;
        }

        private void Record(string name, params object[] args)
        {
            lock (Calls) Calls.Add((name + " " + string.Join(" ", args.Select(a => a == null ? "-" : Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture)))).Trim());
        }
    }
}
