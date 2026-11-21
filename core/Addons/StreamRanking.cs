using System;
using Ru7100.Core.Playback;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Ru7100.Core.Addons
{
    /// Pure, deterministic source ranking (F2-F10, F15), re-tuned for the RU7100 player.
    /// Regexes ported from stremio-core.mjs use ECMAScript mode so \b, \w and \d stay ASCII as in JavaScript.
    public static class StreamRanking
    {
        public const double MaxStreamSizeGb = 6.5;
        public const int MinHealthySeeders = 50;

        // Copied from probe/StremioAddonClient.cs DefaultTrackers (same list as stremio-core.mjs).
        public static readonly IReadOnlyList<string> DefaultTrackers = new[]
        {
            "udp://tracker.opentrackr.org:1337/announce",
            "udp://open.stealth.si:80/announce",
            "udp://tracker.torrent.eu.org:451/announce",
            "udp://exodus.desync.com:6969/announce",
            "https://tracker.tamersunion.org:443/announce",
        };

        private const RegexOptions Js = RegexOptions.ECMAScript | RegexOptions.IgnoreCase;
        private static Regex R(string pattern) => new Regex(pattern, Js);

        private static readonly Regex Cam = R(@"\b(cam|hdcam|telesync|telecine|dvdscr)\b|web[ ._-]*screener|\bscreener\b");
        private static readonly Regex SeedersEmoji = new Regex("\U0001F464\\s*(\\d+)", RegexOptions.CultureInvariant);
        private static readonly Regex SeedersWord = R(@"(?:seeders?|seeds?)\D{0,4}(\d+)");
        private static readonly Regex Size = new Regex("(?:\U0001F4BE\\s*)?([0-9]+(?:\\.[0-9]+)?)\\s*(GB|MB)(?![A-Za-z0-9_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Q4k = R(@"2160p|\b4k\b");
        private static readonly Regex Q1080 = R("1080p");
        private static readonly Regex Q720 = R("720p");
        private static readonly Regex WebDl = R(@"web[- .]?dl");
        private static readonly Regex BluRay = R(@"blu[- .]?ray|bdrip");
        private static readonly Regex H264 = R(@"\bh[ .]?264|x264|\bavc\b");
        private static readonly Regex Hevc = R(@"\bh[ .]?265|x265|\bhevc\b");
        private static readonly Regex Av1 = R(@"\bav1\b");
        private static readonly Regex TenBit = R(@"\b10[ ._-]?bits?\b|\bhi10p?\b");
        private static readonly Regex DolbyVision = R(@"\b(?:dolby[ ._-]?vision|dovi|dv)\b");
        private static readonly Regex Hdr10 = R(@"\bhdr(?:10)?\b");
        private static readonly Regex Aac = R(@"\baac");
        private static readonly Regex Eac3 = R(@"\be-?ac-?3|\bddp|\bdd\+");
        private static readonly Regex Ac3 = R(@"\bac-?3|\bdd[ ._]?[257][ ._]?[01]\b");
        private static readonly Regex Flac = R(@"\bflac");
        private static readonly Regex Opus = R(@"\bopus");
        private static readonly Regex Dts = R(@"\bdts");
        private static readonly Regex TrueHd = R(@"\btrue[ ._-]?hd|\btruhd");
        private static readonly Regex FlacOpus = R(@"\b(flac|opus)\b");
        private static readonly Regex Upscale = R("upscale");
        // Season packs, judged on the torrent's name (the first line of Torrentio's title; a pack's file name follows on
        // the next line). An episode marker means a single episode; a season marker without one means a pack.
        private static readonly Regex EpisodeMarker = R(@"\bs\d{1,2}[ ._-]?e\d{1,3}\b|\b\d{1,2}x\d{2,3}\b|\bep(?:isode)?[ ._-]?\d{1,3}\b");
        private static readonly Regex SeasonMarker = new Regex(@"\bs\d{1,2}\b|\bs\d{1,2}[ ._-]*-[ ._-]*s?\d{1,2}\b|\bseasons?\b|\bcomplete\b|сезон|серии", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        /// Score cost of a season pack versus a single-episode torrent. It only orders sources inside the same health band
        /// and language tier, where seeders count log2 x 12: a pack must have roughly twice the seeders to rank first.
        /// Single episodes start faster: their peers all hold this episode (in a pack many peers fetch other files), the
        /// metadata is smaller, and the swarm's pieces are this file's.
        public const double SeasonPackPenalty = 12;

        // ---- F6/F7 language detection ----
        private static readonly Regex Flag = new Regex("(?:\uD83C[\uDDE6-\uDDFF]){2}", RegexOptions.CultureInvariant);
        private const string FlagGb = "\U0001F1EC\U0001F1E7";
        private const string FlagUs = "\U0001F1FA\U0001F1F8";
        private static readonly Regex MultiAudio = R(@"\b(?:dual|multi)[ ._-]*(?:audio|dub)\b|\bdual[ ._-]*lang(?:uage)?\b");
        private static readonly Regex JapaneseAudio = R(@"\b(?:jpn|japanese|jap)[ ._-]*(?:audio|dub)?\b|\bja[ ._-]*audio\b");
        private static readonly Regex JapaneseSubs = R(@"\b(?:jpn|japanese|jap)[ ._-]*(?:subs?|subtitles?)\b");
        private static readonly Regex EnglishSubsAnime = R(@"\b(?:eng|english)[ ._-]*(?:subs?|subtitles?)\b");
        private static readonly Regex MultiSubs = R(@"\bmulti[ ._-]*(?:subs?|subtitles?)\b");
        private static readonly Regex EnglishDub = R(@"\b(?:eng|english)[ ._-]*(?:audio|dub(?:bed)?)\b");
        private static readonly Regex EnglishLabel = R(@"\b(?:eng|english)[ ._-]*(?:audio|dub(?:bed)?|voice|ddp(?:[257]\.1)?|e-?ac-?3(?:[257]\.1)?|ac-?3(?:[257]\.1)?|aac(?:[257]\.1)?|truehd(?:[257]\.1)?|dts(?:[ ._-]*hd)?|[257]\.1|2\.0)\b");
        private static readonly Regex EnglishTag = R(@"\beng\b(?![ ._-]*(?:subs?|subtitles?)\b)");
        private static readonly Regex ForeignTag = R(@"(?:^|[ ._\[(-])(?:rus|ukr|ita|ger|fre|spa|pol|tur|hin|kor|chi)(?=$|[ ._\])-])(?![ ._-]*(?:subs?|subtitles?)\b)");
        private static readonly Regex EnglishSubs = R(@"\b(?:eng|english)[ ._-]*(?:subs?|subtitles?)\b|\bengsub\b");
        private static readonly Regex ForeignAudio = R(@"\b(?:jpn|japanese|kor|korean|spa|spanish|latino|fre|french|ita|italian|ger|german|rus|russian|hin|hindi|ara|arabic|tur|turkish|por|portuguese|chi|chinese|mandarin|cantonese)[ ._-]*(?:audio|dub(?:bed)?|voice)\b");

        public static double EffectiveMaxSizeGb(double? requested) =>
            requested.HasValue && requested.Value > 0 ? Math.Min(requested.Value, MaxStreamSizeGb) : MaxStreamSizeGb;

        /// Full pipeline: dedupe (F2), analyze + hard filters (F3, TV), health bands (F8), sort (F9),
        /// quality filter, size cap (F10), excluded identities. Deterministic for a given input order.
        public static IReadOnlyList<StreamAnalysis> RankAnalyses(IEnumerable<AddonStreamItem> streams, RankOptions options)
        {
            options = options ?? new RankOptions();
            var anime = options.Anime;
            var scored = Deduplicate(streams)
                .Select(stream => Analyze(stream, options.Episodic, anime, options.Device))
                .Where(entry => entry.RejectReason == null)
                .ToList();
            // Seeder counts are stale snapshots, so "healthy" is relative to the best swarm on offer.
            var bestSeeders = 0;
            foreach (var entry in scored)
                if (!string.IsNullOrEmpty(entry.Stream.InfoHash) && entry.SeedersKnown && entry.Seeders > bestSeeders)
                    bestSeeders = entry.Seeders;
            var healthyFloor = Math.Max(MinHealthySeeders, bestSeeders * 0.1);
            foreach (var entry in scored) entry.HealthBand = HealthBand(entry, anime, healthyFloor);

            // For a title made in English, a release that names no language is English already. "Multi audio" alone
            // (tier 1: English plus a dub, nothing about subtitles) then adds nothing, and must not outrank a better
            // resolution or a healthier swarm; it sorts with the unnamed releases and the score decides.
            int SortTier(StreamAnalysis entry)
            {
                var tier = entry.LanguageTier(anime);
                return !anime && options.EnglishOriginal && tier == 1 ? 0 : tier;
            }
            IEnumerable<StreamAnalysis> ranked = scored
                .OrderByDescending(entry => entry.LanguageTier(anime) < 0 ? 0 : 1)
                .ThenByDescending(entry => entry.HealthBand)
                .ThenByDescending(SortTier)
                .ThenByDescending(entry => entry.Score);
            var quality = string.IsNullOrEmpty(options.Quality) ? "auto" : options.Quality.ToLowerInvariant();
            if (quality != "auto") ranked = ranked.Where(entry => entry.Quality == quality);
            var maxBytes = EffectiveMaxSizeGb(options.MaxSizeGb) * 1e9;
            // Unknown size passes; the player re-checks once the real length is known.
            ranked = ranked.Where(entry => !entry.SizeBytes.HasValue || entry.SizeBytes.Value <= maxBytes);
            if (options.ExcludeIdentities != null && options.ExcludeIdentities.Count > 0)
                ranked = ranked.Where(entry => !options.ExcludeIdentities.Contains(entry.Stream.Identity)
                    && !options.ExcludeIdentities.Contains(ToCandidate(entry, anime).Identity));
            return ranked.ToList();
        }

        public static IReadOnlyList<StreamCandidate> Rank(IEnumerable<AddonStreamItem> streams, RankOptions options)
        {
            var anime = options?.Anime ?? false;
            return RankAnalyses(streams, options).Select(entry => ToCandidate(entry, anime)).ToList();
        }

        /// F2: first occurrence of each identity wins; streams without identity are dropped.
        public static IReadOnlyList<AddonStreamItem> Deduplicate(IEnumerable<AddonStreamItem> streams)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<AddonStreamItem>();
            foreach (var stream in streams ?? Enumerable.Empty<AddonStreamItem>())
            {
                var identity = stream?.Identity;
                if (identity == null || !seen.Add(identity)) continue;
                list.Add(stream);
            }
            return list;
        }

        /// F3 + TV hard filters, F4 parsing and F5 score for one stream.
        public static StreamAnalysis Analyze(AddonStreamItem stream, bool episodic, bool anime, DeviceCaps device = null)
        {
            device = device ?? DeviceCaps.Ru7100;
            var result = new StreamAnalysis { Stream = stream };
            var text = stream.SearchText;
            var hasHash = IsInfoHash(stream.InfoHash);
            var hasUrl = AddonUrls.IsWebUrl(stream.Url);

            result.VideoCodec = H264.IsMatch(text) ? "h264" : Hevc.IsMatch(text) ? "hevc" : Av1.IsMatch(text) ? "av1" : "unknown";
            result.AudioCodec = Aac.IsMatch(text) ? "aac" : Eac3.IsMatch(text) ? "eac3" : Ac3.IsMatch(text) ? "ac3"
                : Flac.IsMatch(text) ? "flac" : Opus.IsMatch(text) ? "opus" : Dts.IsMatch(text) ? "dts" : TrueHd.IsMatch(text) ? "truehd" : "unknown";

            result.RejectReason = HardFilter(stream, text, hasHash, hasUrl, device);
            if (result.RejectReason != null) return result;

            var seedersMatch = SeedersEmoji.Match(text);
            if (!seedersMatch.Success) seedersMatch = SeedersWord.Match(text);
            result.SeedersKnown = seedersMatch.Success;
            result.Seeders = seedersMatch.Success && int.TryParse(seedersMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seeders) ? seeders : 0;
            result.SizeBytes = ParseSize(text) ?? (stream.VideoSize > 0 ? stream.VideoSize : null);
            result.Quality = Q4k.IsMatch(text) ? "4k" : Q1080.IsMatch(text) ? "1080p" : Q720.IsMatch(text) ? "720p" : "unknown";

            var score = Math.Log(result.Seeders + 1, 2) * 12;
            if (result.Seeders >= 100) result.Reasons.Add(result.Seeders + " seeders");
            switch (result.Quality)
            {
                case "1080p": score += 30; result.Reasons.Add("reliable 1080p"); break;
                case "4k": score += result.Seeders >= 20 && (!result.SizeBytes.HasValue || result.SizeBytes.Value <= 15e9) ? 27 : 8; break;
                case "720p": score += 12; break;
                default: score -= 18; break;
            }
            if (WebDl.IsMatch(text)) { score += 13; result.Reasons.Add("WEB-DL"); }
            else if (BluRay.IsMatch(text)) { score += 10; result.Reasons.Add("BluRay"); }

            // TV re-tune: HEVC (incl. HDR10) is hardware-decoded, so H.264 only gets a small preference.
            if (result.VideoCodec == "h264")
            {
                score += 10;
                result.Reasons.Add("H.264");
                if (TenBit.IsMatch(text)) { score -= 25; result.Reasons.Add("10-bit H.264 (no hardware decode)"); }
            }
            else if (result.VideoCodec == "hevc") { score += 7; result.Reasons.Add("HEVC"); }

            if (Aac.IsMatch(text)) { score += 8; result.Reasons.Add("AAC audio"); }
            else if (Eac3.IsMatch(text) || Ac3.IsMatch(text)) { score += 6; result.Reasons.Add("Dolby Digital audio"); }
            // Dolby Vision with an HDR10 base layer plays as HDR10; prefer plain HDR10/SDR slightly.
            if (DolbyVision.IsMatch(text)) { score -= 10; result.Reasons.Add("Dolby Vision (HDR10 fallback)"); }
            if (FlacOpus.IsMatch(text)) score -= 12;
            if (Upscale.IsMatch(text)) score -= 16;
            if (episodic && result.SizeBytes.HasValue && result.SizeBytes.Value > 0)
            {
                if (result.SizeBytes.Value >= 3e8 && result.SizeBytes.Value <= 4e9) score += 8;
                else if (result.SizeBytes.Value > 8e9) score -= 20;
            }

            if (anime)
            {
                var languages = DetectAnimeLanguages(text);
                result.AnimeLanguageTier = languages.Tier;
                result.JapaneseAudio = languages.JapaneseAudio;
                result.AnimeOriginalAudio = languages.JapaneseAudio || languages.MultiAudio || languages.EnglishSubtitles;
                if (languages.JapaneseAudio) { score += 28; result.Reasons.Add("Japanese audio"); }
                else if (languages.MultiAudio) { score += 14; result.Reasons.Add("dual/multi audio"); }
                if (languages.EnglishSubtitles) { score += 24; result.Reasons.Add("English subtitles"); }
                else if (languages.MultiSubtitles) { score += 12; result.Reasons.Add("multi-subs"); }
                if (languages.JapaneseAudio && languages.EnglishSubtitles) { score += 30; result.Reasons.Add("preferred anime language combination"); }
                if (languages.EnglishDubOnly) { score -= 30; result.Reasons.Add("English dub only"); }
            }
            else
            {
                var languages = DetectGeneralLanguages(text);
                result.GeneralLanguageTier = languages.Tier;
                if (languages.EnglishAudio) result.Reasons.Add("English audio");
                else if (languages.MultiAudio) result.Reasons.Add("dual/multi audio");
                if (languages.PreferredSubtitles) result.Reasons.Add(Languages.Name(Languages.Subtitles) + " subtitles");
                else if (languages.EnglishSubtitles) result.Reasons.Add("English subtitles");
                else if (languages.MultiSubtitles) result.Reasons.Add("multi-subs");
                if (languages.ForeignAudioOnly) result.Reasons.Add("no indicated English audio");
            }
            if (!stream.FileIdx.HasValue && hasHash) score -= 3;
            if (episodic && hasHash && IsSeasonPack(stream))
            {
                result.SeasonPack = true;
                score -= SeasonPackPenalty;
                result.Reasons.Add("season pack");
            }
            result.Score = score;
            return result;
        }

        /// True when the torrent's name (first line of the title or description) marks a season or multi-season pack.
        public static bool IsSeasonPack(AddonStreamItem stream)
        {
            var text = stream?.Title ?? stream?.Description ?? "";
            var newline = text.IndexOf('\n');
            var name = newline >= 0 ? text.Substring(0, newline) : text;
            return !EpisodeMarker.IsMatch(name) && SeasonMarker.IsMatch(name);
        }

        /// Returns why the stream can never play on the device, or null.
        public static string HardFilter(AddonStreamItem stream, string text, bool hasHash, bool hasUrl, DeviceCaps device)
        {
            if (!string.IsNullOrEmpty(stream.ExternalUrl)) return "external link";
            if (!hasHash && !hasUrl) return "no playable url or info hash";
            if (!hasHash && stream.HasProxyHeaders) return "needs proxy headers";
            if (Cam.IsMatch(text)) return "cam/screener";
            if (device.RejectAv1 && Av1.IsMatch(text)) return "AV1 video";
            if (device.RejectDolbyVisionWithoutHdr10 && DolbyVision.IsMatch(text) && !Hdr10.IsMatch(text)) return "Dolby Vision without HDR10";
            if (device.RejectDts && Dts.IsMatch(text)) return "DTS audio";
            if (device.RejectTrueHd && TrueHd.IsMatch(text)) return "TrueHD audio";
            if (device.SupportedVideoCodecs != null)
            {
                var label = H264.IsMatch(text) ? "h264" : Hevc.IsMatch(text) ? "hevc" : Av1.IsMatch(text) ? "av1" : null;
                if (label != null && !device.SupportedVideoCodecs.Contains(label)) return "unsupported video codec " + label;
            }
            return null;
        }

        /// F8: 2 healthy (direct URL or seeders >= floor), 1 unknown, 0 weak; anime without original audio is 0.
        private static int HealthBand(StreamAnalysis entry, bool anime, double healthyFloor)
        {
            if (anime && !entry.AnimeOriginalAudio) return 0;
            if (string.IsNullOrEmpty(entry.Stream.InfoHash)) return 2;
            if (!entry.SeedersKnown) return 1;
            return entry.Seeders >= healthyFloor ? 2 : 0;
        }

        public static StreamCandidate ToCandidate(StreamAnalysis entry, bool anime)
        {
            var stream = entry.Stream;
            var isTorrent = IsInfoHash(stream.InfoHash);
            return new StreamCandidate
            {
                AddonId = stream.AddonId,
                Name = stream.Name,
                Title = stream.Title ?? stream.Description,
                InfoHash = isTorrent ? stream.InfoHash.ToLowerInvariant() : null,
                FileIndex = isTorrent ? stream.FileIdx : null,
                Url = isTorrent ? null : stream.Url,
                Trackers = isTorrent ? TrackersFor(stream) : Array.Empty<string>(),
                Filename = stream.Filename,
                SizeBytes = entry.SizeBytes,
                Seeders = entry.SeedersKnown ? entry.Seeders : (int?)null,
                Quality = entry.Quality,
                VideoCodec = entry.VideoCodec,
                AudioCodec = entry.AudioCodec,
                LanguageTier = entry.LanguageTier(anime),
                HealthBand = entry.HealthBand,
                Score = Math.Round(entry.Score * 10) / 10,
            };
        }

        /// The stream's `tracker:` sources (prefix removed); the default trackers when it lists none.
        public static IReadOnlyList<string> TrackersFor(AddonStreamItem stream)
        {
            var list = new List<string>();
            foreach (var source in stream.Sources ?? Array.Empty<string>())
            {
                if (source == null || !source.StartsWith("tracker:", StringComparison.OrdinalIgnoreCase)) continue;
                var tracker = source.Substring("tracker:".Length);
                if (!Uri.TryCreate(tracker, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)) continue;
                if (uri.Scheme != "udp" && uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) continue;
                if (!list.Contains(tracker)) list.Add(tracker);
            }
            return list.Count > 0 ? list : new List<string>(DefaultTrackers);
        }

        public static bool IsInfoHash(string value)
        {
            if (value == null || value.Length != 40) return false;
            foreach (var c in value)
                if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        /// F4 size: first "N.N GB|MB" (decimal units).
        public static long? ParseSize(string text)
        {
            var match = Size.Match(text ?? "");
            if (!match.Success) return null;
            var value = double.Parse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            var unit = string.Equals(match.Groups[2].Value, "GB", StringComparison.OrdinalIgnoreCase) ? 1e9 : 1e6;
            var bytes = (long)Math.Round(value * unit);
            return bytes > 0 ? bytes : (long?)null;
        }

        public sealed class GeneralLanguages
        {
            public bool EnglishAudio, MultiAudio, PreferredSubtitles, EnglishSubtitles, MultiSubtitles, ForeignAudioOnly;
            public int Tier;
        }

        public sealed class AnimeLanguages
        {
            public bool JapaneseAudio, MultiAudio, EnglishSubtitles, MultiSubtitles, EnglishDubOnly;
            public int Tier;
        }

        /// F6 general tiers (7 English audio + subtitles in the preferred language ... -1 foreign audio only).
        public static GeneralLanguages DetectGeneralLanguages(string text)
        {
            text = text ?? "";
            var flagEnglish = false;
            var flagForeign = false;
            foreach (Match flag in Flag.Matches(text))
            {
                if (flag.Value == FlagGb || flag.Value == FlagUs) flagEnglish = true;
                else flagForeign = true;
            }
            var englishPresent = EnglishLabel.IsMatch(text) || EnglishTag.IsMatch(text) || flagEnglish;
            var foreignPresent = flagForeign || ForeignTag.IsMatch(text);
            var multiAudio = MultiAudio.IsMatch(text) || (englishPresent && foreignPresent);
            var result = new GeneralLanguages
            {
                MultiAudio = multiAudio,
                EnglishAudio = englishPresent && !foreignPresent,
                PreferredSubtitles = Languages.HasPreferredSubtitles && Languages.SubtitlePattern(Languages.Subtitles).IsMatch(text),
                EnglishSubtitles = EnglishSubs.IsMatch(text),
                MultiSubtitles = MultiSubs.IsMatch(text),
            };
            result.ForeignAudioOnly = (ForeignAudio.IsMatch(text) || foreignPresent) && !englishPresent && !multiAudio;
            if (result.EnglishAudio && result.PreferredSubtitles) result.Tier = 7;
            else if (result.EnglishAudio && result.EnglishSubtitles) result.Tier = 6;
            else if (result.EnglishAudio && result.MultiSubtitles) result.Tier = 5;
            else if (result.EnglishAudio) result.Tier = 4;
            else if (multiAudio && result.PreferredSubtitles) result.Tier = 3;
            else if (multiAudio && result.EnglishSubtitles) result.Tier = 2;
            else if (multiAudio) result.Tier = 1;
            else if (result.ForeignAudioOnly) result.Tier = -1;
            return result;
        }

        /// F7 anime tiers (5 Japanese audio + English subs ... -1 English dub only).
        public static AnimeLanguages DetectAnimeLanguages(string text)
        {
            text = text ?? "";
            var result = new AnimeLanguages
            {
                MultiAudio = MultiAudio.IsMatch(text),
                JapaneseAudio = JapaneseAudio.IsMatch(text) && !JapaneseSubs.IsMatch(text),
                EnglishSubtitles = EnglishSubsAnime.IsMatch(text),
                MultiSubtitles = MultiSubs.IsMatch(text),
            };
            result.EnglishDubOnly = EnglishDub.IsMatch(text) && !result.JapaneseAudio && !result.MultiAudio;
            if (result.JapaneseAudio && result.EnglishSubtitles) result.Tier = 5;
            else if ((result.JapaneseAudio && result.MultiSubtitles) || (result.MultiAudio && result.EnglishSubtitles)) result.Tier = 4;
            else if (result.MultiAudio && result.MultiSubtitles) result.Tier = 3;
            else if (result.JapaneseAudio || result.EnglishSubtitles) result.Tier = 2;
            else if (result.MultiAudio || result.MultiSubtitles) result.Tier = 1;
            if (result.EnglishDubOnly) result.Tier = -1;
            return result;
        }

        /// Parses an add-on `{streams:[...]}` payload.
        public static IReadOnlyList<AddonStreamItem> ParseStreams(JObject payload, AddonDescriptor addon)
        {
            var list = new List<AddonStreamItem>();
            if (!(payload?["streams"] is JArray streams)) return list;
            foreach (var item in streams.OfType<JObject>())
                list.Add(ParseStream(item, addon));
            return list;
        }

        public static AddonStreamItem ParseStream(JObject item, AddonDescriptor addon)
        {
            var hints = item["behaviorHints"] as JObject;
            var fileIdx = item["fileIdx"];
            int? index = null;
            if (fileIdx != null && fileIdx.Type == JTokenType.Integer) index = (int)(long)fileIdx;
            else if (fileIdx != null && fileIdx.Type == JTokenType.Float)
            {
                var value = (double)fileIdx;
                if (Math.Abs(value - Math.Round(value)) < double.Epsilon) index = (int)value;
            }
            long? videoSize = null;
            var sizeToken = hints?["videoSize"];
            if (sizeToken != null && (sizeToken.Type == JTokenType.Integer || sizeToken.Type == JTokenType.Float)) videoSize = (long)(double)sizeToken;
            var sources = (item["sources"] as JArray)?.Where(token => token.Type == JTokenType.String).Select(token => (string)token).ToList();
            return new AddonStreamItem
            {
                AddonId = addon?.Id,
                AddonName = addon?.Name,
                Name = Str(item["name"]),
                Title = Str(item["title"]),
                Description = Str(item["description"]),
                InfoHash = Str(item["infoHash"]),
                FileIdx = index,
                Url = Str(item["url"]),
                ExternalUrl = Str(item["externalUrl"]),
                Sources = (IReadOnlyList<string>)sources ?? Array.Empty<string>(),
                Filename = Str(hints?["filename"]),
                VideoSize = videoSize,
                HasProxyHeaders = hints?["proxyHeaders"] is JObject,
            };
        }

        private static string Str(JToken token) => token != null && token.Type == JTokenType.String ? (string)token : null;
    }
}
