using System;
using System.Linq;
using System.Text;
using Ru7100.Core.Account;
using Xunit;

namespace Ru7100.Core.Tests.Account
{
    /// Golden vectors generated with node against
    /// samsung-stremio-spotify-mcp/node_modules/stremio-watched-bitfield 1.1.0 (pako deflate).
    public class WatchedBitFieldTests
    {
        static string[] Ids(int n, string prefix = "tt1:1:") => Enumerable.Range(1, n).Select(i => prefix + i).ToArray();
        static string Bits(WatchedBitField wb, int n) => string.Concat(Enumerable.Range(0, n).Select(i => wb.Get(i) ? '1' : '0'));

        // constructFromArray([1,0,1,1,0,0,0,1,1], ids(9)).serialize()
        const string FromArray9 = "tt1:1:9:9:eJzrZQQAAR0Ajw==";

        [Theory]
        [InlineData("tt1:1:9:9:eJzrZQQAAR0Ajw==", 9, "101100011")]
        [InlineData("tt1:1:3:3:eJxjAAAAAQAB", 3, "000")]
        [InlineData("tt1:1:20:20:eJzznMQCAAIGAOA=", 20, "10010010010010010010")]
        [InlineData("tt1:1:9:9:eJzrYwQAAR8AkA==", 9, "011100011")]
        public void DecodesNodeVectors(string serialized, int count, string bits)
        {
            var wb = WatchedBitField.ConstructAndResize(serialized, Ids(count));
            Assert.Equal(bits, Bits(wb, count));
        }

        [Fact]
        public void DecodesLargeVectorWithColonInId()
        {
            var ids = Ids(300, "tt0903747:");
            var wb = WatchedBitField.ConstructAndResize("tt0903747:300:300:eJzjUGwRcOIgQLAAAJA8Byc=", ids);
            for (int i = 0; i < 300; i++) Assert.Equal((i * 7) % 5 == 1, wb.Get(i));
        }

        [Fact]
        public void GrowKeepsBitsAndSerializesLikeNode()
        {
            // grow12: same bytes (2 bytes still hold 12 bits) → identical packed payload to node.
            var g = WatchedBitField.ConstructAndResize(FromArray9, Ids(12));
            Assert.Equal("101100011000", Bits(g, 12));
            Assert.StartsWith("tt1:1:12:12:", g.Serialize());
            var g17 = WatchedBitField.ConstructAndResize(FromArray9, Ids(17));
            Assert.Equal("10110001100000000", Bits(g17, 17));
            Assert.Equal(3, g17.Bitfield.Values.Length);
            Assert.StartsWith("tt1:1:17:17:", g17.Serialize());
        }

        [Fact]
        public void ShiftWhenLeadingVideosDisappear()
        {
            // node: shift2 bits 110001100, serialize "tt1:1:11:9:eJxLZgAAAMgAZA=="
            var ids = Ids(11).Skip(2).ToArray();
            var sh = WatchedBitField.ConstructAndResize(FromArray9, ids);
            Assert.Equal("110001100", Bits(sh, 9));
            Assert.StartsWith("tt1:1:11:9:", sh.Serialize());
            var nodeBytes = Zlib.Decompress(Convert.FromBase64String("eJxLZgAAAMgAZA=="));
            Assert.Equal(nodeBytes, sh.Bitfield.Values);
        }

        [Fact]
        public void UnknownLastIdOrUnshiftStartsBlank()
        {
            var unknown = WatchedBitField.ConstructAndResize(FromArray9, Ids(5, "tt2:1:"));
            Assert.Equal("00000", Bits(unknown, 5));
            Assert.StartsWith("tt2:1:5:5:", unknown.Serialize());

            var unshiftIds = new[] { "tt1:0:1", "tt1:0:2" }.Concat(Ids(9)).ToArray();
            var unshift = WatchedBitField.ConstructAndResize(FromArray9, unshiftIds);
            Assert.Equal("00000000000", Bits(unshift, 11));
            Assert.StartsWith("tt1:1:9:11:", unshift.Serialize());

            // stored 12, now 9: last id missing → blank (node: "tt1:1:9:9:eJxjYAAAAAIAAQ==")
            var shrink = WatchedBitField.ConstructAndResize("tt1:1:12:12:eJzjZwAAACAAEA==", Ids(9));
            Assert.Equal("000000000", Bits(shrink, 9));
        }

        [Fact]
        public void SerializeRoundTripsAndMatchesNodeForSameBytes()
        {
            var wb = WatchedBitField.ConstructFromArray(new[] { true, false, true, true, false, false, false, true, true }, Ids(9));
            var s = wb.Serialize();
            Assert.StartsWith("tt1:1:9:9:", s);
            // Header is standard zlib like pako; payload inflates to node's bytes.
            var packed = Convert.FromBase64String(s.Substring("tt1:1:9:9:".Length));
            Assert.Equal(0x78, packed[0]);
            Assert.Equal(Zlib.Decompress(Convert.FromBase64String("eJzrZQQAAR0Ajw==")), Zlib.Decompress(packed));
            // On .NET's zlib the output is even byte-identical to pako's (verified with node).
            Assert.Equal(FromArray9, s);
            var back = WatchedBitField.ConstructAndResize(s, Ids(9));
            Assert.Equal("101100011", Bits(back, 9));
        }

        [Fact]
        public void SetVideoAndGetVideo()
        {
            var wb = WatchedBitField.ConstructAndResize(FromArray9, Ids(9));
            wb.SetVideo("tt1:1:2", true);
            wb.SetVideo("tt1:1:1", false);
            wb.SetVideo("nope", true);
            Assert.Equal("011100011", Bits(wb, 9));
            Assert.True(wb.GetVideo("tt1:1:2"));
            Assert.False(wb.GetVideo("nope"));
        }

        [Fact]
        public void MalformedInputThrowsAndRulesFallBackToBlank()
        {
            Assert.ThrowsAny<Exception>(() => WatchedBitField.ConstructAndResize("garbage", Ids(3)));
            Assert.ThrowsAny<Exception>(() => WatchedBitField.ConstructAndResize("tt1:1:3:3:bm90emxpYg==", Ids(3)));
            var meta = Metas.Series("tt1", "Show", (1, 3));
            var state = LibraryRules.EpisodeState(meta, "tt1:1:3:3:bm90emxpYg==");
            Assert.False(state.GetVideo("tt1:1:1"));
        }

        [Fact]
        public void ZlibAdlerAndRoundTrip()
        {
            Assert.Equal(0x11E60398u, Zlib.Adler32(Encoding.ASCII.GetBytes("Wikipedia")));
            var data = Enumerable.Range(0, 5000).Select(i => (byte)(i * 31)).ToArray();
            Assert.Equal(data, Zlib.Decompress(Zlib.Compress(data)));
        }
    }
}
