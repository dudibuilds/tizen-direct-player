using Ru7100.Core;
using Xunit;

public class ContractsTests
{
    [Fact]
    public void TorrentIdentityMatchesStartupDedupeKey()
    {
        var candidate = new StreamCandidate { InfoHash = "ABCDEF0123456789ABCDEF0123456789ABCDEF01" };
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01:auto", candidate.Identity);
        candidate.FileIndex = 2;
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef01:2", candidate.Identity);
    }
}
