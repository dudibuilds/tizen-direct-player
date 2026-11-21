using System;
using System.Threading;
using System.Threading.Tasks;
using Ru7100.Core.Addons;
using Ru7100.Core.Playback;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Core.Tests.Addons
{
    /// Runs alone, after the parallel tests: it switches the preferred subtitle language back to the app's English
    /// default (the rest of the suite runs with Spanish).
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class EnglishDefaultCollection { public const string Name = "English default"; }

    [Collection(EnglishDefaultCollection.Name)]
    public sealed class EnglishDefaultTests : IDisposable
    {
        private readonly string saved = Languages.Subtitles;

        public EnglishDefaultTests() => Languages.Subtitles = "eng";

        public void Dispose() => Languages.Subtitles = saved;

        [Fact]
        public void EnglishOnlyOrderAndTiers()
        {
            Assert.False(Languages.HasPreferredSubtitles);
            Assert.Equal(new[] { "eng" }, TrackRules.DefaultSubtitleOrder(false));
            Assert.Equal(new[] { "eng", "eng" }, new[] { PlayerSettings.DefaultFor(false).AudioLanguage, PlayerSettings.DefaultFor(false).SubtitleLanguage });
            Assert.Equal(6, StreamRanking.DetectGeneralLanguages("Movie.2020.1080p.WEB-DL.ENG.Eng.Subs").Tier);
            Assert.Equal(4, StreamRanking.DetectGeneralLanguages("Movie.2020.1080p.WEB-DL.ENG.SpaSub").Tier);
            Assert.Equal("English subtitles", BrowseViewModel.SubtitleOption);
        }

        [Fact]
        public void EnglishSubtitlesAreTheTopTierForTheEarlyReturn()
        {
            var http = Fixtures.Http(new FixtureHandler());
            var clock = new FakeClock();
            var resolver = new SourceResolver(new AddonRegistry(new MemoryStore(), http, null, clock.Func), http, clock.Func);
            Assert.True(resolver.IsViable(new[] { Fixtures.Torrent("Movie.2020.1080p.x264.ENG.Eng.Subs\n👤 900") }, MediaType.Movie, new SourceOptions(), null));
            Assert.False(resolver.IsViable(new[] { Fixtures.Torrent("Movie.2020.1080p.x264.ENG\n👤 900") }, MediaType.Movie, new SourceOptions(), null));
        }
    }
}
