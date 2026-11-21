using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Account
{
    /// Serializable library write operations kept in the Stremio outbox. Each carries enough metadata
    /// (a compact snapshot including the sorted video list when the bitfield is touched) to be replayed
    /// later without the catalog.
    public static class LibraryOps
    {
        public const string Add = "add";
        public const string Remove = "remove";
        public const string Notifications = "notifications";
        public const string Watched = "watched";
        public const string Started = "started";
        public const string Progress = "progress";
        public const string Completed = "completed";

        public static JObject Create(string kind, MetaDetail meta, bool includeVideos, DateTimeOffset at)
        {
            return new JObject
            {
                ["kind"] = kind,
                ["at"] = IsoTime.Format(at),
                ["meta"] = SnapshotMeta(meta, includeVideos),
            };
        }

        public static JObject CreateForId(string kind, string id, DateTimeOffset at)
        {
            return new JObject
            {
                ["kind"] = kind,
                ["at"] = IsoTime.Format(at),
                ["meta"] = new JObject { ["id"] = id },
            };
        }

        /// Snapshot taken from an existing raw item (keeps its exact type string, e.g. "tv").
        public static JObject CreateFromItem(string kind, JObject item, DateTimeOffset at)
        {
            return new JObject
            {
                ["kind"] = kind,
                ["at"] = IsoTime.Format(at),
                ["meta"] = new JObject
                {
                    ["id"] = LibraryRules.StringOrNull(item["_id"]),
                    ["type"] = LibraryRules.StringOrNull(item["type"]),
                    ["name"] = LibraryRules.StringOrNull(item["name"]),
                    ["poster"] = LibraryRules.StringOrNull(item["poster"]),
                },
            };
        }

        public static string MetaId(JObject op) => (string)op["meta"]?["id"];

        public static JObject SnapshotMeta(MetaDetail meta, bool includeVideos)
        {
            var summary = meta?.Summary ?? new MetaSummary();
            var snapshot = new JObject
            {
                ["id"] = summary.Id,
                ["type"] = LibraryRules.TypeName(summary.Type),
                ["name"] = summary.Name,
                ["poster"] = summary.Poster,
            };
            if (includeVideos)
            {
                snapshot["videos"] = new JArray(LibraryRules.SortVideos(meta?.Videos).Select(v => new JObject
                {
                    ["id"] = v.Id,
                    ["season"] = v.Season,
                    ["episode"] = v.Episode,
                    ["released"] = v.Released.HasValue ? IsoTime.Format(v.Released.Value) : null,
                }));
            }
            return snapshot;
        }

        /// Rebuilds a MetaDetail from the snapshot. Videos is null when the snapshot has none.
        public static MetaDetail RestoreMeta(JObject op, out bool hasVideos)
        {
            var snapshot = op["meta"] as JObject ?? new JObject();
            var videos = snapshot["videos"] as JArray;
            hasVideos = videos != null;
            return new MetaDetail
            {
                Summary = new MetaSummary
                {
                    Id = (string)snapshot["id"],
                    Type = (string)snapshot["type"] == "series" ? MediaType.Series : MediaType.Movie,
                    Name = (string)snapshot["name"],
                    Poster = (string)snapshot["poster"],
                },
                Videos = videos == null ? (IReadOnlyList<VideoInfo>)Array.Empty<VideoInfo>() : videos.OfType<JObject>().Select(v => new VideoInfo
                {
                    Id = (string)v["id"],
                    Season = (int)(v["season"] ?? 0),
                    Episode = (int)(v["episode"] ?? 0),
                    Released = IsoTime.Parse(v["released"]),
                }).ToList(),
            };
        }

        /// Applies the operation to a library item (existing may be null). Returns null when nothing
        /// should be written (removing an item the account does not have).
        public static JObject ApplyTo(JObject existing, JObject op, DateTimeOffset now)
        {
            string kind = (string)op["kind"];
            if (existing == null && (kind == Remove)) return null;
            var meta = RestoreMeta(op, out bool hasVideos);
            var snapshot = (JObject)op["meta"];
            DateTimeOffset at = IsoTime.Parse(op["at"]) ?? now;
            string type = (string)snapshot["type"];
            if (existing == null && type == null) return null;   // id-only change for an item we cannot create
            return LibraryRules.Upsert(existing, meta.Summary.Id, meta.Summary.Name, type, meta.Summary.Poster, item =>
            {
                var state = LibraryRules.State(item);
                switch (kind)
                {
                    case Add:
                        break;
                    case Remove:
                        item["removed"] = true;
                        break;
                    case Notifications:
                        state["noNotif"] = !(bool)op["enabled"];
                        break;
                    case Watched:
                    {
                        var video = new VideoInfo { Id = (string)op["videoId"], Released = IsoTime.Parse(op["released"]), Season = (int)(op["season"] ?? 0), Episode = (int)(op["episode"] ?? 0) };
                        LibraryRules.ApplyWatched(meta, item, video, (bool)op["watched"], at);
                        break;
                    }
                    case Started:
                        LibraryRules.ApplyStarted(item, (string)op["videoId"], (long)op["resumeMs"]);
                        break;
                    case Progress:
                        LibraryRules.ApplyProgress(item, null, (string)op["videoId"], (long)op["positionMs"], (long)op["durationMs"], false, at);
                        break;
                    case Completed:
                        LibraryRules.ApplyProgress(item, hasVideos || meta.Summary.Type == MediaType.Movie ? meta : null,
                            (string)op["videoId"], (long)op["positionMs"], (long)op["durationMs"], true, at);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown library operation: " + kind);
                }
            }, now);
        }

        /// Consecutive progress saves for the same title and video collapse into the newest one.
        public static bool CanReplace(JObject previous, JObject next)
        {
            return (string)previous["kind"] == Progress && (string)next["kind"] == Progress
                && MetaId(previous) == MetaId(next)
                && (string)previous["videoId"] == (string)next["videoId"];
        }
    }
}
