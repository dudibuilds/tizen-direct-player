using System;
using System.Collections.Generic;
using System.Linq;
using Ru7100.Core;
using Ru7100.Core.Addons;
using Xunit;

namespace Ru7100.Core.Tests.Addons
{
    public class StreamRankingTests
    {
        private const string Movie = "torrentio-movie-tt1375666.json";
        private const string Series = "torrentio-series-tt0903747-1-1.json";
        private const string Anime = "torrentio-series-tt2560140-1-1.json";

        private static StreamAnalysis Analyze(string title, string name = "Torrentio\n1080p", bool anime = false, bool episodic = false) =>
            StreamRanking.Analyze(Fixtures.Torrent(title, name), episodic, anime);

        // ---- Real Torrentio fixtures ----

        [Theory]
        [InlineData(Movie, false, false)]
        [InlineData(Series, true, false)]
        [InlineData(Anime, true, true)]
        public void FixtureRankingRespectsHardFiltersCapAndOrder(string fixture, bool episodic, bool anime)
        {
            var streams = Fixtures.Streams(fixture);
            var ranked = StreamRanking.RankAnalyses(streams, new RankOptions { Episodic = episodic, Anime = anime });

            Assert.NotEmpty(ranked);
            foreach (var entry in ranked)
            {
                var text = entry.Stream.SearchText;
                Assert.Null(entry.RejectReason);
                Assert.DoesNotMatch(@"(?i)\bAV1\b", text);
                Assert.DoesNotMatch(@"(?i)\bDTS", text);
                Assert.DoesNotMatch(@"(?i)true-?hd|\btruhd", text);
                Assert.True(!entry.SizeBytes.HasValue || entry.SizeBytes.Value <= 6.5e9, text);
            }

            // F9: group key [tier<0?0:1, band, tier] descending, then score descending.
            for (var i = 1; i < ranked.Count; i++)
            {
                var a = Key(ranked[i - 1], anime);
                var b = Key(ranked[i], anime);
                var cmp = Compare(a, b);
                Assert.True(cmp > 0 || (cmp == 0 && ranked[i - 1].Score >= ranked[i].Score), "rank order broken at " + i);
            }

            // Deterministic.
            var again = StreamRanking.Rank(streams, new RankOptions { Episodic = episodic, Anime = anime });
            Assert.Equal(ranked.Select(entry => entry.Stream.Identity), again.Select(candidate => candidate.Identity));
        }

        [Theory]
        [InlineData("Ironclad.S02.COMPLETE.1080p.AMZN.WEB-DL.DDP5.1.H.264-Kitsune[TGx]\nIronclad.S02E01.A.LESSON.FOR.YOUR.NEXT.LIFE.1080p.mkv\n👤 606 💾 1.42 GB", true)]
        [InlineData("Ironclad S02 Season 2 1080p AMZN WEBRip AAC5.1 10bits x265-Rapta\nIronclad - S02E01.mkv\n👤 180", true)]
        [InlineData("Ironclad 2021 Seasons 1 to 3 Complete 1080p WEB x264 [i_c]\nSeason 2/Ironclad - S02E01.mkv", true)]
        [InlineData("Несокрушимый / Ironclad / Сезон: 2 / Серии: 1-8 из 8 [2023 WEB-DL 1080p]\nIronclad.S02E01.1080p.WEB-DL.mkv", true)]
        [InlineData("Ironclad.2021.S02E01.1080p.WEB.h264-ETHEL [TGx]\n👤 60 💾 1.42 GB ⚙️ TorrentGalaxy", false)]
        [InlineData("Ironclad S02E01 A LESSON FOR YOUR NEXT LIFE 1080p AMZN WEB-DL DDP5 1 H 26\n👤 18", false)]
        [InlineData("Night Harbor 1x01 Pilot 720p\n👤 40", false)]
        [InlineData("Paper Lanterns 2010 1080p BluRay x264\n👤 900", false)]
        public void SeasonPacksAreRecognisedFromTheTorrentName(string title, bool pack) =>
            Assert.Equal(pack, StreamRanking.IsSeasonPack(new AddonStreamItem { Title = title }));

        [Fact]
        public void ASingleEpisodeOutranksAPackOfComparableHealthButNotAMuchHealthierOne()
        {
            var single = Fixtures.Torrent("Show.S02E01.1080p.WEB-DL.H.264-GRP\n👤 300 💾 1.4 GB", "Torrentio\n1080p");
            var pack = Fixtures.Torrent("Show.S02.COMPLETE.1080p.WEB-DL.H.264-GRP\nShow.S02E01.1080p.WEB-DL.H.264-GRP.mkv\n👤 400 💾 1.4 GB", "Torrentio\n1080p");
            var hugePack = Fixtures.Torrent("Show.S02.COMPLETE.1080p.WEB-DL.H.264-GRP2\nShow.S02E01.mkv\n👤 2000 💾 1.4 GB", "Torrentio\n1080p");
            var ranked = StreamRanking.RankAnalyses(new[] { pack, single }, new RankOptions { Episodic = true });
            Assert.Equal(single.Identity, ranked[0].Stream.Identity);
            Assert.True(ranked[1].SeasonPack);
            Assert.Contains("season pack", ranked[1].Reasons);
            ranked = StreamRanking.RankAnalyses(new[] { single, hugePack }, new RankOptions { Episodic = true });
            Assert.Equal(hugePack.Identity, ranked[0].Stream.Identity);
            // Movies never get the pack cost.
            Assert.False(StreamRanking.Analyze(pack, episodic: false, anime: false).SeasonPack);
        }

        [Fact]
        public void MovieTopPicksArePlayableOnTheTv()
        {
            var ranked = StreamRanking.Rank(Fixtures.Streams(Movie), new RankOptions { Episodic = false });
            var top = ranked[0];
            Assert.NotNull(top.InfoHash);
            Assert.True(top.SizeBytes <= 6.5e9);
            Assert.Contains(top.VideoCodec, new[] { "h264", "hevc", "unknown" });
            Assert.Equal(StreamRanking.DefaultTrackers, top.Trackers);
            Assert.Equal(2, top.HealthBand);
        }

        [Fact]
        public void RealTitlesWithUnsupportedCodecsAreRejected()
        {
            var streams = Fixtures.Streams(Movie);
            string Reject(string startsWith) => StreamRanking.Analyze(streams.First(s => s.Title.StartsWith(startsWith, StringComparison.Ordinal)), false, false).RejectReason;

            Assert.Equal("AV1 video", Reject("Paper Lanterns 2010 PROPER Bluray 2160p AV1 HDR10"));
            Assert.Equal("DTS audio", Reject("Paper Lanterns 2010 UHD BluRay 2160p HDR10 DV HEVC DTS-HD"));
            // "4k DV" with no HDR10 marker anywhere.
            Assert.Equal("Dolby Vision without HDR10", Reject("Paper Lanterns 2010 UHD BluRay 2160p DTS-HD MA 5 1 DV HEVC HYBRID REMUX"));

            var anime = Fixtures.Streams(Anime);
            var truhd = anime.First(s => s.Title.StartsWith("Iron.of.Tides.S01.Bluray.REMUX.1080p.TRUHD", StringComparison.Ordinal));
            Assert.Equal("TrueHD audio", StreamRanking.Analyze(truhd, true, true).RejectReason);
        }

        [Fact]
        public void PermissiveDeviceKeepsCodecsButCamIsAlwaysDropped()
        {
            var options = new RankOptions { Device = DeviceCaps.Permissive, Episodic = false };
            var av1 = Fixtures.Torrent("Movie 2020 1080p WEB-DL AV1 Opus\n👤 100 💾 2 GB");
            var cam = Fixtures.Torrent("Movie 2024 HDCAM x264\n👤 900 💾 1 GB");
            var ranked = StreamRanking.RankAnalyses(new[] { av1, cam }, options);
            Assert.Single(ranked);
            Assert.Equal("av1", ranked[0].VideoCodec);
        }

        // ---- TV hard filters ----

        [Theory]
        [InlineData("Movie.2023.2160p.WEB-DL.DDP5.1.DV.H.265-GRP", "Dolby Vision without HDR10")]
        [InlineData("Movie.2023.2160p.WEB-DL.DDP5.1.DoVi.H.265-GRP", "Dolby Vision without HDR10")]
        [InlineData("Movie 2023 2160p Dolby Vision WEB-DL", "Dolby Vision without HDR10")]
        [InlineData("Movie.2023.1080p.BluRay.DTS-HD.MA.5.1.x264", "DTS audio")]
        [InlineData("Movie.2023.1080p.BluRay.DTS.x264", "DTS audio")]
        [InlineData("Movie.2023.1080p.BluRay.TrueHD.Atmos.7.1.x264", "TrueHD audio")]
        [InlineData("Movie.2023.1080p.WEB.AV1.Opus", "AV1 video")]
        [InlineData("Movie 2023 TELESYNC 1080p", "cam/screener")]
        [InlineData("Movie 2023 WEB Screener 1080p", "cam/screener")]
        public void HardFilters(string title, string reason)
        {
            Assert.Equal(reason, Analyze(title).RejectReason);
        }

        [Theory]
        [InlineData("Movie.2023.2160p.WEB-DL.DDP5.1.DV.HDR.H.265-GRP", "Torrentio\n4k DV | HDR")]
        [InlineData("Movie.2023.2160p.UHD.BluRay.HDR10.HEVC.EAC3", "Torrentio\n4k HDR")]
        [InlineData("Movie.2023.1080p.WEB-DL.AC3.5.1.x264", "Torrentio\n1080p")]
        [InlineData("Movie.2023.1080p.BluRay.x265.10bit.AAC", "Torrentio\n1080p")]
        public void TvPlayableReleasesPass(string title, string name)
        {
            Assert.Null(Analyze(title, name).RejectReason);
        }

        [Fact]
        public void DolbyVisionWithHdr10FallbackIsPenalizedNotDropped()
        {
            var hdr = Analyze("Movie.2023.2160p.WEB-DL.DDP5.1.HDR.H.265-GRP\n👤 100 💾 5 GB", "Torrentio\n4k HDR");
            var dv = Analyze("Movie.2023.2160p.WEB-DL.DDP5.1.DV.HDR.H.265-GRP\n👤 100 💾 5 GB", "Torrentio\n4k DV | HDR");
            Assert.Null(dv.RejectReason);
            Assert.Equal(10, hdr.Score - dv.Score, 6);
        }

        [Fact]
        public void HevcOnlySlightlyBehindH264()
        {
            var h264 = Analyze("Show.S01E01.1080p.WEB-DL.AAC.x264\n👤 100 💾 1 GB");
            var hevc = Analyze("Show.S01E01.1080p.WEB-DL.AAC.x265\n👤 100 💾 1 GB");
            var spaced = Analyze("Night Harbor S01E01 Pilot 2160p NF WEB-DL DDP5 1 H 265-XEBEC");
            Assert.Equal("hevc", hevc.VideoCodec);
            Assert.Equal("hevc", spaced.VideoCodec);
            Assert.Equal("eac3", spaced.AudioCodec);
            Assert.Equal(3, h264.Score - hevc.Score, 6);
        }

        [Fact]
        public void TenBitH264IsPenalized()
        {
            var eight = Analyze("[Group] Show - 01 [1080p x264 AAC]\n👤 100");
            var ten = Analyze("[Group] Show - 01 [1080p Hi10P x264 AAC]\n👤 100");
            Assert.Equal(25, eight.Score - ten.Score, 6);
        }

        // ---- F4 parsing, F5 score ----

        [Fact]
        public void ParsesSeedersSizeQualityAndCodecs()
        {
            var entry = Analyze("Paper Lanterns.2010.1080p.BluRay.x264.AAC5.1-[YTS.MX]\n👤 1234 💾 2.03 GB ⚙️ YTS", "Torrentio\n1080p");
            Assert.Equal(1234, entry.Seeders);
            Assert.True(entry.SeedersKnown);
            Assert.Equal(2_030_000_000L, entry.SizeBytes);
            Assert.Equal("1080p", entry.Quality);
            Assert.Equal("h264", entry.VideoCodec);
            Assert.Equal("aac", entry.AudioCodec);
            // log2(1235)*12 + 1080p 30 + BluRay 10 + H.264 10 + AAC 8
            Assert.Equal(Math.Log(1235, 2) * 12 + 58, entry.Score, 6);

            var mb = Analyze("Show S01E01 720p WEB seeders: 12 850 MB", "Torrentio\n720p");
            Assert.Equal(12, mb.Seeders);
            Assert.Equal(850_000_000L, mb.SizeBytes);
            Assert.Equal("720p", mb.Quality);

            var unknown = Analyze("Some release", "Torrentio");
            Assert.False(unknown.SeedersKnown);
            Assert.Null(unknown.SizeBytes);
            Assert.Equal("unknown", unknown.Quality);
        }

        [Fact]
        public void EpisodicSizeBonusAndPenalty()
        {
            var normal = StreamRanking.Analyze(Fixtures.Torrent("Show S01E01 1080p WEB-DL x264 💾 1.2 GB"), true, false);
            var huge = StreamRanking.Analyze(Fixtures.Torrent("Show S01E01 1080p WEB-DL x264 💾 9 GB"), true, false);
            var movie = StreamRanking.Analyze(Fixtures.Torrent("Show S01E01 1080p WEB-DL x264 💾 1.2 GB"), false, false);
            Assert.Equal(8, normal.Score - movie.Score, 6);
            Assert.Equal(28, normal.Score - huge.Score, 6);
        }

        [Fact]
        public void TorrentWithoutFileIdxLosesThreePoints()
        {
            var withIdx = StreamRanking.Analyze(Fixtures.Torrent("Movie 1080p x264", fileIdx: 0), false, false);
            var without = StreamRanking.Analyze(Fixtures.Torrent("Movie 1080p x264", fileIdx: null), false, false);
            Assert.Equal(3, withIdx.Score - without.Score, 6);
        }

        // ---- F6 general language tiers ----

        [Theory]
        [InlineData("Movie.2010.1080p.WEB-DL.ENG.SpaSub", 7)]
        [InlineData("Movie 2010 1080p ENG audio rus subs", 4)]   // a subtitle tag is not foreign audio
        [InlineData("Movie 2010 1080p English audio + Spanish subtitles", 7)]
        [InlineData("Movie 2010 1080p\n🇬🇧 English subs", 6)]
        [InlineData("Movie 2010 1080p ENG multi-subs", 5)]
        [InlineData("Movie 2010 1080p\n🇺🇸", 4)]
        [InlineData("Movie 2010 1080p spa subs\n🇬🇧 / 🇷🇺", 3)]
        [InlineData("Movie 2010 1080p Dual Audio eng subs", 2)]
        [InlineData("Movie 2010 1080p Dual Audio", 1)]
        [InlineData("Movie 2010 1080p BluRay x264", 0)]
        [InlineData("Movie 2010 1080p\n🇮🇹", -1)]
        [InlineData("Movie.2010.1080p.Rus.x264", -1)]
        [InlineData("Movie 2010 1080p French audio", -1)]
        public void GeneralLanguageTiers(string text, int tier)
        {
            Assert.Equal(tier, StreamRanking.DetectGeneralLanguages(text).Tier);
        }

        [Fact]
        public void CyrillicNeighboursDoNotBreakAsciiWordBoundaries()
        {
            // JS \b is ASCII-only: "Rus" right after Cyrillic letters still counts as a tag boundary only via the
            // explicit delimiter class, and "eng" glued to Cyrillic is a separate word.
            Assert.Equal(-1, StreamRanking.DetectGeneralLanguages("Фильм.Rus.1080p").Tier);
            Assert.True(StreamRanking.DetectGeneralLanguages("Фильмeng 1080p").EnglishAudio);
        }

        [Fact]
        public void SpanishSubtitlesOutrankEnglishSubtitlesWithinTheSameHealthBand()
        {
            var eng = Fixtures.Torrent("Movie.2010.1080p.WEB-DL.x264.ENG.EngSub\n👤 900 💾 2 GB");
            var spa = Fixtures.Torrent("Movie.2010.1080p.WEB-DL.x264.ENG.SpaSub\n👤 300 💾 2 GB");
            var ranked = StreamRanking.Rank(new[] { eng, spa }, new RankOptions { Episodic = false });
            Assert.Equal(spa.InfoHash, ranked[0].InfoHash);
            Assert.Equal(7, ranked[0].LanguageTier);
            Assert.Equal(6, ranked[1].LanguageTier);
        }

        [Fact]
        public void HealthyBandBeatsLanguageTierAndForeignAudioSinks()
        {
            // floor = max(50, 0.1 * 2000) = 200
            var healthyPlain = Fixtures.Torrent("Movie 1080p x264 ENG\n👤 2000 💾 2 GB");
            var weakSpanish = Fixtures.Torrent("Movie 1080p x264 ENG SpaSub\n👤 150 💾 2 GB");
            var unknownSeeders = Fixtures.Torrent("Movie 1080p x264 ENG SpaSub 💾 2 GB");
            var foreign = Fixtures.Torrent("Movie 1080p x264 French audio\n👤 5000 💾 2 GB");
            var direct = new AddonStreamItem { AddonId = "x", Name = "Direct 1080p", Title = "Movie 1080p x264 ENG", Url = "https://cdn.example/movie.mp4" };

            var ranked = StreamRanking.RankAnalyses(new[] { foreign, weakSpanish, unknownSeeders, healthyPlain, direct }, new RankOptions { Episodic = false });
            Assert.Equal(new[] { 2, 2, 1, 0, 2 }, ranked.Select(entry => entry.HealthBand).ToArray());
            Assert.Same(foreign, ranked.Last().Stream);
            Assert.Same(unknownSeeders, ranked[2].Stream);
            Assert.Same(weakSpanish, ranked[3].Stream);
        }

        // ---- F7 anime ----

        [Theory]
        [InlineData("[SubsPlease] Show - 01 (1080p) Japanese audio English subs", 5)]
        [InlineData("[Group] Show - 01 JPN multi-subs", 4)]
        [InlineData("[Judas] Tetsu no Shio S1 - 01 [Dual-Audio][Eng-Subs]", 4)]
        [InlineData("[Group] Show [Dual Audio] [Multi Subs]", 3)]
        [InlineData("[Group] Show - 01 [Eng Subs]", 2)]
        [InlineData("[Group] Show - 01 [Dual Audio]", 1)]
        [InlineData("Show S01E01 English Dub 1080p", -1)]
        [InlineData("[Group] Show - 01", 0)]
        public void AnimeLanguageTiers(string text, int tier)
        {
            Assert.Equal(tier, StreamRanking.DetectAnimeLanguages(text).Tier);
        }

        [Fact]
        public void AnimeWithoutOriginalAudioIsWeakBand()
        {
            var plain = StreamRanking.RankAnalyses(new[] { Fixtures.Torrent("[Group] Show - 01 1080p\n👤 500") }, new RankOptions { Anime = true });
            Assert.Equal(0, plain[0].HealthBand);
            var japanese = StreamRanking.RankAnalyses(new[] { Fixtures.Torrent("[Group] Show - 01 1080p JPN audio Eng subs\n👤 500") }, new RankOptions { Anime = true });
            Assert.Equal(2, japanese[0].HealthBand);
            Assert.Equal(5, japanese[0].AnimeLanguageTier);
        }

        [Fact]
        public void AnimePrefersOriginalAudioWithEnglishSubs()
        {
            var ranked = StreamRanking.RankAnalyses(Fixtures.Streams(Anime), new RankOptions { Anime = true, Episodic = true });
            Assert.True(ranked[0].AnimeOriginalAudio);
            Assert.True(ranked[0].AnimeLanguageTier >= 4, ranked[0].Stream.Title);
            Assert.Equal(2, ranked[0].HealthBand);
        }

        // ---- F9 quality, F10 size cap ----

        [Fact]
        public void SizeCapIsSixPointFiveGigabytesAndMaxSizeOnlyLowersIt()
        {
            var small = Fixtures.Torrent("Movie 1080p x264 💾 1.5 GB");
            var medium = Fixtures.Torrent("Movie 1080p x265 💾 4 GB");
            var large = Fixtures.Torrent("Movie 2160p x265 HDR 💾 7.2 GB");
            var unknown = Fixtures.Torrent("Movie 1080p x264 REPACK");
            var all = new[] { small, medium, large, unknown };

            Assert.Equal(6.5, StreamRanking.EffectiveMaxSizeGb(null));
            Assert.Equal(6.5, StreamRanking.EffectiveMaxSizeGb(0));
            Assert.Equal(6.5, StreamRanking.EffectiveMaxSizeGb(20));
            Assert.Equal(2, StreamRanking.EffectiveMaxSizeGb(2));

            var capped = StreamRanking.RankAnalyses(all, new RankOptions()).Select(entry => entry.Stream).ToList();
            Assert.DoesNotContain(large, capped);
            Assert.Contains(unknown, capped);
            Assert.Equal(3, capped.Count);

            var raised = StreamRanking.RankAnalyses(all, new RankOptions { MaxSizeGb = 50 }).Select(entry => entry.Stream).ToList();
            Assert.DoesNotContain(large, raised);

            var lowered = StreamRanking.RankAnalyses(all, new RankOptions { MaxSizeGb = 2 }).Select(entry => entry.Stream).ToList();
            Assert.Equal(new[] { small, unknown }.OrderBy(s => s.InfoHash), lowered.OrderBy(s => s.InfoHash));
        }

        [Fact]
        public void QualityFilterIsExactUnlessAuto()
        {
            var hd = Fixtures.Torrent("Movie 1080p x264 💾 2 GB");
            var uhd = Fixtures.Torrent("Movie 2160p x265 HDR 💾 6 GB", "Torrentio\n4k HDR");
            var sd = Fixtures.Torrent("Movie 720p x264 💾 1 GB", "Torrentio\n720p");
            Assert.Equal(3, StreamRanking.Rank(new[] { hd, uhd, sd }, new RankOptions { Quality = "auto" }).Count);
            Assert.Equal(uhd.InfoHash, StreamRanking.Rank(new[] { hd, uhd, sd }, new RankOptions { Quality = "4k" }).Single().InfoHash);
            Assert.Equal(hd.InfoHash, StreamRanking.Rank(new[] { hd, uhd, sd }, new RankOptions { Quality = "1080p" }).Single().InfoHash);
        }

        // ---- F2 dedupe, exclusions, trackers ----

        [Fact]
        public void DeduplicatesByInfoHashAndFileIndexThenUrl()
        {
            var hash = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
            var streams = new[]
            {
                Fixtures.Torrent("first 1080p", hash: hash, fileIdx: 1),
                Fixtures.Torrent("second 1080p", hash: hash.ToLowerInvariant(), fileIdx: 1),
                Fixtures.Torrent("other file 1080p", hash: hash, fileIdx: 2),
                Fixtures.Torrent("auto file 1080p", hash: hash, fileIdx: null),
                new AddonStreamItem { Title = "direct", Url = "https://cdn.example/a.mp4" },
                new AddonStreamItem { Title = "direct again", Url = "https://cdn.example/a.mp4" },
                new AddonStreamItem { Title = "nothing" },
            };
            var unique = StreamRanking.Deduplicate(streams);
            Assert.Equal(new[] { "first 1080p", "other file 1080p", "auto file 1080p", "direct" }, unique.Select(s => s.Title));
            Assert.Equal("abcdef0123456789abcdef0123456789abcdef01:auto", unique[2].Identity);
        }

        [Fact]
        public void ExcludedIdentitiesAreRemoved()
        {
            var a = Fixtures.Torrent("A 1080p x264 👤 100", fileIdx: 3);
            var b = Fixtures.Torrent("B 1080p x264 👤 90");
            var options = new RankOptions { ExcludeIdentities = new HashSet<string> { a.InfoHash + ":3" } };
            var ranked = StreamRanking.Rank(new[] { a, b }, options);
            Assert.Equal(b.InfoHash, ranked.Single().InfoHash);
        }

        [Fact]
        public void TorrentCandidatesCarryTheStreamTrackers()
        {
            var withTrackers = Fixtures.Torrent("A 1080p", fileIdx: 0, sources: new[]
            {
                "tracker:udp://tracker.example.org:1337/announce",
                "tracker:http://bt.example.net/announce",
                "dht:abcdef",
                "tracker:ftp://bad.example/announce",
            });
            var candidate = StreamRanking.Rank(new[] { withTrackers }, new RankOptions()).Single();
            Assert.Equal(new[] { "udp://tracker.example.org:1337/announce", "http://bt.example.net/announce" }, candidate.Trackers);

            var bare = StreamRanking.Rank(new[] { Fixtures.Torrent("B 1080p", sources: new[] { "dht:abc" }) }, new RankOptions()).Single();
            Assert.Equal(StreamRanking.DefaultTrackers, bare.Trackers);
        }

        [Fact]
        public void DirectUrlCandidatesHaveNoInfoHash()
        {
            var direct = new AddonStreamItem { AddonId = "d", Name = "Debrid 1080p", Title = "Movie 1080p x264", Url = "https://cdn.example/m.mkv" };
            var candidate = StreamRanking.Rank(new[] { direct }, new RankOptions()).Single();
            Assert.Null(candidate.InfoHash);
            Assert.Equal("https://cdn.example/m.mkv", candidate.Identity);
            Assert.Empty(candidate.Trackers);
            Assert.Equal(2, candidate.HealthBand);
        }

        [Fact]
        public void ExternalAndProxiedStreamsAreDropped()
        {
            var external = new AddonStreamItem { Title = "Trailer", ExternalUrl = "https://youtube.example/x", Url = "https://a.example/b" };
            var proxied = new AddonStreamItem { Title = "Proxied 1080p", Url = "https://a.example/c", HasProxyHeaders = true };
            Assert.Empty(StreamRanking.Rank(new[] { external, proxied }, new RankOptions()));
        }

        private static int[] Key(StreamAnalysis entry, bool anime)
        {
            var tier = entry.LanguageTier(anime);
            return new[] { tier < 0 ? 0 : 1, entry.HealthBand, tier };
        }

        private static int Compare(int[] a, int[] b)
        {
            for (var i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return a[i] - b[i];
            return 0;
        }
    }
}
