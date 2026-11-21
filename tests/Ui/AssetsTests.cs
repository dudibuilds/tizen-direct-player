using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Ru7100.Core.Ui;
using Xunit;

namespace Ru7100.Tests.Ui
{
    public class AssetsTests
    {
        private static byte[] Pixels(string path, out int width, out int height)
        {
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes.Take(8).ToArray());
            int Big(int at) => (bytes[at] << 24) | (bytes[at + 1] << 16) | (bytes[at + 2] << 8) | bytes[at + 3];
            width = Big(16);
            height = Big(20);
            var idatLength = Big(33);
            Assert.Equal("IDAT", System.Text.Encoding.ASCII.GetString(bytes, 37, 4));
            using (var z = new ZLibStream(new MemoryStream(bytes, 41, idatLength), CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                z.CopyTo(output);
                return output.ToArray();
            }
        }

        [Fact]
        public void RoundedRectIsTransparentInCornersAndOpaqueInside()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ru7100-assets-" + Guid.NewGuid().ToString("N"));
            try
            {
                var assets = new UiAssets(dir);
                var raw = Pixels(assets.RoundedRect(12), out var w, out var h);
                Assert.Equal(26, w);
                Assert.Equal(26, h);
                byte Alpha(int x, int y) => raw[y * (w * 4 + 1) + 1 + x * 4 + 3];
                Assert.Equal(0, Alpha(0, 0));
                Assert.Equal(255, Alpha(13, 13));
                Assert.Equal(255, Alpha(13, 0));
                var corner = Pixels(assets.CornerMask(12, 0), out var cw, out _);
                Assert.Equal(12, cw);
                Assert.Equal(255, corner[1 + 3]);   // top-left pixel of the TL mask is outside the circle
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        /// Walks every chunk like a strict decoder (libpng on the TV): length, type, CRC-32, then the zlib stream's
        /// header and Adler-32, and the scanline count. Returns the chunk types in order.
        private static string[] ValidatePng(byte[] png)
        {
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.Take(8).ToArray());
            uint Big(int at) => ((uint)png[at] << 24) | ((uint)png[at + 1] << 16) | ((uint)png[at + 2] << 8) | png[at + 3];
            uint Crc(int from, int count)
            {
                var crc = 0xFFFFFFFFu;
                for (var i = from; i < from + count; i++)
                {
                    crc ^= png[i];
                    for (var k = 0; k < 8; k++)
                        crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
                }
                return crc ^ 0xFFFFFFFFu;
            }
            var types = new System.Collections.Generic.List<string>();
            var idat = new MemoryStream();
            int width = 0, height = 0;
            var at = 8;
            while (at < png.Length)
            {
                var length = (int)Big(at);
                var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
                Assert.True(at + 12 + length <= png.Length, "chunk " + type + " runs past the file");
                Assert.Equal(Crc(at + 4, 4 + length), Big(at + 8 + length));
                if (type == "IHDR")
                {
                    Assert.Equal(13, length);
                    width = (int)Big(at + 8);
                    height = (int)Big(at + 12);
                    Assert.Equal(new byte[] { 8, 6, 0, 0, 0 }, png.Skip(at + 16).Take(5).ToArray());   // 8-bit RGBA, no interlace
                }
                if (type == "IDAT")
                    idat.Write(png, at + 8, length);
                types.Add(type);
                at += 12 + length;
            }
            Assert.Equal(png.Length, at);
            var z = idat.ToArray();
            Assert.Equal(0, ((z[0] << 8) | z[1]) % 31);   // zlib header check bits
            Assert.Equal(8, z[0] & 0x0F);                 // deflate
            byte[] raw;
            using (var inflate = new DeflateStream(new MemoryStream(z, 2, z.Length - 6), CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                inflate.CopyTo(output);
                raw = output.ToArray();
            }
            Assert.Equal((width * 4 + 1) * height, raw.Length);
            uint a = 1, b = 0;
            foreach (var d in raw)
            {
                a = (a + d) % 65521;
                b = (b + a) % 65521;
            }
            Assert.Equal((b << 16) | a, ((uint)z[z.Length - 4] << 24) | ((uint)z[z.Length - 3] << 16) | ((uint)z[z.Length - 2] << 8) | z[z.Length - 1]);
            for (var y = 0; y < height; y++)
                Assert.Equal(0, raw[y * (width * 4 + 1)]);   // filter type "none" on every scanline
            return types.ToArray();
        }

        [Fact]
        public void GeneratedShapeImagesAreStrictlyValidPngFiles()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ru7100-assets-" + Guid.NewGuid().ToString("N"));
            try
            {
                var assets = new UiAssets(dir);
                var files = new[]
                {
                    assets.RoundedRect(8), assets.RoundedRect(12), assets.RoundedRect(16), assets.RoundedRect(38),
                    assets.CornerMask(12, 0), assets.CornerMask(12, 1), assets.CornerMask(12, 2), assets.CornerMask(12, 3),
                    assets.Ramp(true, false), assets.Ramp(true, true), assets.Ramp(false, false), assets.Ramp(false, true)
                };
                foreach (var file in files)
                {
                    Assert.EndsWith(".png", file);
                    Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, ValidatePng(File.ReadAllBytes(file)));
                }
                Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void PosterCacheNamesFilesByImageFormat()
        {
            Assert.Equal(".jpg", PosterCache.ExtensionFor(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
            Assert.Equal(".png", PosterCache.ExtensionFor(new byte[] { 0x89, 0x50, 0x4E, 0x47, 13, 10, 26, 10 }));
            Assert.Equal(".webp", PosterCache.ExtensionFor(System.Text.Encoding.ASCII.GetBytes("RIFF0000WEBPVP8 ")));
        }

        [Fact]
        public void LoweringTurnsShapesIntoTintedImages()
        {
            var dir = Path.Combine(Path.GetTempPath(), "ru7100-assets-" + Guid.NewGuid().ToString("N"));
            try
            {
                var frame = new UiFrame();
                frame.Box("pill", 0, 0, 200, 50, "#FFFFFF").R(UiTheme.RadiusPill);
                frame.VerticalGradient("fade", 0, 0, 100, 100, "#141414", 255, 0);
                var poster = frame.Image("poster", 0, 0, 200, 300, "https://img/x.jpg").R(UiTheme.RadiusCard);
                poster.CornerMask = UiTheme.Background;
                var lowered = UiFrameLowering.Lower(frame, new UiAssets(dir));
                var pill = lowered.Nodes.Single(n => n.Key == "pill");
                Assert.Equal(UiNodeKind.Image, pill.Kind);
                Assert.Equal(25, pill.NinePatch);
                var fade = lowered.Nodes.Single(n => n.Key == "fade");
                Assert.True(fade.Stretch);
                Assert.EndsWith("ramp-v-down.png", fade.ImageUrl);
                Assert.Equal("#141414FF", fade.Fill);
                Assert.Equal(4, lowered.Nodes.Count(n => n.Key.StartsWith("poster:corner", StringComparison.Ordinal)));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
