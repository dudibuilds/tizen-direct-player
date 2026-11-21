using System;
using System.Collections.Generic;
using System.Linq;

namespace Ru7100.Core.App
{
    /// How the previous run of the app ended, from the lifecycle lines it wrote as it went ("start", "background",
    /// "foreground", "back: exit", "low memory: …", "terminate", "crash"). The TV's app manager can end a backgrounded
    /// or memory-starved app without any callback, and a native crash leaves no managed trace, so the absence of a
    /// "terminate" line is itself the evidence.
    public static class ExitHistory
    {
        public sealed class Summary
        {
            /// closed | back | killed_in_background | ended_on_screen | crash | unknown
            public string Kind { get; set; }
            public string Text { get; set; }
            public string StartedAt { get; set; }
            public string EndedAt { get; set; }
            public IReadOnlyList<string> Events { get; set; } = Array.Empty<string>();
        }

        /// Lines are "&lt;ISO time&gt; &lt;event&gt;", oldest first, for one run.
        public static Summary Summarize(IReadOnlyList<string> lines)
        {
            var events = (lines ?? Array.Empty<string>()).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            var summary = new Summary { Events = events.Skip(Math.Max(0, events.Count - 10)).ToList() };
            if (events.Count == 0)
            {
                summary.Kind = "unknown";
                summary.Text = "No record of the previous run.";
                return summary;
            }
            string Event(string line) { var at = line.IndexOf(' '); return at < 0 ? line : line.Substring(at + 1); }
            string Time(string line) { var at = line.IndexOf(' '); return at < 0 ? null : line.Substring(0, at); }
            summary.StartedAt = Time(events[0]);
            summary.EndedAt = Time(events[events.Count - 1]);
            var last = Event(events[events.Count - 1]);
            var lowMemory = events.Any(l => Event(l).StartsWith("low memory", StringComparison.Ordinal));
            var memoryNote = lowMemory ? " The TV had reported low memory." : "";
            if (events.Any(l => Event(l).StartsWith("crash", StringComparison.Ordinal)))
            {
                summary.Kind = "crash";
                summary.Text = "The app crashed (see lastCrash).";
            }
            else if (last == "terminate")
            {
                var back = events.Any(l => Event(l).StartsWith("back: exit", StringComparison.Ordinal) || Event(l).StartsWith("back: to background", StringComparison.Ordinal));
                summary.Kind = back ? "back" : "closed";
                summary.Text = back ? "Closed with Back on Home." : "Closed normally (by the TV or another app taking over).";
            }
            else if (last.StartsWith("background", StringComparison.Ordinal) || last.StartsWith("back: to background", StringComparison.Ordinal))
            {
                summary.Kind = "killed_in_background";
                var byBack = last.StartsWith("back:", StringComparison.Ordinal) ||
                    events.Count > 1 && Event(events[events.Count - 2]).StartsWith("back: to background", StringComparison.Ordinal);
                summary.Text = (byBack ? "Sent to the background with Back on Home, then" : "Went to the background, then") +
                    " ended by the TV without notice (usually to free memory)." + memoryNote;
            }
            else
            {
                summary.Kind = "ended_on_screen";
                summary.Text = "Ended while on screen with no crash record and no notice: a native crash or the TV ending it." + memoryNote;
            }
            return summary;
        }
    }
}
