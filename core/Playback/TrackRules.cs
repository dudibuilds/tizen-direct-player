using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ru7100.Core.Playback
{
    /// What the add-on label promised about languages. Derived from StreamCandidate.LanguageTier
    /// because the shared contract carries no per-flag hints (see report: contract change).
    public sealed class LanguageHints
    {
        public bool EnglishAudio { get; set; }
        public bool JapaneseAudio { get; set; }
        public bool EnglishSubtitles { get; set; }
        public bool PreferredSubtitles { get; set; }

        public static LanguageHints FromCandidate(StreamCandidate candidate, bool anime)
        {
            var tier = candidate?.LanguageTier ?? 0;
            if (anime)
                return new LanguageHints { JapaneseAudio = tier == 5, EnglishSubtitles = tier == 5 };
            return new LanguageHints
            {
                EnglishAudio = tier >= 4,
                PreferredSubtitles = tier == 7 || tier == 3,
                EnglishSubtitles = tier == 6 || tier == 2,
            };
        }
    }

    public sealed class AudioDecision
    {
        public bool Accept { get; set; }
        public int? SelectIndex { get; set; }
        public string Language { get; set; }
        public string Reason { get; set; }
    }

    /// F12 and F14 against the TV player's own track list.
    public static class TrackRules
    {
        public const int MaxSubtitleOffsetMs = 30000;

        public static int ClampSubtitleOffset(int offsetMs) => Math.Max(-MaxSubtitleOffsetMs, Math.Min(MaxSubtitleOffsetMs, offsetMs));

        public static string DefaultAudioLanguage(bool anime) => anime ? Languages.AnimeAudio : Languages.Normalize(Languages.Audio);

        public static IReadOnlyList<string> DefaultSubtitleOrder(bool anime) => Languages.SubtitleOrder(anime);

        /// Normalizes a language tag, or failing that a track title, to a three-letter code (Languages.Normalize);
        /// null when unknown.
        public static string Normalize(string language, string label = null)
        {
            var code = Languages.Normalize(language);
            if (code.Length == 3 && Languages.Name(code) != code)
                return code;
            var named = Languages.FromTitle(label);
            if (named != null) return named;
            if (code.Length == 0 || code == "und" || code == "unknown" || code == "mul" || code == "zxx")
                return null;
            return code;
        }

        /// F12 on the TV. Differences from the Mac's ffprobe rule (media-normalizer.mjs:39-41):
        /// tracks whose language the TV reports as blank/und are unknown and accepted, and a known-wrong audio
        /// language rejects the source only when its label promised the required language (otherwise a film
        /// with only original-language audio could never play). A saved per-show language is preferred first.
        public static AudioDecision DecideAudio(IReadOnlyList<TrackInfo> tracks, bool anime, LanguageHints hints, string preferredLanguage)
        {
            tracks = tracks ?? Array.Empty<TrackInfo>();
            var required = DefaultAudioLanguage(anime);
            if (!string.IsNullOrEmpty(preferredLanguage))
            {
                var preferred = tracks.FirstOrDefault(t => Normalize(t.Language, t.Label) == preferredLanguage);
                if (preferred != null)
                    return new AudioDecision { Accept = true, SelectIndex = preferred.Index, Language = preferredLanguage, Reason = "show_preference" };
            }
            var match = tracks.FirstOrDefault(t => Normalize(t.Language, t.Label) == required);
            if (match != null)
                return new AudioDecision { Accept = true, SelectIndex = match.Index, Language = required, Reason = "required_language" };
            if (tracks.Count == 0)
                return new AudioDecision { Accept = true, Reason = "no_track_list" };
            if (tracks.Any(t => Normalize(t.Language, t.Label) == null))
                return new AudioDecision { Accept = true, Reason = tracks.Count == 1 ? "single_unknown_track" : "unknown_languages" };
            var promised = anime ? hints?.JapaneseAudio == true : hints?.EnglishAudio == true;
            if (promised)
                return new AudioDecision
                {
                    Accept = false,
                    Reason = (anime ? "Japanese" : "English") + " audio was promised, but the source has only "
                        + string.Join(", ", tracks.Select(t => Normalize(t.Language, t.Label)).Distinct()) + " audio.",
                };
            return new AudioDecision { Accept = true, Reason = "label_made_no_promise" };
        }

        // Releases often list a forced signs/songs track first; full dialogue wins, then SDH (media-normalizer.mjs:872-880).
        private static int Coverage(TrackInfo track)
        {
            var title = track.Label ?? "";
            if (Regex.IsMatch(title, @"\b(?:forced|signs?|songs?)\b", RegexOptions.IgnoreCase)) return 2;
            return Regex.IsMatch(title, @"\b(?:sdh|cc)\b|hearing", RegexOptions.IgnoreCase) ? 1 : 0;
        }

        public static TrackInfo BestEmbedded(IReadOnlyList<TrackInfo> tracks, string language) =>
            (tracks ?? Array.Empty<TrackInfo>()).Where(t => Normalize(t.Language, t.Label) == language)
                .OrderBy(Coverage).ThenBy(t => t.Index).FirstOrDefault();

        public static IReadOnlyList<string> SubtitleOrder(bool anime, string preferredLanguage)
        {
            var order = new List<string>();
            if (!string.IsNullOrEmpty(preferredLanguage) && preferredLanguage != "off")
                order.Add(preferredLanguage);
            foreach (var language in DefaultSubtitleOrder(anime))
                if (!order.Contains(language))
                    order.Add(language);
            return order;
        }

        /// True when the embedded tracks already satisfy the first-choice language, so external subtitles
        /// need not be awaited (F14: "If the embedded subtitle is in the first-choice language, skip external ones").
        public static bool EmbeddedSatisfiesFirstChoice(IReadOnlyList<TrackInfo> embedded, bool anime, string preferredLanguage)
        {
            if (preferredLanguage == "off")
                return true;
            return BestEmbedded(embedded, SubtitleOrder(anime, preferredLanguage)[0]) != null;
        }

        /// True when some acceptable embedded track exists (then wait at most 1.5 s for a better external one).
        public static bool EmbeddedHasAcceptable(IReadOnlyList<TrackInfo> embedded, bool anime, string preferredLanguage) =>
            SubtitleOrder(anime, preferredLanguage).Any(language => BestEmbedded(embedded, language) != null);

        /// F14: for each language in preference order, embedded beats external.
        public static SubtitleDecision DecideSubtitle(IReadOnlyList<TrackInfo> embedded, IReadOnlyList<SubtitleCandidate> external,
            bool anime, string preferredLanguage)
        {
            if (preferredLanguage == "off")
                return new SubtitleDecision { Kind = SubtitleKind.None, Reason = "show_preference_off" };
            foreach (var language in SubtitleOrder(anime, preferredLanguage))
            {
                var inside = BestEmbedded(embedded, language);
                if (inside != null)
                    return new SubtitleDecision { Kind = SubtitleKind.Embedded, EmbeddedIndex = inside.Index, Language = language, Reason = "embedded" };
                var outside = (external ?? Array.Empty<SubtitleCandidate>())
                    .FirstOrDefault(s => !string.IsNullOrEmpty(s.Url) && Normalize(s.Language) == language);
                if (outside != null)
                    return new SubtitleDecision { Kind = SubtitleKind.External, External = outside, Language = language, Reason = "external" };
            }
            return new SubtitleDecision { Kind = SubtitleKind.None, Reason = "no_preferred_language" };
        }
    }
}
