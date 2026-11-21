using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ru7100.Core.Playback
{
    public sealed class PlayResolution
    {
        public string Status { get; set; }       // PlayStatus.Playing (ready to start) | ConfirmationRequired | CaughtUp
        public VideoInfo Video { get; set; }
        public long ResumeMs { get; set; }
        public string Proposal { get; set; }
        public VideoInfo NextScheduled { get; set; }
    }

    /// H1-H3 plus the episode helpers from stremio-core.mjs (B3 released, C7 next released episode).
    public static class PlayResolver
    {
        public static PlayResolution Resolve(MetaDetail meta, LibraryEntry existing, ILibrary library, int? season, int? episode,
            bool confirmed, DateTimeOffset now)
        {
            var state = existing?.State;
            if (meta.Summary.Type == MediaType.Series)
            {
                if (season.HasValue || episode.HasValue)
                {
                    if (!season.HasValue || !episode.HasValue)
                        throw new ArgumentException("Both season and episode are required.");
                    // H1: explicit episode plays without confirmation.
                    var video = EpisodeForRequest(meta, season.Value, episode.Value);
                    long resume = 0;
                    if (state != null && state.VideoId == video.Id && IsResumable(state))
                        resume = state.TimeOffsetMs;
                    return new PlayResolution { Status = PlayStatus.Playing, Video = video, ResumeMs = resume };
                }

                // H2: bare series title.
                var current = state?.VideoId != null ? meta.Videos.FirstOrDefault(v => v.Id == state.VideoId) : null;
                VideoInfo chosen = null;
                long resumeMs = 0;
                if (current != null && state.DurationMs > 0 && state.TimeOffsetMs > 1000 && state.TimeOffsetMs < state.DurationMs * 0.9)
                {
                    chosen = current;
                    resumeMs = state.TimeOffsetMs;
                }
                else if (current != null && state.DurationMs > 0 && state.TimeOffsetMs >= state.DurationMs * 0.9)
                    chosen = NextReleasedVideoAfter(meta, current, now);
                if (chosen == null)
                    chosen = existing != null ? library.NextVideo(existing, meta) : NextUnwatchedFallback(meta, now);
                if (chosen == null)
                {
                    var upcoming = meta.Videos.FirstOrDefault(v => v.Released.HasValue && v.Released.Value > now);
                    return new PlayResolution { Status = PlayStatus.CaughtUp, NextScheduled = upcoming };
                }
                if (!confirmed)
                    return new PlayResolution
                    {
                        Status = PlayStatus.ConfirmationRequired, Video = chosen, ResumeMs = resumeMs,
                        Proposal = resumeMs > 0
                            ? $"Resume S{chosen.Season}E{chosen.Episode} at {FormatDuration(resumeMs)}?"
                            : $"Play S{chosen.Season}E{chosen.Episode}{(IsNewEpisode(chosen, existing) ? " (new episode)" : "")}?",
                    };
                return new PlayResolution { Status = PlayStatus.Playing, Video = chosen, ResumeMs = resumeMs };
            }

            // H3: movies always need confirmation.
            var movie = new VideoInfo { Id = meta.Summary.Id, Title = meta.Summary.Name };
            long movieResume = state != null && IsResumable(state) ? state.TimeOffsetMs : 0;
            if (!confirmed)
                return new PlayResolution
                {
                    Status = PlayStatus.ConfirmationRequired, Video = movie, ResumeMs = movieResume,
                    Proposal = $"Play {meta.Summary.Name}?",
                };
            return new PlayResolution { Status = PlayStatus.Playing, Video = movie, ResumeMs = movieResume };
        }

        public static bool IsResumable(LibraryState state) =>
            state.TimeOffsetMs > 1000 && state.TimeOffsetMs < state.DurationMs * 0.9;

        // Used only when the show is not in the library at all: first released regular episode.
        private static VideoInfo NextUnwatchedFallback(MetaDetail meta, DateTimeOffset now)
        {
            var released = ReleasedVideos(meta, now);
            return released.FirstOrDefault(v => v.Season > 0) ?? released.FirstOrDefault();
        }

        public static VideoInfo EpisodeForRequest(MetaDetail meta, int season, int episode) =>
            meta.Videos.FirstOrDefault(v => v.Season == season && v.Episode == episode)
            ?? new VideoInfo { Id = meta.Summary.Id + ":" + season + ":" + episode, Season = season, Episode = episode };

        public static List<VideoInfo> ReleasedVideos(MetaDetail meta, DateTimeOffset now) =>
            meta.Videos.Where(v => !v.Released.HasValue || v.Released.Value <= now).ToList();

        /// C7 (stremio-core.mjs:422-438).
        public static VideoInfo NextReleasedVideoAfter(MetaDetail meta, VideoInfo current, DateTimeOffset now)
        {
            var released = ReleasedVideos(meta, now);
            var regular = current != null && current.Season > 0 ? released.Where(v => v.Season > 0).ToList() : released;
            var candidates = regular.Count > 0 ? regular : released;
            var index = candidates.FindIndex(v => v.Id == current?.Id);
            if (index >= 0)
                return index + 1 < candidates.Count ? candidates[index + 1] : null;
            if (current == null)
                return null;
            return candidates.FirstOrDefault(v => v.Season > current.Season || (v.Season == current.Season && v.Episode > current.Episode));
        }

        public static bool IsNewEpisode(VideoInfo video, LibraryEntry existing)
        {
            if (!video.Released.HasValue)
                return false;
            var baseline = existing?.State?.LastWatched ?? existing?.Created ?? DateTimeOffset.MinValue;
            return video.Released.Value > baseline;
        }

        public static string FormatDuration(long ms)
        {
            var total = ms / 1000;
            return (total / 60).ToString(CultureInfo.InvariantCulture) + ":" + (total % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        /// F11 (the part decidable from metadata): Anime genre, or Japan + Animation.
        public static bool IsJapaneseAnime(MetaDetail meta)
        {
            var genres = meta.Genres ?? Array.Empty<string>();
            if (genres.Any(g => string.Equals(g?.Trim(), "anime", StringComparison.OrdinalIgnoreCase)))
                return true;
            return Regex.IsMatch(meta.Country ?? "", @"(?:^|,|\s)Japan(?:$|,)", RegexOptions.IgnoreCase)
                && genres.Any(g => string.Equals(g?.Trim(), "animation", StringComparison.OrdinalIgnoreCase));
        }

        // ---- B2 title resolution (only used when a request carries a free-text query) ----

        public sealed class TitleMatch
        {
            public MetaSummary Match { get; set; }
            public IReadOnlyList<MetaSummary> Ambiguous { get; set; }
        }

        public static async Task<TitleMatch> ResolveTitleAsync(ICatalog catalog, string query, MediaType? type, CancellationToken token)
        {
            var results = await catalog.SearchAsync(query, type, 5, token).ConfigureAwait(false);
            var ranked = results.Select(r => new { r, score = TitleSimilarity(query, r.Name) })
                .OrderByDescending(x => x.score).ThenByDescending(x => x.r.ReleaseInfo ?? "", StringComparer.Ordinal).ToList();
            if (ranked.Count == 0)
                return new TitleMatch();
            if (ranked.Count > 1 && ranked[0].score < 0.72 && ranked[0].score - ranked[1].score < 0.15)
                return new TitleMatch { Ambiguous = ranked.Take(5).Select(x => x.r).ToList() };
            return new TitleMatch { Match = ranked[0].r };
        }

        public static double TitleSimilarity(string a, string b)
        {
            string Norm(string s) => Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]+", " ").Trim();
            var x = Norm(a);
            var y = Norm(b);
            if (x.Length == 0 || y.Length == 0)
                return 0;
            if (x == y)
                return 1;
            if (x.Contains(y) || y.Contains(x))
                return 0.9;
            var wx = x.Split(' ');
            var wy = new HashSet<string>(y.Split(' '));
            var shared = wx.Distinct().Count(wy.Contains);
            return (double)shared / Math.Max(wx.Length, wy.Count);
        }
    }
}
