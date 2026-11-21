using System;
using Ru7100.Core.Playback;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Ru7100.Core.Ui
{
    public static class UiFormat
    {
        public static string Clock(long ms)
        {
            if (ms < 0)
                ms = 0;
            var t = TimeSpan.FromMilliseconds(ms);
            return t.TotalHours >= 1
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", t.Minutes, t.Seconds);
        }

        public static string Episode(int season, int episode) =>
            string.Format(CultureInfo.InvariantCulture, "S{0}E{1}", season, episode);

        public static string Episode(VideoInfo video) => video == null ? "" : Episode(video.Season, video.Episode);

        public static string Size(long? bytes)
        {
            if (bytes == null || bytes <= 0)
                return null;
            return bytes >= 1_000_000_000
                ? (bytes.Value / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
                : (bytes.Value / 1e6).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        public static string Offset(int ms) =>
            (ms / 1000.0).ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture) + " s";

        public static bool IsReleased(VideoInfo video, DateTimeOffset now) => video.Released == null || video.Released <= now;

        /// Parity F11, without the stream-based check for ambiguous animation.
        public static bool IsAnime(MetaDetail meta)
        {
            if (meta == null)
                return false;
            var genres = meta.Genres ?? Array.Empty<string>();
            if (genres.Any(g => string.Equals(g, "Anime", StringComparison.OrdinalIgnoreCase)))
                return true;
            return genres.Any(g => string.Equals(g, "Animation", StringComparison.OrdinalIgnoreCase))
                && (meta.Country ?? "").IndexOf("Japan", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string LanguageTier(int tier, bool anime)
        {
            if (anime)
            {
                switch (tier)
                {
                    case 5: return "JP audio · EN subs";
                    case 4: return "JP/multi · EN subs";
                    case 3: return "Multi audio · multi subs";
                    case 2: return "JP audio or EN subs";
                    case 1: return "Multi";
                    case -1: return "English dub";
                    default: return null;
                }
            }
            switch (tier)
            {
                case 7: return "EN audio · " + Languages.Short(Languages.Subtitles) + " subs";
                case 6: return "EN audio · EN subs";
                case 5: return "EN audio · multi subs";
                case 4: return "EN audio";
                case 3: return "Multi audio · " + Languages.Short(Languages.Subtitles) + " subs";
                case 2: return "Multi audio · EN subs";
                case 1: return "Multi audio";
                case -1: return "Foreign audio";
                default: return null;
            }
        }

        public static string LanguageName(string code)
        {
            var normalized = PlayerSettings.Normalize(code);
            return normalized.Length == 0 ? "Unknown" : Languages.Name(normalized) == normalized ? code : Languages.Name(normalized);
        }

        public static string TrackLabel(TrackInfo track)
        {
            if (track == null)
                return "Unavailable";
            var name = LanguageName(track.Language);
            return string.IsNullOrEmpty(track.Label) || track.Label == track.Language ? name : name + " · " + track.Label;
        }

        public static string SubtitleLabel(SubtitleOption option)
        {
            if (option == null)
                return "Off";
            var name = string.IsNullOrEmpty(option.Label) ? LanguageName(option.Language) : option.Label;
            return name + (option.External ? " (external)" : "");
        }

        /// Cinemeta-style background art for an IMDb id (Tile/MetaSummary carry no background field).
        public static string BackgroundFor(string id) =>
            id != null && id.StartsWith("tt", StringComparison.Ordinal) ? "https://images.metahub.space/background/medium/" + id + "/img" : null;

        /// Card-sized background art (480x270) for an IMDb id. Row cards are 464x261, so this is the size they draw at;
        /// the 1920x1080 "medium" file costs the TV sixteen times the pixels to decode for the same card, on the
        /// main loop, every time a card scrolls into view. The billboard keeps BackgroundFor.
        public static string CardArtFor(string id) =>
            id != null && id.StartsWith("tt", StringComparison.Ordinal) ? "https://images.metahub.space/background/small/" + id + "/img" : null;

        /// True for the card-sized art of CardArtFor (not an episode still or an add-on's own image).
        public static bool IsCardArt(string url) => url != null && url.StartsWith("https://images.metahub.space/background/small/", StringComparison.Ordinal);

        /// A poster URL at card size: metahub's 300x450 "small" for cards drawn at 288x432 (its "medium" is 500x750,
        /// nearly three times the pixels to decode). Other hosts are returned unchanged.
        public static string CardPoster(string url)
        {
            if (url == null)
                return null;
            if (url.IndexOf("metahub.space/poster/", StringComparison.Ordinal) < 0)
                return ArtAtWidth(url, PosterCardW);   // TMDB posters: w342 (a focused card is ~305 px wide)
            return url.Replace("/poster/medium/", "/poster/small/").Replace("/poster/large/", "/poster/small/");
        }

        /// Widest a poster card is drawn (focused, grown).
        private const int PosterCardW = (int)(UiScale.PosterW * (1 + UiScale.PosterGrow)) + 1;

        /// Widths the size-addressed image hosts serve: Cinemeta's episode stills (episodes.metahub.space, which
        /// redirects to TMDB at the same width) and TMDB itself.
        private static readonly int[] ServedWidths = { 185, 300, 342, 500, 780 };

        /// An episode still or TMDB image at the smallest served width that still covers a slot `slotW` px wide, so
        /// the TV never decodes a 780 px still for a 128 px thumbnail. Only ever shrinks (a smaller source is kept)
        /// and leaves every other URL unchanged.
        public static string ArtAtWidth(string url, int slotW)
        {
            if (url == null)
                return null;
            int at, end;
            if (url.StartsWith("https://episodes.metahub.space/", StringComparison.Ordinal))
            {
                // https://episodes.metahub.space/<id>/<season>/<episode>/w780.jpg
                at = url.LastIndexOf('/') + 1;
                end = url.IndexOf(".jpg", at, StringComparison.Ordinal);
                if (end < 0 || end != url.Length - 4)
                    return url;
            }
            else if (url.StartsWith("https://image.tmdb.org/t/p/", StringComparison.Ordinal))
            {
                // https://image.tmdb.org/t/p/w780/<file>.jpg
                at = "https://image.tmdb.org/t/p/".Length;
                end = url.IndexOf('/', at);
                if (end < 0)
                    return url;
            }
            else
                return url;
            if (end - at < 2 || url[at] != 'w' || !int.TryParse(url.Substring(at + 1, end - at - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var current))
                return url;
            var wanted = ServedWidths[ServedWidths.Length - 1];
            foreach (var width in ServedWidths)
                if (width >= slotW)
                {
                    wanted = width;
                    break;
                }
            return wanted >= current ? url : url.Substring(0, at) + "w" + wanted.ToString(CultureInfo.InvariantCulture) + url.Substring(end);
        }

        public static string LogoFor(string id) =>
            id != null && id.StartsWith("tt", StringComparison.Ordinal) ? "https://images.metahub.space/logo/medium/" + id + "/img" : null;

        /// Episode still in Cinemeta's image layout (same URL its `videos[].thumbnail` uses).
        public static string EpisodeStill(string id, int season, int episode) =>
            id != null && id.StartsWith("tt", StringComparison.Ordinal) && season > 0 ? "https://episodes.metahub.space/" + id + "/" + season + "/" + episode + "/w780.jpg" : null;

        public static string EpisodeColon(int season, int episode) => string.Format(CultureInfo.InvariantCulture, "S{0}:E{1}", season, episode);

        public static string EpisodeColon(VideoInfo video) => video == null ? "" : EpisodeColon(video.Season, video.Episode);

        /// "26m left", "1h 5m left".
        public static string Remaining(long ms)
        {
            if (ms <= 0) return null;
            var minutes = (int)Math.Ceiling(ms / 60000.0);
            return (minutes >= 60 ? minutes / 60 + "h " + minutes % 60 + "m" : minutes + "m") + " left";
        }

        /// "49 min" -> 49; "1h 30min" -> 90; null when unknown.
        public static int? RuntimeMinutes(string runtime)
        {
            if (string.IsNullOrEmpty(runtime)) return null;
            var match = System.Text.RegularExpressions.Regex.Match(runtime, @"^\s*(?:(\d+)\s*h\w*)?\s*(?:(\d+)\s*m)?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success) return null;
            var total = (match.Groups[1].Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60 : 0) + (match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0);
            return total > 0 ? total : (int?)null;
        }

        /// "Show.Name.S01E01.1080p…GRP.mkv" → "Show.Name.S01E…GRP.mkv" when longer than max.
        public static string MiddleEllipsis(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max || max < 8)
                return text;
            var head = (max - 1) * 3 / 5;
            var tail = max - 1 - head;
            return text.Substring(0, head) + "…" + text.Substring(text.Length - tail);
        }

        /// Line height of wrapped text as a multiple of the font size. One constant for the layout, the browser
        /// preview and the TV painter (EFL's own line height), so a box sized for N lines shows N whole lines.
        public const double LineHeight = 1.45;

        /// Average glyph advance as a fraction of the font size, for estimating where text wraps.
        public const double GlyphWidth = 0.52;

        public static int LineH(int fontSize) => (int)Math.Ceiling(fontSize * LineHeight);

        /// Whole lines of `fontSize` text that fit a box `height` px tall.
        public static int LinesIn(int height, int fontSize) => Math.Max(1, (int)Math.Floor((height + fontSize * 0.2) / (fontSize * LineHeight)));

        /// Cuts text to what fits `maxLines` lines of a box `width` px wide, at a word boundary, ending in "…".
        /// Text that fits comes back unchanged. Painters with their own wrapping still never show a cut line,
        /// because boxes are sized in whole lines (LineH).
        public static string ClampLines(string text, int width, int fontSize, int maxLines)
        {
            text = text ?? "";
            var perLine = Math.Max(8, (int)(width / (fontSize * GlyphWidth)));
            var budget = Math.Max(1, maxLines) * perLine;
            if (text.Length <= budget)
                return text;
            var cut = text.Substring(0, Math.Max(0, budget - 1));
            var space = cut.LastIndexOf(' ');
            if (space > budget / 2)
                cut = cut.Substring(0, space);
            return cut.TrimEnd(' ', ',', '.', ';', ':', '-', '–') + "…";
        }

        public static string Facts(params string[] parts) => string.Join("  ·  ", parts.Where(p => !string.IsNullOrEmpty(p)));

        public static IEnumerable<string> Lines(string text) =>
            (text ?? "").Replace("\r\n", "\n").Split('\n').Where(l => l.Length > 0);
    }
}
