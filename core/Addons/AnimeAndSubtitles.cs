using System;
using Ru7100.Core.Playback;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ru7100.Core.Addons
{
    /// F11 anime detection.
    public static class AnimeDetection
    {
        private static readonly Regex Japan = new Regex(@"(?:^|,|\s)Japan(?:$|,)", RegexOptions.IgnoreCase | RegexOptions.ECMAScript);
        private static readonly Regex AmbiguousCountry = new Regex(@"(?:^|,|\s)(?:South Korea|China|Taiwan)(?:$|,)", RegexOptions.IgnoreCase | RegexOptions.ECMAScript);

        /// Genre "Anime", or country Japan with genre "Animation".
        public static bool IsJapaneseAnime(MetaDetail meta)
        {
            if (meta == null) return false;
            if (HasGenre(meta, "anime")) return true;
            return Japan.IsMatch(meta.Country ?? "") && HasGenre(meta, "animation");
        }

        /// Animation with no country, or South Korea/China/Taiwan: anime only if a stream has Japanese audio.
        public static bool IsAmbiguousAnimation(MetaDetail meta)
        {
            if (meta == null || !HasGenre(meta, "animation")) return false;
            var country = (meta.Country ?? "").Trim();
            return country.Length == 0 || AmbiguousCountry.IsMatch(country);
        }

        /// Whether ranking should use the anime rules (F7) for this title and these streams.
        public static bool PrefersJapaneseAudio(MetaDetail meta, IEnumerable<AddonStreamItem> streams)
        {
            if (IsJapaneseAnime(meta)) return true;
            if (!IsAmbiguousAnimation(meta)) return false;
            return StreamRanking.RankAnalyses(streams, new RankOptions { Episodic = meta.Summary?.Type == MediaType.Series, Anime = true })
                .Any(entry => entry.JapaneseAudio);
        }

        private static bool HasGenre(MetaDetail meta, string genre) =>
            (meta.Genres ?? Array.Empty<string>()).Any(item => string.Equals((item ?? "").Trim(), genre, StringComparison.OrdinalIgnoreCase));
    }

    /// F14 external subtitle choice. Embedded-track rules live with the player.
    public static class SubtitleSelection
    {
        /// A language tag as a three-letter code ("es-MX" -> "spa"), otherwise the lowercased code without region.
        public static string NormalizeLanguage(string language) => Languages.Normalize(language);

        public static IReadOnlyList<string> PreferredLanguages(bool anime) => Languages.SubtitleOrder(anime);

        /// The preferred subtitle language, then English (anime: English only). Null when none matches.
        public static SubtitleCandidate SelectPreferred(IEnumerable<SubtitleCandidate> subtitles, bool anime)
        {
            var available = (subtitles ?? Enumerable.Empty<SubtitleCandidate>()).Where(item => item != null && !string.IsNullOrEmpty(item.Url)).ToList();
            foreach (var language in PreferredLanguages(anime))
            {
                var match = available.FirstOrDefault(item => NormalizeLanguage(item.Language) == language);
                if (match != null) return match;
            }
            return null;
        }

        /// Preferred languages first in preference order, then the rest in add-on order. Stable.
        public static IReadOnlyList<SubtitleCandidate> Order(IEnumerable<SubtitleCandidate> subtitles, bool anime)
        {
            var preferred = PreferredLanguages(anime);
            return (subtitles ?? Enumerable.Empty<SubtitleCandidate>())
                .Where(item => item != null && !string.IsNullOrEmpty(item.Url))
                .OrderBy(item =>
                {
                    var index = Array.IndexOf(preferred.ToArray(), NormalizeLanguage(item.Language));
                    return index < 0 ? int.MaxValue : index;
                })
                .ToList();
        }

        /// F14: skip external subtitles when the embedded track is already in the first-choice language.
        public static bool ShouldSkipExternal(string embeddedLanguage, bool anime) =>
            NormalizeLanguage(embeddedLanguage) == PreferredLanguages(anime)[0];

        /// F14: with an embedded English track (non-anime), wait at most this long for an external one in the
        /// preferred language.
        public static readonly TimeSpan EnglishEmbeddedWait = TimeSpan.FromMilliseconds(1500);

        /// F14: subtitle offset clamp, +/-30 s.
        public static long ClampOffsetMs(long offsetMs) => Math.Max(-30000, Math.Min(30000, offsetMs));
    }
}
