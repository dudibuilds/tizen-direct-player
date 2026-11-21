using Ru7100.Core.Playback;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestFramework("Ru7100.Tests.TestLanguages", "Core.Tests")]

namespace Ru7100.Tests
{
    /// The suite exercises the preferred-subtitle-language paths with Spanish (the app's own default is English).
    /// Set once, before any test runs, so parallel tests never see it change.
    public sealed class TestLanguages : XunitTestFramework
    {
        public TestLanguages(IMessageSink sink) : base(sink) => Languages.Subtitles = "spa";
    }
}
