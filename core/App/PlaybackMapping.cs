using System;
using System.Collections.Generic;
using System.Linq;
using Ru7100.Core.Playback;
using Ru7100.Core.Ui;

namespace Ru7100.Core.App
{
    /// One subtitle choice as both front ends see it. The remote UI addresses entries by Id ("embedded:2",
    /// "external:<url>"); the agent API addresses them by their position in this list (embedded tracks first, in
    /// player order, then external files in add-on order). Both resolve to the same engine SubtitleDecision.
    public sealed class SubtitleMenuEntry
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Language { get; set; }
        public bool External { get; set; }
        public SubtitleDecision Decision { get; set; }
    }

    public static class PlaybackMapping
    {
        public const string EmbeddedPrefix = "embedded:";
        public const string ExternalPrefix = "external:";

        /// Session end reasons that are immediately followed by another item or source.
        private static readonly HashSet<string> TransitionReasons = new HashSet<string>(StringComparer.Ordinal)
            { "episode_advance", "stream_recovery", "user_play", "restore", "queue_skip" };

        /// How long a transition end may leave the session without an active record before the UI treats it as over.
        public static readonly TimeSpan TransitionGrace = TimeSpan.FromSeconds(3);

        public static IReadOnlyList<SubtitleMenuEntry> SubtitleMenu(PlaybackStatus status, IReadOnlyList<SubtitleCandidate> externals)
        {
            var menu = new List<SubtitleMenuEntry>();
            foreach (var track in status?.Tracks?.EmbeddedSubtitles ?? Array.Empty<TrackInfo>())
            {
                if (track == null) continue;
                menu.Add(new SubtitleMenuEntry
                {
                    Id = EmbeddedPrefix + track.Index,
                    Label = EmbeddedLabel(track),
                    Language = track.Language,
                    Decision = new SubtitleDecision { Kind = SubtitleKind.Embedded, EmbeddedIndex = track.Index },
                });
            }
            var files = new List<SubtitleCandidate>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var chosen = status?.Tracks?.Subtitle;
            if (chosen?.Kind == SubtitleKind.External && chosen.External?.Url != null && seen.Add(chosen.External.Url))
                files.Add(chosen.External);
            foreach (var file in externals ?? Array.Empty<SubtitleCandidate>())
                if (file?.Url != null && seen.Add(file.Url))
                    files.Add(file);
            var perLanguage = new Dictionary<string, int>(StringComparer.Ordinal);
            var labels = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var language = PlayerSettings.Normalize(file.Language);
                perLanguage.TryGetValue(language, out var count);
                perLanguage[language] = ++count;
                var tag = SubtitleMatching.MenuTag(file);
                var label = UiFormat.LanguageName(file.Language) + (tag != null ? " · " + tag : count > 1 ? " " + count : "");
                labels.TryGetValue(label, out var same);
                labels[label] = ++same;
                menu.Add(new SubtitleMenuEntry
                {
                    Id = ExternalPrefix + file.Url,
                    Label = same > 1 ? label + " (" + same + ")" : label,
                    Language = file.Language,
                    External = true,
                    Decision = new SubtitleDecision { Kind = SubtitleKind.External, External = file },
                });
            }
            return menu;
        }

        /// What the subtitle sync did, in the viewer's words; null when the subtitle is used as it came.
        public static string SyncLabel(SubtitleTiming timing)
        {
            if (timing.IsIdentity || timing.Source == null)
                return null;
            var speed = timing.SpeedName;
            switch (timing.Source)
            {
                case "reference": return "Synced to your file" + (speed != null ? " · " + speed : "");
                case "offset": return "Speed fixed" + (speed != null ? " · " + speed : "");
                case "remembered": return "Speed fixed like last episode" + (speed != null ? " · " + speed : "");
                default: return null;
            }
        }

        private static string EmbeddedLabel(TrackInfo track)
        {
            var name = UiFormat.LanguageName(track.Language);
            var label = track.Label;
            return string.IsNullOrEmpty(label) || label == track.Language || string.Equals(label, name, StringComparison.OrdinalIgnoreCase)
                ? name : name + " · " + label;
        }

        public static string SelectedSubtitleId(SubtitleDecision decision)
        {
            if (decision == null) return null;
            switch (decision.Kind)
            {
                case SubtitleKind.Embedded: return decision.EmbeddedIndex.HasValue ? EmbeddedPrefix + decision.EmbeddedIndex.Value : null;
                case SubtitleKind.External: return decision.External?.Url == null ? null : ExternalPrefix + decision.External.Url;
                default: return null;
            }
        }

        public static int SelectedSubtitleIndex(IReadOnlyList<SubtitleMenuEntry> menu, SubtitleDecision decision)
        {
            var id = SelectedSubtitleId(decision);
            if (id == null) return -1;
            for (var i = 0; i < menu.Count; i++)
                if (menu[i].Id == id) return i;
            return -1;
        }

        /// True while the UI should keep showing the player: a session is active, a start or source switch is in
        /// progress, or the previous item ended only moments ago for a transition (binge advance, recovery, skip).
        /// The engine briefly has no active record between ending one item and committing the next.
        public static bool IsSessionActive(PlaybackStatus status, DateTimeOffset now)
        {
            if (status == null) return false;
            if (status.Active) return true;
            if (status.Phase == "resolving" || status.Phase == "starting" || status.Phase == "recovering") return true;
            var last = status.LastSession;
            return last?.EndedAt != null && last.EndReason != null && TransitionReasons.Contains(last.EndReason)
                && now - last.EndedAt.Value < TransitionGrace && now >= last.EndedAt.Value - TransitionGrace;
        }

        public static string SourceLabel(StreamCandidate candidate, long sizeBytes)
        {
            if (candidate == null) return null;
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(candidate.Quality) && candidate.Quality != "unknown") parts.Add(candidate.Quality);
            var size = UiFormat.Size(sizeBytes > 0 ? sizeBytes : candidate.SizeBytes);
            if (!string.IsNullOrEmpty(size)) parts.Add(size);
            var addon = (candidate.Name ?? "").Split('\n')[0].Trim();
            if (addon.Length > 0) parts.Add(addon);
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }

        public static string QueueStatusForUi(string status)
        {
            switch (status)
            {
                case null: return "empty";
                case QueueStatuses.Starting: return "playing";
                default: return status;
            }
        }
    }
}
