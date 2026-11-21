using System.Linq;
using Newtonsoft.Json.Linq;
using Ru7100.Core;
using Ru7100.Core.Addons;
using Ru7100.Core.Playback;
using Xunit;

namespace Ru7100.Tests.Playback
{
    public class SubtitleMatchingTests
    {
        private const string File = "His.Dark.Materials.S01E01.1080p.BluRay.x265-RARBG.mp4";

        [Theory]
        [InlineData("His.Dark.Materials.S01E01.1080p.BluRay.x265-RARBG.mp4", "RARBG", "bluray", 1080)]
        [InlineData("His.Dark.Materials.S01E01.720p.BluRay.x264-SHORTBREHD-HI.srt", "SHORTBREHD", "bluray", 720)]
        [InlineData("His.Dark.Materials.S01E01.1080p.HDTV.H264-MTB.Hi.srt", "MTB", "hdtv", 1080)]
        [InlineData("His.Dark.Materials.S01E01.Lyras.Jordan.720p.AMZN.WEB-DL.DDP5.1.H.264-NTb", "NTb", "web", 720)]
        [InlineData("His.Dark.Materials.S01E01.BDRip.x264-HAGGiS", "HAGGiS", "bluray", 0)]
        [InlineData("Some Movie 2019", null, null, 0)]
        public void ReadsTheReleaseFromAName(string name, string group, string source, int resolution)
        {
            var info = ReleaseInfo.Parse(name);
            Assert.Equal(group, info.Group);
            Assert.Equal(source, info.Source);
            Assert.Equal(resolution, info.Resolution);
        }

        [Fact]
        public void ScoresHowWellASubtitleFitsTheFile()
        {
            SubtitleMatch Score(string release, bool hash = false) =>
                SubtitleMatching.Score(File, "118c5c0637b40432", new SubtitleCandidate { Release = release, HashMatch = hash });
            Assert.Equal(SubtitleMatch.Hash, Score("anything", hash: true));
            Assert.Equal(SubtitleMatch.Release, Score("His.Dark.Materials.S01E01.1080p.BluRay.x265-RARBG"));
            Assert.Equal(SubtitleMatch.Group, Score("His.Dark.Materials.S01E01.1080p.WEBRip.x265-RARBG"));
            Assert.Equal(SubtitleMatch.Source, Score("His.Dark.Materials.S01E01.720p.BluRay.x264-SHORTBREHD"));
            Assert.Equal(SubtitleMatch.None, Score("His.Dark.Materials.S01E01.HDTV.x264-MTB"));
            Assert.Equal(SubtitleMatch.None, Score(null));
            Assert.Equal(SubtitleMatch.Hash, SubtitleMatching.Score(File, "118c5c0637b40432", new SubtitleCandidate { MovieHash = "118C5C0637B40432" }));
            // The add-on's format field stands in when the name says nothing about the source.
            Assert.Equal(SubtitleMatch.Source, SubtitleMatching.Score(File, null, new SubtitleCandidate { Release = "x-SOMEONE", ReleaseFormat = "BluRay" }));
        }

        [Fact]
        public void OrdersByLanguageThenByMatch()
        {
            var list = new[]
            {
                new SubtitleCandidate { Url = "en-match", Language = "eng", Match = SubtitleMatch.Release },
                new SubtitleCandidate { Url = "es-web", Language = "spa", Match = SubtitleMatch.None },
                new SubtitleCandidate { Url = "fr", Language = "fre", Match = SubtitleMatch.Hash },
                new SubtitleCandidate { Url = "es-bluray", Language = "spa", Match = SubtitleMatch.Source },
                new SubtitleCandidate { Url = "es-other", Language = "spa", Match = SubtitleMatch.None },
            };
            Assert.Equal(new[] { "es-bluray", "es-web", "es-other", "en-match", "fr" }, SubtitleMatching.Order(list, false).Select(s => s.Url).ToArray());
            Assert.Equal("en-match", SubtitleMatching.Order(list, true)[0].Url);   // anime: English first
        }

        [Fact]
        public void TagsSubtitlesForTheMenu()
        {
            Assert.Equal("matches your file", SubtitleMatching.MenuTag(new SubtitleCandidate { Match = SubtitleMatch.Release }));
            Assert.Equal("WEB NTb", SubtitleMatching.MenuTag(new SubtitleCandidate { Release = "Show.S01E01.720p.AMZN.WEB-DL.DDP5.1.H.264-NTb" }));
            Assert.Equal("TV MTB", SubtitleMatching.MenuTag(new SubtitleCandidate { Release = "Show.S01E01.HDTV.x264-MTB" }));
            Assert.Null(SubtitleMatching.MenuTag(new SubtitleCandidate()));
        }

        [Fact]
        public void HashesTheFileLikeOpenSubtitles()
        {
            // Reference value computed independently (Python, struct '<Q' words).
            var head = Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray();
            var tail = Enumerable.Range(0, 65536).Select(i => (byte)(i * 7 % 253)).ToArray();
            Assert.Equal("db8b3defab6d3586", OpenSubtitlesHash.Compute(1_000_000, head, tail));
            Assert.Null(OpenSubtitlesHash.Compute(1000, head, tail));
            Assert.Null(OpenSubtitlesHash.Compute(1_000_000, new byte[10], tail));
        }

        [Fact]
        public void ReadsReleaseFactsFromTheAddonAndSendsTheFileDetails()
        {
            var item = JObject.Parse(@"{""id"":""7962823"",""url"":""https://subs/1"",""lang"":""spa"",""m"":""i"",""g"":""2"",
                ""subtitleFileName"":""x.srt"",""movieReleaseName"":""His.Dark.Materials.S01E01.Lyras.Jordan.720p.AMZN.WEB.DL.DDP5.1.H.264-NTb"",
                ""releaseGroup"":""NTb"",""releaseFormat"":""WEB-DL"",""fpsMilli"":25000}");
            var parsed = SourceResolver.ParseSubtitle(item, "opensubtitles");
            Assert.Equal("https://subs/1", parsed.Url);
            Assert.Equal("spa", parsed.Language);
            Assert.Equal("NTb", parsed.ReleaseGroup);
            Assert.Equal(25000, parsed.FpsMilli);
            Assert.False(parsed.HashMatch);
            Assert.StartsWith("His.Dark.Materials", parsed.Release);
            Assert.True(SourceResolver.ParseSubtitle(JObject.Parse(@"{""url"":""u"",""lang"":""en"",""m"":""h""}"), "a").HashMatch);

            Assert.Equal("videoHash=118c5c0637b40432&videoSize=955512898&filename=His.Dark.Materials.S01E01.1080p.BluRay.x265-RARBG.mp4",
                SourceResolver.SubtitleExtra(new StreamCandidate { FileHash = "118c5c0637b40432", SizeBytes = 955512898, Filename = File }));
            Assert.Equal("filename=a%20b.mkv", SourceResolver.SubtitleExtra(new StreamCandidate { Filename = "a b.mkv" }));
            Assert.Null(SourceResolver.SubtitleExtra(null));
        }
    }
}
