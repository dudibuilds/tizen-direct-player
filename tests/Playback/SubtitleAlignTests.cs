using System;
using System.Linq;
using Ru7100.Core.Playback;
using Xunit;
using Xunit.Abstractions;
using D = Ru7100.Tests.Playback.SubtitleTimingData;

namespace Ru7100.Tests.Playback
{
    public class SubtitleAlignTests
    {
        private const double PalToFilm = 25 / (24000 / 1001.0);
        private readonly ITestOutputHelper output;

        public SubtitleAlignTests(ITestOutputHelper output) => this.output = output;

        private AlignResult Align(long[] target, long[] reference)
        {
            var result = SubtitleAligner.Align(target, reference);
            output.WriteLine(result.ToString());
            return result;
        }

        [Fact]
        public void AWebSubtitleIsShiftedOntoTheBluRayTimeline()
        {
            // Measured by matching identical English lines: the Amazon version runs about 4 s ahead of the BluRay.
            var result = Align(D.AmazonEnglish, D.BluRayEnglish);
            Assert.True(result.Accepted);
            Assert.InRange(result.Timing.Scale, 0.999, 1.001);
            Assert.InRange(result.Timing.OffsetMs, -4800, -3200);
        }

        [Fact]
        public void ATvRecordingSubtitleIsShiftedTheOtherWay()
        {
            var result = Align(D.TvEnglish, D.BluRayEnglish);
            Assert.True(result.Accepted);
            Assert.InRange(result.Timing.Scale, 0.999, 1.001);
            Assert.InRange(result.Timing.OffsetMs, 1200, 2800);
        }

        [Fact]
        public void ASpanishSubtitleAlignsToAnEnglishReference()
        {
            // Different translations, different line breaks (673 cues against 729): lines still start with the speech.
            var result = Align(D.AmazonSpanish, D.BluRayEnglish);
            Assert.True(result.Accepted);
            Assert.InRange(result.Timing.Scale, 0.999, 1.001);
            Assert.InRange(result.Timing.OffsetMs, -4800, -3200);
        }

        [Fact]
        public void AFileSlowedFrom25To23976FpsIsFoundAsASpeedChange()
        {
            // The reference made for a 25 fps master played at 23.976 fps: every cue 4.27 % later.
            var slowed = D.BluRayEnglish.Select(x => (long)Math.Round(x * PalToFilm)).ToArray();
            var result = Align(D.AmazonSpanish, slowed);
            Assert.True(result.Accepted);
            Assert.InRange(result.Timing.Scale, PalToFilm - 0.0008, PalToFilm + 0.0008);
            Assert.InRange(result.Timing.OffsetMs, -5000, -3300);
            Assert.Equal("25 → 23.976", result.Timing.SpeedName);
            // 50 minutes in, the fix moves the line by more than two minutes: far past what the ±30 s offset can do.
            Assert.True(result.Timing.ToVideo(3_000_000) - 3_000_000 > 120_000);
        }

        [Fact]
        public void TheSameTimelineNeedsNoChange()
        {
            var result = Align(D.TvSpanish, D.TvEnglish);
            Assert.True(result.Accepted);
            Assert.InRange(result.Timing.Scale, 0.999, 1.001);
            Assert.InRange(Math.Abs(result.Timing.OffsetMs), 0, 1500);
        }

        [Fact]
        public void AnUnrelatedTimelineIsLeftAlone()
        {
            var random = new Random(7);
            var noise = Enumerable.Range(0, 700).Select(_ => (long)random.Next(0, 3_300_000)).OrderBy(x => x).ToArray();
            var result = Align(D.AmazonSpanish, noise);
            Assert.False(result.Accepted);
            Assert.True(result.Timing.IsIdentity);
        }

        [Fact]
        public void AReleaseWithADifferentCutIsLeftAlone()
        {
            // Two minutes added in the middle: no single shift or speed fits the whole episode.
            var recut = D.BluRayEnglish.Select(x => x > 1_600_000 ? x + 120_000 : x).ToArray();
            var result = Align(D.AmazonEnglish, recut);
            Assert.False(result.Accepted);
            Assert.True(result.Timing.IsIdentity);
        }

        [Fact]
        public void TooFewCuesAreLeftAlone()
        {
            Assert.False(SubtitleAligner.Align(D.AmazonEnglish.Take(20).ToArray(), D.BluRayEnglish).Accepted);
            Assert.False(SubtitleAligner.Align(null, D.BluRayEnglish).Accepted);
        }

        [Fact]
        public void TimingMapsBothWays()
        {
            var timing = new SubtitleTiming { Scale = PalToFilm, OffsetMs = -4000 };
            Assert.InRange(timing.ToSubtitle(timing.ToVideo(1_000_000)), 999_999, 1_000_001);
            Assert.True(SubtitleTiming.Identity.IsIdentity);
            Assert.Null(SubtitleTiming.Identity.SpeedName);
        }
    }
}
