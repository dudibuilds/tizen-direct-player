using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ru7100.Core.Playback
{
    /// The viewer's preferred languages and the languages the app can name. Codes are ISO 639-2 ("eng", "spa");
    /// tags, two-letter codes and English names normalize to them.
    public static class Languages
    {
        /// Audio language for titles that are not anime (anime uses Japanese audio).
        public static string Audio { get; set; } = "eng";

        /// Preferred subtitle language for titles that are not anime. "eng" means English only; another language is
        /// tried first, with English as the fallback. Anime subtitles are English.
        public static string Subtitles { get; set; } = "eng";

        public const string AnimeAudio = "jpn";
        public const string AnimeSubtitles = "eng";

        /// True when the viewer prefers subtitles in a language other than English.
        public static bool HasPreferredSubtitles => Normalize(Subtitles) != "eng";

        /// Subtitle languages in preference order.
        public static IReadOnlyList<string> SubtitleOrder(bool anime) =>
            anime || !HasPreferredSubtitles ? new[] { "eng" } : new[] { Normalize(Subtitles), "eng" };

        private sealed class Language
        {
            public string Code, Short, Name;
            public string[] Aliases;
            /// Extra words that mark subtitles in this language in a release name (beyond "code subs" / "name subs").
            public string SubtitleWords;
            /// The language's own name, as it appears in track titles.
            public string NativeName;
        }

        private static readonly Language[] Known =
        {
            new Language { Code = "eng", Short = "EN", Name = "English", Aliases = new[] { "en" } },
            new Language { Code = "heb", Short = "HE", Name = "Hebrew", Aliases = new[] { "he", "iw" } },
            new Language { Code = "jpn", Short = "JA", Name = "Japanese", Aliases = new[] { "ja", "jp" }, NativeName = "日本語" },
            new Language { Code = "spa", Short = "ES", Name = "Spanish", Aliases = new[] { "es" } },
            new Language { Code = "fre", Short = "FR", Name = "French", Aliases = new[] { "fr", "fra" } },
            new Language { Code = "ger", Short = "DE", Name = "German", Aliases = new[] { "de", "deu" } },
            new Language { Code = "rus", Short = "RU", Name = "Russian", Aliases = new[] { "ru" } },
            new Language { Code = "ita", Short = "IT", Name = "Italian", Aliases = new[] { "it" } },
            new Language { Code = "por", Short = "PT", Name = "Portuguese", Aliases = new[] { "pt" } },
            new Language { Code = "ara", Short = "AR", Name = "Arabic", Aliases = new[] { "ar" } },
            new Language { Code = "kor", Short = "KO", Name = "Korean", Aliases = new[] { "ko" } },
            new Language { Code = "chi", Short = "ZH", Name = "Chinese", Aliases = new[] { "zh", "zho" } },
            new Language { Code = "tur", Short = "TR", Name = "Turkish", Aliases = new[] { "tr" } },
            new Language { Code = "pol", Short = "PL", Name = "Polish", Aliases = new[] { "pl" } },
            new Language { Code = "dut", Short = "NL", Name = "Dutch", Aliases = new[] { "nl", "nld" } },
            new Language { Code = "ukr", Short = "UK", Name = "Ukrainian", Aliases = new[] { "uk" } },
            new Language { Code = "hin", Short = "HI", Name = "Hindi", Aliases = new[] { "hi" } },
        };

        private static Language Find(string code)
        {
            code = Normalize(code);
            return Known.FirstOrDefault(l => l.Code == code);
        }

        /// A tag ("en-US", "pt_BR"), two- or three-letter code or English name as a three-letter code; other values
        /// come back lowercased without their region.
        public static string Normalize(string language)
        {
            var value = (language ?? "").Trim().ToLowerInvariant();
            var dash = value.IndexOfAny(new[] { '-', '_' });
            if (dash > 0)
                value = value.Substring(0, dash);
            foreach (var l in Known)
                if (value == l.Code || value == l.Name.ToLowerInvariant() || Array.IndexOf(l.Aliases, value) >= 0)
                    return l.Code;
            return value;
        }

        /// The language a track title names ("English SDH", "日本語"), or null.
        public static string FromTitle(string title)
        {
            if (string.IsNullOrEmpty(title))
                return null;
            foreach (var l in Known)
            {
                var pattern = @"\b(?:" + l.Code + "|" + l.Name + @")\b" + (l.NativeName != null ? "|" + Regex.Escape(l.NativeName) : "");
                if (Regex.IsMatch(title, pattern, RegexOptions.IgnoreCase))
                    return l.Code;
            }
            return null;
        }

        /// English name ("Spanish"); the code itself when unknown.
        public static string Name(string code) => Find(code)?.Name ?? code;

        /// Two-letter badge ("ES"); the code upper-cased when unknown.
        public static string Short(string code) => Find(code)?.Short ?? (code ?? "").ToUpperInvariant();

        /// The three-letter code for a two-letter badge ("ES" -> "spa").
        public static string FromShort(string badge) =>
            Known.FirstOrDefault(l => string.Equals(l.Short, badge, StringComparison.OrdinalIgnoreCase))?.Code;

        private static readonly Dictionary<string, Regex> SubtitlePatterns = new Dictionary<string, Regex>();

        /// Matches a release name that promises subtitles in this language ("spa subs", "spanish subtitles", "spasub").
        public static Regex SubtitlePattern(string code)
        {
            code = Normalize(code);
            lock (SubtitlePatterns)
            {
                if (SubtitlePatterns.TryGetValue(code, out var cached))
                    return cached;
                var l = Find(code);
                var name = l?.Name ?? code;
                var pattern = @"\b(?:" + Regex.Escape(code) + "|" + Regex.Escape(name) + @")[ ._-]*(?:subs?|subtitles?)\b|\b" + Regex.Escape(code) + @"sub\b";
                if (l?.SubtitleWords != null)
                    pattern += "|" + l.SubtitleWords;
                var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                SubtitlePatterns[code] = regex;
                return regex;
            }
        }
    }
}
