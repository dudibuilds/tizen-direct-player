using System;
using System.Linq;
using System.Globalization;
using System.IO;
using System.IO.Compression;

namespace Ru7100.Core.Ui
{
    /// White alpha images for painters without border-radius or gradients (ElmSharp): a 9-patch rounded rect per
    /// radius, corner masks (white outside the quarter circle) and gradient ramps. Painters tint them with the
    /// node colour, so one file serves every colour. Generated on first use into a cache directory (a few KB).
    public sealed class UiAssets
    {
        /// Bump when the generated images change, so painters keep them in a fresh folder.
        public const int Version = 3;

        private readonly string directory;

        public UiAssets(string directory)
        {
            this.directory = directory;
            Directory.CreateDirectory(directory);
        }

        /// 9-patch: (2r+2)² image, stretch borders = r on every side.
        public string RoundedRect(int radius) => Ensure("round-" + radius + ".png", () =>
        {
            var size = radius * 2 + 2;
            return Render(size, size, (x, y) => CoverageInsideRoundRect(x, y, size, size, radius));
        });

        /// Corner mask r×r: opaque outside the quarter circle, transparent inside. corner: 0 TL, 1 TR, 2 BR, 3 BL.
        public string CornerMask(int radius, int corner) => Ensure("corner-" + radius + "-" + corner + ".png", () =>
            Render(radius, radius, (x, y) =>
            {
                // Distance from the circle centre placed at the inner corner of this quadrant.
                double cx = corner == 0 || corner == 3 ? radius : 0;
                double cy = corner == 0 || corner == 1 ? radius : 0;
                return 1 - Coverage(x, y, cx, cy, radius);
            }));

        /// 1×256 (vertical) or 256×1 ramp, alpha 0 → 255 (or 255 → 0 when descending).
        public string Ramp(bool vertical, bool descending) => Ensure("ramp-" + (vertical ? "v" : "h") + (descending ? "-down" : "-up") + ".png", () =>
            Render(vertical ? 1 : 256, vertical ? 256 : 1, (x, y) =>
            {
                var t = (vertical ? y : x) / 255.0;
                return descending ? 1 - t : t;
            }));

        /// A glyph (UiGlyphs) as a white alpha square, `size` px a side (default UiGlyphs.RasterSize). Painters stretch and tint it.
        public string Glyph(string name, int size = 0)
        {
            var px = size > 0 ? size : UiGlyphs.RasterSize(name);
            return Ensure("glyph-" + name + "-" + px + ".png", () => Render(px, px, (x, y) => UiGlyphs.Coverage(name, x, y, px)));
        }

        /// Whether the glyph's file exists already (no generation): the launch intro waits for the logo parts so the
        /// UI thread never rasterises them in the middle of the animation.
        public bool HasGlyph(string name)
        {
            var file = "glyph-" + name + "-" + UiGlyphs.RasterSize(name) + ".png";
            lock (ensured)
                if (ensured.ContainsKey(file))
                    return true;
            return File.Exists(Path.Combine(directory, file));
        }

        /// 9-patch soft shadow for a rounded box: opaque under the box, fading to nothing over `spread` px.
        /// Stretch borders = radius + spread on every side; the node is the box grown by `spread`.
        public string Shadow(int radius, int spread) => Ensure("shadow-" + radius + "-" + spread + ".png", () =>
        {
            var border = radius + spread;
            var size = border * 2 + 2;
            return Render(size, size, (x, y) =>
            {
                // Distance from the pixel centre to the rounded box inset by `spread`.
                double px = x + 0.5, py = y + 0.5;
                double cx = Math.Max(border, Math.Min(px, size - border)), cy = Math.Max(border, Math.Min(py, size - border));
                var d = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy)) - radius;
                return UiGlyphs.ShadowAlpha(d, spread);
            });
        });

        /// Generates everything the UI is known to use (every glyph, the card and panel shadows, the common corner
        /// radii and the gradient ramps), so the first focus ring, pop-out card or sheet never pays for an image in
        /// the middle of navigation. Safe to call from a background thread at startup.
        public void Prewarm()
        {
            foreach (var name in UiGlyphs.Names.Where(UiGlyphs.IsLogo).Concat(UiGlyphs.Names.Where(n => !UiGlyphs.IsLogo(n))))
                Glyph(name);
            foreach (var radius in new[] { 2, 3, 5, 8, UiTheme.RadiusRow, UiTheme.RadiusCard, UiTheme.RadiusPanel, UiTheme.RadiusCard + UiScale.FocusRing, 10, 14, 18, 22, 32, 33, 36, 40, 44 })   // 33: the pop card's 66 px buttons
                RoundedRect(radius);
            Shadow(0, 440);   // the launch intro's glow (LaunchIntro), otherwise generated inside its first frame
            Shadow(UiTheme.RadiusCard, UiTheme.ShadowSpread);
            Shadow(UiTheme.RadiusPanel, UiTheme.ShadowSpread);
            Shadow(44, 32);   // the toast
            for (var corner = 0; corner < 4; corner++)
                CornerMask(UiTheme.RadiusCard, corner);
            Ramp(true, true); Ramp(true, false); Ramp(false, true); Ramp(false, false);
        }

        /// Lowered forms of the nodes this painter host was handed, kept while the source node lives (UiFrameLowering).
        internal readonly System.Collections.Generic.Dictionary<string, LoweredNode> Lowered = new System.Collections.Generic.Dictionary<string, LoweredNode>();

        internal struct LoweredNode
        {
            public UiNode Source;
            public UiNode[] Result;
        }

        private readonly System.Collections.Generic.Dictionary<string, string> ensured = new System.Collections.Generic.Dictionary<string, string>();

        /// The file for `name`, generated on first use. Later calls are one dictionary lookup: lowering asks for
        /// these on every frame, and a file-system check per shape per frame is not free on the TV.
        private string Ensure(string name, Func<byte[]> make)
        {
            lock (ensured)
                if (ensured.TryGetValue(name, out var known))
                    return known;
            var made = EnsureFile(name, make);
            lock (ensured)
                ensured[name] = made;
            return made;
        }

        private string EnsureFile(string name, Func<byte[]> make)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
            {
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, make());
                // The launch prewarm and the UI thread may make the same file at the same moment: the first one in
                // wins, the other drops its copy.
                try
                {
                    if (File.Exists(path)) File.Delete(temp); else File.Move(temp, path);
                }
                catch (IOException)
                {
                    try { File.Delete(temp); } catch (IOException) { }
                    if (!File.Exists(path)) throw;
                }
            }
            return path;
        }

        private static double CoverageInsideRoundRect(int x, int y, int w, int h, int r)
        {
            double cx = x < r ? r : x >= w - r ? w - r : x + 0.5;
            double cy = y < r ? r : y >= h - r ? h - r : y + 0.5;
            return Coverage(x, y, cx, cy, r);
        }

        /// Fraction of pixel (x, y) inside the circle (cx, cy, r), 4×4 supersampled.
        private static double Coverage(int x, int y, double cx, double cy, double r)
        {
            var inside = 0;
            for (var sy = 0; sy < 4; sy++)
                for (var sx = 0; sx < 4; sx++)
                {
                    var px = x + (sx + 0.5) / 4 - cx;
                    var py = y + (sy + 0.5) / 4 - cy;
                    if (px * px + py * py <= r * r) inside++;
                }
            return inside / 16.0;
        }

        private static byte[] Render(int w, int h, Func<int, int, double> alpha)
        {
            var rgba = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 4;
                    rgba[i] = rgba[i + 1] = rgba[i + 2] = 255;
                    rgba[i + 3] = (byte)Math.Round(255 * Math.Max(0, Math.Min(1, alpha(x, y))));
                }
            return Png.Encode(w, h, rgba);
        }
    }

    /// Minimal PNG encoder (RGBA8, no filtering).
    public static class Png
    {
        private static readonly uint[] CrcTable = MakeCrcTable();

        public static byte[] Encode(int width, int height, byte[] rgba)
        {
            using (var output = new MemoryStream())
            {
                output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                var header = new byte[13];
                WriteBig(header, 0, (uint)width);
                WriteBig(header, 4, (uint)height);
                header[8] = 8;   // bit depth
                header[9] = 6;   // RGBA
                Chunk(output, "IHDR", header);
                var raw = new byte[(width * 4 + 1) * height];
                for (var y = 0; y < height; y++)
                    Buffer.BlockCopy(rgba, y * width * 4, raw, y * (width * 4 + 1) + 1, width * 4);
                Chunk(output, "IDAT", Zlib(raw));
                Chunk(output, "IEND", new byte[0]);
                return output.ToArray();
            }
        }

        private static byte[] Zlib(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                output.WriteByte(0x78);
                output.WriteByte(0x9C);
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                    deflate.Write(data, 0, data.Length);
                uint a = 1, b = 0;
                foreach (var d in data)
                {
                    a = (a + d) % 65521;
                    b = (b + a) % 65521;
                }
                var adler = new byte[4];
                WriteBig(adler, 0, (b << 16) | a);
                output.Write(adler, 0, 4);
                return output.ToArray();
            }
        }

        private static void Chunk(Stream output, string type, byte[] data)
        {
            var length = new byte[4];
            WriteBig(length, 0, (uint)data.Length);
            output.Write(length, 0, 4);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            output.Write(typeBytes, 0, 4);
            output.Write(data, 0, data.Length);
            var crc = 0xFFFFFFFFu;
            foreach (var x in typeBytes) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
            foreach (var x in data) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
            var crcBytes = new byte[4];
            WriteBig(crcBytes, 0, crc ^ 0xFFFFFFFFu);
            output.Write(crcBytes, 0, 4);
        }

        private static void WriteBig(byte[] buffer, int at, uint value)
        {
            buffer[at] = (byte)(value >> 24);
            buffer[at + 1] = (byte)(value >> 16);
            buffer[at + 2] = (byte)(value >> 8);
            buffer[at + 3] = (byte)value;
        }

        private static uint[] MakeCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
    }

    /// Rewrites rounded boxes, gradients and rounded images into plain image nodes for painters that only have
    /// rectangles and images (ElmSharp): tinted 9-patch images, stretched ramps and corner masks.
    public static class UiFrameLowering
    {
        // A node at rest reaches lowering as the same object frame after frame (UiAnimator reuses it), so its lowered
        // form is remembered and handed to the painter again: no allocation, and the painter sees nothing to do.
        private static readonly UiNode[] Same = new UiNode[0];

        public static UiFrame Lower(UiFrame frame, UiAssets assets)
        {
            var output = new UiFrame { Background = frame.Background, ScreenKey = frame.ScreenKey };
            output.Nodes.Capacity = frame.Nodes.Count + 8;
            foreach (var n in frame.Nodes)
            {
                // One slot per node key: a hit only while the source is the very same object (a node at rest).
                UiNode[] result;
                if (n.Key == null)
                    result = LowerNode(n, assets);
                else if (assets.Lowered.TryGetValue(n.Key, out var slot) && ReferenceEquals(slot.Source, n))
                    result = slot.Result;
                else
                {
                    result = LowerNode(n, assets);
                    assets.Lowered[n.Key] = new UiAssets.LoweredNode { Source = n, Result = result };
                }
                if (result == Same)
                    output.Nodes.Add(n);
                else
                    output.Nodes.AddRange(result);
            }
            // Keys that left the screen are dropped now and then, so the table follows what is on screen.
            if (assets.Lowered.Count > 4 * frame.Nodes.Count + 256)
                assets.Lowered.Clear();
            return output;
        }

        private static UiNode[] LowerNode(UiNode n, UiAssets assets)
        {
            var r = n.EffectiveRadius;
            if (n.Kind == UiNodeKind.Box && r > 0)
            {
                var box = n.Clone();
                box.Kind = UiNodeKind.Image;
                box.ImageUrl = "file://" + assets.RoundedRect(r);
                box.NinePatch = r;
                return new[] { box };
            }
            if (n.Kind == UiNodeKind.Image && UiGlyphs.IsGlyph(n.ImageUrl, out var glyph))
            {
                var icon = n.Clone();
                icon.ImageUrl = "file://" + assets.Glyph(glyph);
                icon.Stretch = true;
                return new[] { icon };
            }
            if (n.Kind == UiNodeKind.Image && UiGlyphs.IsShadow(n.ImageUrl, out var shadowRadius, out var shadowSpread))
            {
                var shadow = n.Clone();
                shadow.ImageUrl = "file://" + assets.Shadow(shadowRadius, shadowSpread);
                shadow.NinePatch = shadowRadius + shadowSpread;
                return new[] { shadow };
            }
            if (n.Kind == UiNodeKind.Gradient)
            {
                var from = Alpha(n.Fill);
                var to = Alpha(n.Color);
                var g = n.Clone();
                g.Kind = UiNodeKind.Image;
                g.ImageUrl = "file://" + assets.Ramp(n.Vertical, from > to);
                g.Fill = (n.Fill ?? "#000000").Substring(0, 7) + Math.Max(from, to).ToString("X2", CultureInfo.InvariantCulture);
                g.Stretch = true;
                return new[] { g };
            }
            if (n.Kind == UiNodeKind.Image && r > 0 && n.CornerMask != null)
            {
                var nodes = new UiNode[5];
                nodes[0] = n;
                for (var c = 0; c < 4; c++)
                {
                    nodes[c + 1] = new UiNode
                    {
                        Key = n.Key + ":corner" + c, Kind = UiNodeKind.Image, ImageUrl = "file://" + assets.CornerMask(r, c),
                        X = c == 0 || c == 3 ? n.X : n.X + n.W - r, Y = c == 0 || c == 1 ? n.Y : n.Y + n.H - r, W = r, H = r,
                        Fill = n.CornerMask, Stretch = true, Opacity = n.Opacity, Enter = UiEnter.None
                    };
                }
                return nodes;
            }
            return Same;
        }

        private static int Alpha(string color) =>
            color != null && color.Length == 9 ? int.Parse(color.Substring(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 255;
    }
}
