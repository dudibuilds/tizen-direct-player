using Ru7100.Core.App;
using Xunit;

namespace Ru7100.Tests.App
{
    public class ExitHistoryTests
    {
        private static ExitHistory.Summary Of(params string[] events)
        {
            var lines = new string[events.Length];
            for (var i = 0; i < events.Length; i++)
                lines[i] = "2026-11-20T16:2" + i + ":00.0000000Z " + events[i];
            return ExitHistory.Summarize(lines);
        }

        [Fact]
        public void AnAppSentAwayWithBackAndNeverTerminatedWasEndedByTheTv()
        {
            var summary = Of("start pid=1", "background rss=90MB", "foreground", "back: to background", "background rss=95MB");
            Assert.Equal("killed_in_background", summary.Kind);
            Assert.StartsWith("Sent to the background with Back", summary.Text);
            Assert.StartsWith("Went to the background", Of("start pid=1", "background rss=90MB").Text);
            Assert.Equal("2026-11-20T16:20:00.0000000Z", summary.StartedAt);
            Assert.Equal(5, summary.Events.Count);
            Assert.Equal("killed_in_background", Of("start pid=1", "back: to background").Kind);
            Assert.StartsWith("Sent to the background with Back", Of("start pid=1", "back: to background").Text);
        }

        [Fact]
        public void TellsTheOtherEndingsApart()
        {
            Assert.Equal("back", Of("start pid=1", "back: exit (still in front)", "terminate").Kind);
            Assert.Equal("closed", Of("start pid=1", "background rss=80MB", "terminate").Kind);
            Assert.Equal("crash", Of("start pid=1", "crash unhandled (terminating=True pid=1)").Kind);
            var onScreen = Of("start pid=1", "low memory: HardWarning", "foreground");
            Assert.Equal("ended_on_screen", onScreen.Kind);
            Assert.Contains("low memory", onScreen.Text);
            Assert.Equal("unknown", ExitHistory.Summarize(null).Kind);
        }
    }
}
