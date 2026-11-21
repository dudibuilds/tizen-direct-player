using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core;
using Ru7100.Core.App;
using Ru7100.Core.Playback;
using Xunit;
using D = Ru7100.Tests.Playback.SubtitleTimingData;

namespace Ru7100.Tests.Playback
{
    /// The session fits external subtitles to the playing file (standing decision 2026-11-20).
    public class SubtitleSyncSessionTests
    {
        private static readonly CancellationToken None = CancellationToken.None;
        private const double PalToFilm = 25 / (24000 / 1001.0);
        private const string RarbgFile = "Show.S01E01.1080p.BluRay.x265-RARBG.mp4";

        private static Harness ShowHarness(string fileName = RarbgFile)
        {
            var h = new Harness();
            var show = FakeCatalog.Series("tt100", "Show", 1, 3);
            h.Catalog.Metas[show.Summary.Id] = show;
            h.Resolver.Streams = videoId =>
            {
                var torrent = Harness.Torrent("A-" + videoId);
                torrent.Filename = fileName.Replace("S01E01", "S01E" + videoId.Split(':').Last().PadLeft(2, '0'));
                return new[] { torrent };
            };
            h.Opener.Head = Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray();
            h.Opener.Tail = Enumerable.Range(0, 65536).Select(i => (byte)(i * 7 % 253)).ToArray();
            return h;
        }

        private static string Srt(long[] starts, Func<long, long> map = null)
        {
            var text = new StringBuilder();
            for (var i = 0; i < starts.Length; i++)
            {
                var start = map == null ? starts[i] : map(starts[i]);
                text.Append(i + 1).Append('\n').Append(Time(start)).Append(" --> ").Append(Time(start + 1500)).Append("\nline\n\n");
            }
            return text.ToString();
        }

        private static string Time(long ms) =>
            TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss\,fff", System.Globalization.CultureInfo.InvariantCulture);

        private static SubtitleCandidate Sub(string url, string language, string release, int? fps = null) =>
            new SubtitleCandidate { Url = url, Language = language, Release = release, FpsMilli = fps };

        [Fact]
        public async Task ASubtitleMadeForThisReleaseReplacesTheAutomaticChoice()
        {
            using (var h = ShowHarness())
            {
                var web = Sub("http://subs/es-web", "spa", "Show.S01E01.720p.AMZN.WEB-DL.DDP5.1.H.264-NTb");
                h.Resolver.Subtitles = new[] { web };
                h.Resolver.SubtitlesForFile = file => new[]
                {
                    Sub("http://subs/es-web", "spa", "Show.S01E01.720p.AMZN.WEB-DL.DDP5.1.H.264-NTb"),
                    Sub("http://subs/es-rarbg", "spa", "Show.S01E01.1080p.BluRay.x265-RARBG"),
                    Sub("http://subs/en-tv", "eng", "Show.S01E01.HDTV.x264-MTB"),
                };
                h.Subtitles.Files["http://subs/es-web"] = Srt(D.AmazonSpanish);
                h.Subtitles.Files["http://subs/es-rarbg"] = Srt(D.AmazonSpanish);
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await session.PendingSubtitleWork;

                var query = h.Resolver.SubtitleQueries.Last();
                Assert.Equal("db8b3defab6d3586".Length, query.FileHash.Length);
                Assert.Equal(OpenSubtitlesHash.Compute(1_000_000_000, h.Opener.Opened[0].Head, h.Opener.Opened[0].Tail), query.FileHash);
                Assert.Equal(RarbgFile, query.Filename);
                Assert.Equal(1_000_000_000, query.SizeBytes);
                Assert.Equal(query.FileHash, session.FileHash);

                var decision = session.GetStatus().Tracks.Subtitle;
                Assert.Equal("http://subs/es-rarbg", decision.External.Url);
                Assert.Equal("release_match", decision.Reason);
                Assert.Equal("http://subs/es-rarbg", session.ExternalSubtitles[0].Url);
                Assert.True(session.SubtitleSync.IsIdentity);   // it matches: nothing to fix

                var labels = PlaybackMapping.SubtitleMenu(session.GetStatus(), session.ExternalSubtitles).Select(e => e.Label).ToArray();
                Assert.Contains("Spanish · matches your file", labels);
                Assert.Contains("Spanish · WEB NTb", labels);
                Assert.Contains("English · TV MTB", labels);
            }
        }

        [Fact]
        public async Task AnotherReleasesSubtitleIsAlignedToOneMadeForThisReleaseAndTheFixIsRemembered()
        {
            using (var h = ShowHarness())
            {
                // Only an English subtitle was made for this (slowed, 23.976 fps) file; the Spanish one is a web release's.
                h.Resolver.Subtitles = new[] { Sub("http://subs/es-web", "spa", "Show.S01E01.720p.AMZN.WEB-DL.DDP5.1.H.264-NTb", 25000) };
                h.Resolver.SubtitlesForFile = file => file.Filename.Contains("S01E01")
                    ? new[] { Sub("http://subs/en-rarbg", "eng", "Show.S01E01.1080p.BluRay.x265-RARBG") }
                    : Array.Empty<SubtitleCandidate>();
                h.Subtitles.Files["http://subs/es-web"] = Srt(D.AmazonSpanish);
                h.Subtitles.Files["http://subs/en-rarbg"] = Srt(D.BluRayEnglish, x => (long)Math.Round(x * PalToFilm));
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await session.PendingSubtitleWork;

                Assert.Equal("http://subs/es-web", session.GetStatus().Tracks.Subtitle.External.Url);   // still the Spanish one
                var sync = session.GetStatus().Tracks.SubtitleSync;
                Assert.Equal("reference", sync.Source);
                Assert.InRange(sync.Scale, PalToFilm - 0.0008, PalToFilm + 0.0008);
                Assert.Equal("Synced to your file · 25 → 23.976", PlaybackMapping.SyncLabel(sync));
                Assert.Contains("SubtitleTimings", h.Store.Values[PlaybackKeys.TrackPreferences]);

                // The next episode of the same release has no reference: the remembered speed starts it in sync.
                h.Subtitles.Files["http://subs/es-web-2"] = Srt(D.AmazonSpanish);
                h.Resolver.Subtitles = new[] { Sub("http://subs/es-web-2", "spa", "Show.S01E02.720p.AMZN.WEB-DL.DDP5.1.H.264-NTb", 25000) };
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 2 }, None);
                await session.PendingSubtitleWork;
                sync = session.SubtitleSync;
                Assert.Equal("remembered", sync.Source);
                Assert.InRange(sync.Scale, PalToFilm - 0.0008, PalToFilm + 0.0008);
            }
        }

        [Fact]
        public async Task OneOffsetConfirmedByBothFrameRatesFixesTheSpeed()
        {
            using (var h = ShowHarness())
            {
                h.Player.VideoFps = 23;   // Tizen's whole-number report for 23.976
                h.Resolver.Subtitles = new[] { Sub("http://subs/es-web", "spa", "Show.S01E01.720p.AMZN.WEB-DL.DDP5.1.H.264-NTb", 25000) };
                h.Subtitles.Files["http://subs/es-web"] = Srt(D.AmazonSpanish);
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await session.PendingSubtitleWork;
                Assert.True(session.SubtitleSync.IsIdentity);

                // What the viewer did at 13:40: +27 s brought the Spanish lines onto the speech.
                h.Player.PositionMs = 820_000;
                session.SetSubtitleOffset(27_000);
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(4));
                await session.TickAsync();

                var sync = session.SubtitleSync;
                Assert.Equal("offset", sync.Source);
                Assert.Equal("25 → 23.976", sync.SpeedName);
                Assert.InRange(sync.OffsetMs, -9000, -5000);
                Assert.Equal(0, session.GetStatus().Tracks.SubtitleOffsetMs);   // folded into the fix
                // Where the viewer was looking, the same line still shows.
                Assert.InRange(sync.ToSubtitle(820_000) - (820_000 - 27_000), -50, 50);
            }
        }

        [Fact]
        public async Task OneOffsetAloneIsNotEnough()
        {
            using (var h = ShowHarness())
            {
                // No frame rate from the player: a single offset could as well be a plain shift.
                h.Resolver.Subtitles = new[] { Sub("http://subs/es-web", "spa", "Show.S01E01.720p.AMZN.WEB-DL.DDP5.1.H.264-NTb", 25000) };
                h.Subtitles.Files["http://subs/es-web"] = Srt(D.AmazonSpanish);
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await session.PendingSubtitleWork;
                h.Player.PositionMs = 820_000;
                session.SetSubtitleOffset(27_000);
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(4));
                await session.TickAsync();
                Assert.True(session.SubtitleSync.IsIdentity);
                Assert.Equal(27_000, session.GetStatus().Tracks.SubtitleOffsetMs);
            }
        }

        [Fact]
        public async Task TwoOffsetsFarApartRevealTheSpeed()
        {
            using (var h = ShowHarness())
            {
                h.Resolver.Subtitles = new[] { Sub("http://subs/es-web", "spa", "Show.S01E01.720p.AMZN.WEB-DL.DDP5.1.H.264-NTb") };
                h.Subtitles.Files["http://subs/es-web"] = Srt(D.AmazonSpanish);
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await session.PendingSubtitleWork;

                h.Player.PositionMs = 120_000;
                session.SetSubtitleOffset(1_000);
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(4));
                await session.TickAsync();
                Assert.True(session.SubtitleSync.IsIdentity);   // one point: could be a shift

                h.Player.PositionMs = 780_000;
                session.SetSubtitleOffset(28_000);
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(4));
                await session.TickAsync();
                var sync = session.SubtitleSync;
                Assert.Equal("offset", sync.Source);
                Assert.InRange(sync.Scale, PalToFilm - 0.0001, PalToFilm + 0.0001);
                Assert.Equal(0, session.GetStatus().Tracks.SubtitleOffsetMs);
            }
        }

        [Fact]
        public async Task OffsetsThatAreNotAFrameRateChangeStayAnOffset()
        {
            using (var h = ShowHarness())
            {
                h.Resolver.Subtitles = new[] { Sub("http://subs/es-web", "spa", "Show.S01E01.720p.AMZN.WEB-DL.DDP5.1.H.264-NTb") };
                h.Subtitles.Files["http://subs/es-web"] = Srt(D.AmazonSpanish);
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await session.PendingSubtitleWork;
                h.Player.PositionMs = 120_000;
                session.SetSubtitleOffset(1_000);
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(4));
                await session.TickAsync();
                h.Player.PositionMs = 780_000;
                session.SetSubtitleOffset(9_000);   // 8 s over 11 minutes: 1.2 %, no frame-rate pair
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(4));
                await session.TickAsync();
                Assert.True(session.SubtitleSync.IsIdentity);
                Assert.Equal(9_000, session.GetStatus().Tracks.SubtitleOffsetMs);
            }
        }

        private static async Task<PlaybackSession> PlayWebSpanishAsync(Harness h, int? fps = 25000)
        {
            h.Resolver.Subtitles = new[] { Sub("http://subs/es-web", "spa", "Show.S01E01.720p.AMZN.WEB-DL.DDP5.1.H.264-NTb", fps) };
            h.Subtitles.Files["http://subs/es-web"] = Srt(D.AmazonSpanish);
            var session = await h.StartAsync();
            await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
            await session.PendingSubtitleWork;
            return session;
        }

        private static async Task SettleAsync(Harness h, PlaybackSession session, long position, int offset)
        {
            h.Player.PositionMs = position;
            session.SetSubtitleOffset(offset);
            await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(4));
            await session.TickAsync();
        }

        [Fact]
        public async Task OffsetsSetByEyeAreNotASpeed()
        {
            using (var h = ShowHarness())
            {
                var session = await PlayWebSpanishAsync(h);
                await SettleAsync(h, session, 120_000, 1_000);
                await SettleAsync(h, session, 1_200_000, -1_000);   // 2 s over 18 minutes: nudges, not 23.976 vs 24
                Assert.True(session.SubtitleSync.IsIdentity);
                Assert.Equal(-1_000, session.GetStatus().Tracks.SubtitleOffsetMs);
            }
        }

        [Fact]
        public async Task AnOffsetHeldAtTheStopIsNotASyncPoint()
        {
            using (var h = ShowHarness())
            {
                var session = await PlayWebSpanishAsync(h, fps: null);
                await SettleAsync(h, session, 120_000, 1_000);
                await SettleAsync(h, session, 1_500_000, 30_000);   // the viewer wanted more than the control allows
                Assert.True(session.SubtitleSync.IsIdentity);
            }
        }

        [Fact]
        public async Task OneOffsetThatTheFrameRatesDoNotExplainStaysAnOffset()
        {
            using (var h = ShowHarness())
            {
                h.Player.VideoFps = 23;
                var session = await PlayWebSpanishAsync(h);
                await SettleAsync(h, session, 820_000, 15_000);   // a slowdown predicts ~30 s here: this is a shift
                Assert.True(session.SubtitleSync.IsIdentity);
                await SettleAsync(h, session, 820_000, -27_000);  // the wrong direction for 25 → 23.976
                Assert.True(session.SubtitleSync.IsIdentity);
            }
        }

        [Fact]
        public async Task ResetDropsTheSpeedFixAndForgetsIt()
        {
            using (var h = ShowHarness())
            {
                h.Player.VideoFps = 23;
                var session = await PlayWebSpanishAsync(h);
                await SettleAsync(h, session, 820_000, 27_000);
                Assert.Equal("offset", session.SubtitleSync.Source);
                await Wait.Until(() => h.Store.Values.TryGetValue(PlaybackKeys.TrackPreferences, out var v) && v.Contains("rarbg|bluray"), "remembered");

                session.ResetSubtitleTiming();
                Assert.True(session.SubtitleSync.IsIdentity);
                Assert.Equal(0, session.GetStatus().Tracks.SubtitleOffsetMs);
                await Wait.Until(() => !h.Store.Values[PlaybackKeys.TrackPreferences].Contains("rarbg|bluray"), "forgotten");
                // The reset is not a sync point: nothing comes back after it settles.
                await h.Clock.AdvanceAsync(TimeSpan.FromSeconds(4));
                await session.TickAsync();
                Assert.True(session.SubtitleSync.IsIdentity);
            }
        }

        [Fact]
        public async Task TheViewersOwnPickIsNeverSwapped()
        {
            using (var h = ShowHarness())
            {
                var web = Sub("http://subs/es-web", "spa", "Show.S01E01.720p.AMZN.WEB-DL.DDP5.1.H.264-NTb");
                h.Resolver.Subtitles = new[] { web };
                var answer = new TaskCompletionSource<bool>();
                h.Resolver.FileGate = answer.Task;
                h.Resolver.SubtitlesForFile = file => new[] { web, Sub("http://subs/es-rarbg", "spa", "Show.S01E01.1080p.BluRay.x265-RARBG") };
                h.Subtitles.Files["http://subs/es-web"] = Srt(D.AmazonSpanish);
                h.Subtitles.Files["http://subs/es-rarbg"] = Srt(D.AmazonSpanish);
                var session = await h.StartAsync();
                await session.PlayAsync(new PlayRequest { MetaId = "tt100", Season = 1, Episode = 1 }, None);
                await Wait.Until(() => session.GetStatus().Tracks.Subtitle?.External?.Url == "http://subs/es-web", "automatic choice");
                await session.SelectSubtitleAsync(new SubtitleDecision { Kind = SubtitleKind.External, External = web }, None);
                answer.SetResult(true);
                await session.PendingSubtitleWork;
                var decision = session.GetStatus().Tracks.Subtitle;
                Assert.Equal("http://subs/es-web", decision.External.Url);
                Assert.Equal("user", decision.Reason);
                Assert.Equal("http://subs/es-rarbg", session.ExternalSubtitles[0].Url);   // still listed first for the menu
            }
        }
    }
}
