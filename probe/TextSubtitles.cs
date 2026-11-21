using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ru7100Probe
{
    internal sealed class TextSubtitles
    {
        private static readonly Regex Timing = new Regex(
            @"^(?<start>(?:\d+:)?\d{2}:\d{2}[,.]\d{3})\s+-->\s+(?<end>(?:\d+:)?\d{2}:\d{2}[,.]\d{3})(?:\s|$)");
        private readonly Cue[] cues;
        public string Name { get; }

        public TextSubtitles(string name, string content)
        {
            Name = name;
            var entries = Regex.Split(content.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n'),
                "\n[ \t]*\n");
            var parsed = new List<Cue>();
            foreach (var entry in entries)
            {
                var lines = entry.Split('\n');
                var index = Array.FindIndex(lines, line => Timing.IsMatch(line));
                if (index < 0 || index == lines.Length - 1)
                    continue;
                var match = Timing.Match(lines[index]);
                if (!TryTimestamp(match.Groups["start"].Value, out var start)
                    || !TryTimestamp(match.Groups["end"].Value, out var end) || start >= end)
                    continue;
                var text = string.Join("\n", lines.Skip(index + 1)).Trim();
                if (text.Length > 0)
                    parsed.Add(new Cue(start, end, text));
            }
            cues = parsed.OrderBy(cue => cue.Start).ToArray();
        }

        public string At(long position)
        {
            string active = null;
            foreach (var cue in cues)
            {
                if (cue.Start > position)
                    break;
                if (cue.End > position)
                    active = active == null ? cue.Text : active + "\n" + cue.Text;
            }
            return active ?? "";
        }

        private static bool TryTimestamp(string value, out long milliseconds)
        {
            value = value.Replace(',', '.');
            if (value.Count(character => character == ':') == 1)
                value = "00:" + value;
            if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var time))
            {
                milliseconds = (long)time.TotalMilliseconds;
                return true;
            }
            milliseconds = 0;
            return false;
        }

        private sealed class Cue
        {
            public Cue(long start, long end, string text)
            {
                Start = start;
                End = end;
                Text = text;
            }
            public long Start { get; }
            public long End { get; }
            public string Text { get; }
        }
    }
}
