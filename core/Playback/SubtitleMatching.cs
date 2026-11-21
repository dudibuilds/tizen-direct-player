using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Ru7100.Core.Addons;

namespace Ru7100.Core.Playback
{
    /// The release a file or subtitle was made for, read from its name: "His.Dark.Materials.S01E01.1080p.BluRay.x265-RARBG".
    public sealed class ReleaseInfo
    {
        private static readonly Regex GroupPattern = new Regex(@"-(?<g>[A-Za-z0-9]+)(?:\[[^\]]*\])?$", RegexOptions.Compiled);
        private static readonly Regex ResolutionPattern = new Regex(@"(?<![0-9])(2160|1080|720|576|480)[pi](?![a-z])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Extension = new Regex(@"\.(mkv|mp4|avi|m4v|ts|srt|sub|vtt|ass)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        /// Words after the group that are not part of it (subtitle file names add them: "-SHORTBREHD-HI", "-MTB.Hi").
        private static readonly HashSet<string> NotGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "hi", "sdh", "cc", "forced", "eng", "en", "x264", "x265", "h264", "h265", "hevc", "dl", "web" };

        /// The release group ("RARBG", "NTb"), or null.
        public string Group { get; set; }
        /// bluray, web, hdtv, dvd, or null.
        public string Source { get; set; }
        public int Resolution { get; set; }

        public static ReleaseInfo Parse(string name)
        {
            var info = new ReleaseInfo();
            if (string.IsNullOrWhiteSpace(name))
                return info;
            var text = Extension.Replace(name.Trim(), "");
            text = Regex.Replace(text, @"[.\-_](HI|SDH|CC|Forced|eng)$", "", RegexOptions.IgnoreCase);
            var match = GroupPattern.Match(text);
            if (match.Success && !NotGroups.Contains(match.Groups["g"].Value))
                info.Group = match.Groups["g"].Value;
            var lower = text.ToLowerInvariant();
            if (Regex.IsMatch(lower, @"blu-?ray|bdrip|brrip|bdremux|\bbd\b|\.bd\."))
                info.Source = "bluray";
            else if (Regex.IsMatch(lower, @"web-?dl|webrip|\bweb\b|\.web\.|amzn|nf\.web|hulu|dsnp|hmax|atvp"))
                info.Source = "web";
            else if (Regex.IsMatch(lower, @"hdtv|pdtv|dsr|\btv\b"))
                info.Source = "hdtv";
            else if (Regex.IsMatch(lower, @"dvdrip|dvd"))
                info.Source = "dvd";
            var res = ResolutionPattern.Match(text);
            if (res.Success)
                info.Resolution = int.Parse(res.Groups[1].Value, CultureInfo.InvariantCulture);
            return info;
        }

        /// The key a subtitle timing fix is remembered under: the same group and source cut the same way.
        public string Key => (Group ?? "?").ToLowerInvariant() + "|" + (Source ?? "?");
    }

    public static class SubtitleMatching
    {
        /// How well `subtitle` fits the file `fileName` (hash: the file's OpenSubtitles hash, or null).
        public static SubtitleMatch Score(string fileName, string hash, SubtitleCandidate subtitle)
        {
            if (subtitle == null)
                return SubtitleMatch.None;
            if (subtitle.HashMatch || (hash != null && string.Equals(subtitle.MovieHash, hash, StringComparison.OrdinalIgnoreCase)))
                return SubtitleMatch.Hash;
            var file = ReleaseInfo.Parse(fileName);
            var sub = ReleaseInfo.Parse(subtitle.Release);
            if (sub.Source == null && subtitle.ReleaseFormat != null)
                sub.Source = ReleaseInfo.Parse("x." + subtitle.ReleaseFormat).Source;
            var group = file.Group ?? "";
            var subGroup = sub.Group ?? subtitle.ReleaseGroup ?? "";
            var sameGroup = group.Length > 0 && string.Equals(group, subGroup, StringComparison.OrdinalIgnoreCase);
            var sameSource = file.Source != null && file.Source == sub.Source;
            if (sameGroup && sameSource)
                return SubtitleMatch.Release;
            if (sameGroup)
                return SubtitleMatch.Group;
            if (sameSource)
                return SubtitleMatch.Source;
            return SubtitleMatch.None;
        }

        /// Preferred languages first (as SubtitleSelection.Order), then the best match to the file, then add-on order.
        public static IReadOnlyList<SubtitleCandidate> Order(IEnumerable<SubtitleCandidate> subtitles, bool anime)
        {
            var preferred = SubtitleSelection.PreferredLanguages(anime).ToList();
            return (subtitles ?? Enumerable.Empty<SubtitleCandidate>())
                .Where(item => item != null && !string.IsNullOrEmpty(item.Url))
                .Select((item, index) => (item, index))
                .OrderBy(x =>
                {
                    var at = preferred.IndexOf(SubtitleSelection.NormalizeLanguage(x.item.Language));
                    return at < 0 ? int.MaxValue : at;
                })
                .ThenByDescending(x => x.item.Match)
                .ThenBy(x => x.index)
                .Select(x => x.item)
                .ToList();
        }

        /// Scores every subtitle against the file, in place.
        public static void Annotate(IEnumerable<SubtitleCandidate> subtitles, string fileName, string hash)
        {
            foreach (var subtitle in subtitles ?? Enumerable.Empty<SubtitleCandidate>())
                if (subtitle != null)
                    subtitle.Match = Score(fileName, hash, subtitle);
        }

        /// A short tag for the subtitle menu: "matches your file", or the release it was made for ("WEB-DL NTb").
        public static string MenuTag(SubtitleCandidate subtitle)
        {
            if (subtitle == null)
                return null;
            if (subtitle.Match >= SubtitleMatch.Release)
                return "matches your file";
            var info = ReleaseInfo.Parse(subtitle.Release);
            var source = info.Source == "bluray" ? "BluRay" : info.Source == "web" ? "WEB" : info.Source == "hdtv" ? "TV" : info.Source == "dvd" ? "DVD" : null;
            var group = info.Group ?? subtitle.ReleaseGroup;
            var tag = string.Join(" ", new[] { source, group }.Where(s => !string.IsNullOrEmpty(s)));
            return tag.Length == 0 ? null : tag;
        }
    }

    /// The OpenSubtitles "movie hash": the file size plus the 64-bit little-endian words of its first and last 64 KiB,
    /// wrapping. Subtitle add-ons use it (Stremio's videoHash) to find subtitles made for exactly this file.
    public static class OpenSubtitlesHash
    {
        public const int ChunkBytes = 64 * 1024;

        public static string Compute(long size, byte[] head, byte[] tail)
        {
            if (size < ChunkBytes || head == null || tail == null || head.Length < ChunkBytes || tail.Length < ChunkBytes)
                return null;
            // Little-endian words (BitConverter on the TV's ARM and the Mac alike).
            unchecked
            {
                var hash = (ulong)size;
                for (var i = 0; i < ChunkBytes; i += 8)
                {
                    hash += BitConverter.ToUInt64(head, i);
                    hash += BitConverter.ToUInt64(tail, i);
                }
                return hash.ToString("x16", CultureInfo.InvariantCulture);
            }
        }
    }
}
