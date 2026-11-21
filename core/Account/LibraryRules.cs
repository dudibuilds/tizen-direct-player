using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Account
{
    public enum LibraryStatus { Library, Watching, Unwatched, CaughtUp }

    /// Pure parity rules for the Stremio library (C2-C9, D2-D5). Library items are raw JObjects so
    /// fields this app does not know about survive every read-modify-write.
    public static class LibraryRules
    {
        // ---- videos (B3) ----

        /// Sort by season, then episode, then released (stable, like Array.prototype.sort).
        public static IReadOnlyList<VideoInfo> SortVideos(IEnumerable<VideoInfo> videos)
        {
            return (videos ?? Enumerable.Empty<VideoInfo>())
                .OrderBy(v => v.Season)
                .ThenBy(v => v.Episode)
                .ThenBy(v => v.Released?.ToUnixTimeMilliseconds() ?? 0)
                .ToList();
        }

        /// Released means !released || released <= now.
        public static IReadOnlyList<VideoInfo> ReleasedVideos(MetaDetail meta, DateTimeOffset now)
        {
            return SortVideos(meta?.Videos).Where(v => !v.Released.HasValue || v.Released.Value <= now).ToList();
        }

        public static VideoInfo FindVideo(MetaDetail meta, string videoId)
        {
            return meta?.Videos?.FirstOrDefault(v => string.Equals(v.Id, videoId, StringComparison.Ordinal));
        }

        // ---- watched bitfield (C3) ----

        /// stremio-core.mjs episodeState: resize against the sorted video ids; blank on any parse error.
        public static WatchedBitField EpisodeState(MetaDetail meta, string serializedWatched)
        {
            var ids = SortVideos(meta?.Videos).Select(v => v.Id).ToList();
            if (string.IsNullOrEmpty(serializedWatched)) return WatchedBitField.Empty(ids);
            try
            {
                return WatchedBitField.ConstructAndResize(serializedWatched, ids);
            }
            catch (Exception)
            {
                return WatchedBitField.Empty(ids);
            }
        }

        // ---- next video (C5-C7) ----

        /// C6: first unwatched released video, preferring season > 0.
        public static VideoInfo NextUnwatchedVideo(MetaDetail meta, WatchedBitField watched, DateTimeOffset now)
        {
            var released = ReleasedVideos(meta, now);
            var regular = released.Where(v => v.Season > 0).ToList();
            var candidates = regular.Count > 0 ? (IReadOnlyList<VideoInfo>)regular : released;
            return candidates.FirstOrDefault(v => !watched.GetVideo(v.Id));
        }

        /// C7. current may be an episode missing from the metadata (only season/episode are used then).
        public static VideoInfo NextReleasedVideoAfter(MetaDetail meta, VideoInfo current, DateTimeOffset now)
        {
            var released = ReleasedVideos(meta, now);
            IReadOnlyList<VideoInfo> regular = current != null && current.Season > 0
                ? released.Where(v => v.Season > 0).ToList()
                : released;
            var candidates = regular.Count > 0 ? regular : released;
            int index = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (current != null && string.Equals(candidates[i].Id, current.Id, StringComparison.Ordinal)) { index = i; break; }
            }
            if (index >= 0) return index + 1 < candidates.Count ? candidates[index + 1] : null;
            if (current == null) return null;
            return candidates.FirstOrDefault(v => v.Season > current.Season || (v.Season == current.Season && v.Episode > current.Episode));
        }

        /// C5 (stremio.mjs nextLibraryVideo).
        public static VideoInfo NextLibraryVideo(MetaDetail meta, string currentVideoId, string serializedWatched, DateTimeOffset now)
        {
            var watched = EpisodeState(meta, serializedWatched);
            var current = currentVideoId == null ? null : FindVideo(meta, currentVideoId);
            if (current != null && !watched.GetVideo(current.Id)) return current;
            var next = current != null ? NextReleasedVideoAfter(meta, current, now) : null;
            while (next != null && watched.GetVideo(next.Id)) next = NextReleasedVideoAfter(meta, next, now);
            return next ?? NextUnwatchedVideo(meta, watched, now);
        }

        // ---- status (C4, C9) ----

        public static LibraryStatus Status(LibraryEntry entry, MetaDetail meta, DateTimeOffset now)
        {
            var state = entry?.State ?? new LibraryState();
            if (entry != null && entry.Type == MediaType.Series)
            {
                if (meta == null) return LibraryStatus.Library; // metadata unavailable: the old listing kept "library"
                var next = NextLibraryVideo(meta, state.VideoId, state.Watched, now);
                return state.TimeOffsetMs > 1000 ? LibraryStatus.Watching : next != null ? LibraryStatus.Unwatched : LibraryStatus.CaughtUp;
            }
            return state.TimeOffsetMs > 1000 ? LibraryStatus.Watching : LibraryStatus.Library;
        }

        /// round(timeOffset/duration*1000)/10, 0 when duration is unknown.
        public static double ProgressPercent(LibraryState state)
        {
            if (state == null || state.DurationMs <= 0) return 0;
            return Math.Round((double)state.TimeOffsetMs / state.DurationMs * 1000, MidpointRounding.AwayFromZero) / 10;
        }

        /// C9: released > (lastWatched || _ctime).
        public static bool IsNewEpisode(VideoInfo video, LibraryEntry entry)
        {
            if (video?.Released == null) return false;
            var baseline = entry?.State?.LastWatched ?? entry?.Created ?? DateTimeOffset.FromUnixTimeMilliseconds(0);
            return video.Released.Value > baseline;
        }

        // ---- libraryItem JSON (C2, D2) ----

        public static JObject DefaultState() => new JObject
        {
            ["lastWatched"] = null,
            ["timeWatched"] = 0,
            ["timeOffset"] = 0,
            ["overallTimeWatched"] = 0,
            ["timesWatched"] = 0,
            ["flaggedWatched"] = 0,
            ["duration"] = 0,
            ["video_id"] = null,
            ["watched"] = null,
            ["noNotif"] = false,
        };

        public static string TypeName(MediaType type) => type == MediaType.Series ? "series" : "movie";

        /// D2 upsert: start from the existing item (or the C2 default), force removed=false/temp=false,
        /// apply the change, overwrite name/type/poster from metadata, keep _ctime, set _mtime, and
        /// merge state over the defaults. Unknown fields of the existing item are preserved.
        public static JObject Upsert(JObject existing, string id, string name, string type, string poster,
            Action<JObject> mutate, DateTimeOffset now)
        {
            string nowIso = IsoTime.Format(now);
            JObject item = existing != null ? (JObject)existing.DeepClone() : new JObject
            {
                ["_id"] = id,
                ["name"] = name,
                ["type"] = type,
                ["poster"] = poster,
                ["posterShape"] = "poster",
                ["removed"] = false,
                ["temp"] = false,
                ["_ctime"] = nowIso,
                ["_mtime"] = nowIso,
                ["state"] = DefaultState(),
                ["behaviorHints"] = new JObject { ["defaultVideoId"] = null, ["featuredVideoId"] = null, ["hasScheduledVideos"] = false },
            };
            item["removed"] = false;
            item["temp"] = false;
            if (!(item["state"] is JObject)) item["state"] = DefaultState();
            mutate?.Invoke(item);
            if (!string.IsNullOrEmpty(name)) item["name"] = name;
            if (!string.IsNullOrEmpty(type)) item["type"] = type;
            item["poster"] = !string.IsNullOrEmpty(poster) ? poster : StremioApiClient.IsTruthy(item["poster"]) ? item["poster"] : JValue.CreateNull();
            if (!StremioApiClient.IsTruthy(item["_ctime"])) item["_ctime"] = nowIso;
            item["_mtime"] = nowIso;
            var merged = DefaultState();
            if (item["state"] is JObject state)
            {
                foreach (var property in state.Properties()) merged[property.Name] = property.Value.DeepClone();
            }
            item["state"] = merged;
            return item;
        }

        public static JObject State(JObject item)
        {
            if (!(item["state"] is JObject state))
            {
                state = DefaultState();
                item["state"] = state;
            }
            return state;
        }

        /// JS `value || 0` for numeric state fields.
        public static double Num(JToken token)
        {
            if (token == null) return 0;
            switch (token.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    var d = (double)token;
                    return double.IsNaN(d) ? 0 : d;
                case JTokenType.Boolean: return (bool)token ? 1 : 0;
                case JTokenType.String: return double.TryParse((string)token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
                default: return 0;
            }
        }

        static JToken NumToken(double value)
        {
            if (Math.Abs(value) < 9e15 && value == Math.Floor(value)) return new JValue((long)value);
            return new JValue(value);
        }

        /// stremio.mjs applyWatched (D4 series, D5 movie). video.Released feeds lastWatched for series.
        public static void ApplyWatched(MetaDetail meta, JObject item, VideoInfo video, bool watched, DateTimeOffset now)
        {
            var state = State(item);
            string nowIso = IsoTime.Format(now);
            if (meta.Summary?.Type == MediaType.Series)
            {
                var bits = EpisodeState(meta, StringOrNull(state["watched"]));
                bool wasWatched = bits.GetVideo(video.Id);
                bits.SetVideo(video.Id, watched);
                state["watched"] = bits.Serialize();
                if (watched != wasWatched)
                {
                    state["timesWatched"] = NumToken(watched ? Num(state["timesWatched"]) + 1 : Math.Max(0, Num(state["timesWatched"]) - 1));
                }
                if (watched)
                {
                    string candidate = video.Released.HasValue ? IsoTime.Format(video.Released.Value) : nowIso;
                    state["lastWatched"] = LaterDate(state["lastWatched"], new JValue(candidate));
                }
                var next = NextReleasedVideoAfter(meta, video, now);
                if (watched && next != null)
                {
                    state["overallTimeWatched"] = NumToken(Num(state["overallTimeWatched"]) + Num(state["timeWatched"]));
                    state["timeWatched"] = 0;
                    state["timeOffset"] = 1;
                    state["video_id"] = next.Id;
                }
                else if (watched)
                {
                    state["timeOffset"] = 0;
                }
            }
            else
            {
                state["flaggedWatched"] = watched ? 1 : 0;
                state["timesWatched"] = watched ? NumToken(Num(state["timesWatched"]) + 1) : new JValue(0);
                state["lastWatched"] = watched ? new JValue(nowIso) : JValue.CreateNull();
                state["timeOffset"] = 0;
            }
        }

        /// E1 playback start.
        public static void ApplyStarted(JObject item, string videoId, long resumeMs)
        {
            var state = State(item);
            bool sameVideo = string.Equals(StringOrNull(state["video_id"]), videoId, StringComparison.Ordinal);
            state["noNotif"] = false;
            state["video_id"] = videoId;
            state["duration"] = sameVideo ? NumToken(Num(state["duration"])) : new JValue(0);
            state["timeOffset"] = Math.Max(0, resumeMs);
            state["timeWatched"] = sameVideo ? NumToken(Num(state["timeWatched"])) : new JValue(0);
        }

        /// E2 periodic save / E3 completion (syncProgressNow). meta may be null (no bitfield change then).
        public static void ApplyProgress(JObject item, MetaDetail meta, string videoId, long positionMs, long durationMs, bool markWatched, DateTimeOffset now)
        {
            var state = State(item);
            state["video_id"] = videoId;
            state["duration"] = durationMs;
            state["timeOffset"] = markWatched ? 0 : positionMs;
            double watchedSoFar = Num(state["timeWatched"]);
            state["timeWatched"] = NumToken(Math.Max(watchedSoFar, positionMs));
            if (!markWatched || meta == null) return;
            // Cinemeta can lag behind stream add-ons: keep progress but leave the bitfield alone when the
            // episode is not in the metadata (its indexes only cover returned episodes).
            VideoInfo video = meta.Summary?.Type == MediaType.Series
                ? FindVideo(meta, videoId)
                : new VideoInfo { Id = meta.Summary?.Id };
            if (video != null) ApplyWatched(meta, item, video, true, now);
        }

        /// laterDate(a, b): new Date(a || 0) > new Date(b || 0) ? a : b.
        static JToken LaterDate(JToken a, JToken b)
        {
            long aa = StremioApiClient.IsTruthy(a) ? IsoTime.EpochMs(a) : 0;
            long bb = StremioApiClient.IsTruthy(b) ? IsoTime.EpochMs(b) : 0;
            return aa > bb ? a.DeepClone() : b.DeepClone();
        }

        internal static string StringOrNull(JToken token) =>
            token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined ? null : token.ToString();

        // ---- mapping to the shared contract ----

        /// Maps a raw libraryItem. Returns null for types other than movie/series.
        public static LibraryEntry ToEntry(JObject item)
        {
            if (item == null) return null;
            string type = StringOrNull(item["type"]);
            MediaType mediaType;
            if (type == "series") mediaType = MediaType.Series;
            else if (type == "movie") mediaType = MediaType.Movie;
            else return null;
            var state = item["state"] as JObject ?? new JObject();
            return new LibraryEntry
            {
                Id = StringOrNull(item["_id"]),
                Name = StringOrNull(item["name"]),
                Type = mediaType,
                Poster = StringOrNull(item["poster"]),
                Removed = StremioApiClient.IsTruthy(item["removed"]),
                Temp = StremioApiClient.IsTruthy(item["temp"]),
                Created = IsoTime.Parse(item["_ctime"]) ?? DateTimeOffset.FromUnixTimeMilliseconds(0),
                Modified = IsoTime.Parse(item["_mtime"]) ?? DateTimeOffset.FromUnixTimeMilliseconds(0),
                State = new LibraryState
                {
                    VideoId = StringOrNull(state["video_id"]),
                    TimeOffsetMs = (long)Num(state["timeOffset"]),
                    DurationMs = (long)Num(state["duration"]),
                    TimeWatchedMs = (long)Num(state["timeWatched"]),
                    OverallTimeWatchedMs = (long)Num(state["overallTimeWatched"]),
                    TimesWatched = (int)Num(state["timesWatched"]),
                    FlaggedWatched = (int)Num(state["flaggedWatched"]),
                    LastWatched = StremioApiClient.IsTruthy(state["lastWatched"]) ? IsoTime.Parse(state["lastWatched"]) : null,
                    Watched = StringOrNull(state["watched"]),
                    NoNotif = StremioApiClient.IsTruthy(state["noNotif"]),
                },
            };
        }
    }
}
