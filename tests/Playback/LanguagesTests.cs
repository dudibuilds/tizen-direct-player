using Ru7100.Core.Playback;
using Xunit;

namespace Ru7100.Tests.Playback
{
    public class LanguagesTests
    {
        [Theory]
        [InlineData("es-MX", "spa")]
        [InlineData("fr", "fre")]
        [InlineData("German", "ger")]
        [InlineData("pt_BR", "por")]
        [InlineData("ENG", "eng")]
        [InlineData("xx", "xx")]
        public void NormalizesTagsCodesAndNames(string input, string expected) => Assert.Equal(expected, Languages.Normalize(input));

        [Fact]
        public void NamesAndBadges()
        {
            Assert.Equal("Spanish", Languages.Name("es"));
            Assert.Equal("ES", Languages.Short("spa"));
            Assert.Equal("spa", Languages.FromShort("es"));
            Assert.Equal("xx", Languages.Name("xx"));
        }

        [Theory]
        [InlineData("Movie.2020.1080p.WEB.Spanish.Subs-GRP", true)]
        [InlineData("Movie 2020 1080p spa subtitles", true)]
        [InlineData("Movie.2020.1080p.spasub", true)]
        [InlineData("Movie.2020.1080p.Spanish.Audio", false)]
        [InlineData("Movie.2020.1080p.English.Subs", false)]
        public void SubtitlePatternFollowsTheLanguage(string name, bool expected) =>
            Assert.Equal(expected, Languages.SubtitlePattern("spa").IsMatch(name));

        [Theory]
        [InlineData("Français", null)]
        [InlineData("French SDH", "fre")]
        [InlineData("日本語", "jpn")]
        [InlineData("Commentary", null)]
        public void TrackTitles(string title, string expected) => Assert.Equal(expected, Languages.FromTitle(title));
    }
}
