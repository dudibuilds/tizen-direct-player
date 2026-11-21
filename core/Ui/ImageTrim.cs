using System;
using System.IO;
using System.IO.Compression;

namespace Ru7100.Core.Ui
{
    /// Opaque area of a picture, in source pixels.
    public struct PixelRect
    {
        public int X, Y, W, H;
        public PixelRect(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
        public override string ToString() => X + "," + Y + " " + W + "x" + H;
    }

    /// Logos ("Contain" images) come padded: Cinemeta's metahub serves every logo on a fixed 800x310 transparent canvas
    /// with the artwork centred, so a bottom-left fit still leaves a gap under and beside it. Renderers fit the opaque
    /// part of the picture instead. The TV painter reads it from the cached PNG with OpaqueBounds; the browser preview
    /// does the same scan on a canvas (UiHtml.TrimScript). Both place the picture with Fit.
    public static class ImageTrim
    {
        /// Alpha at or below this counts as transparent (soft shadows and anti-aliasing fringes).
        public const int AlphaThreshold = 16;

        /// Opaque bounds of a PNG file, or null when it has no alpha channel, nothing to trim, or a layout this reader
        /// does not handle (interlaced, 16-bit). Never throws.
        public static PixelRect? OpaqueBounds(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                    return OpaqueBounds(stream);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static PixelRect? OpaqueBounds(Stream stream)
        {
            try { return Scan(stream); }
            catch (Exception) { return null; }
        }

        private static PixelRect? Scan(Stream stream)
        {
            var reader = new BinaryReader(stream);
            var signature = reader.ReadBytes(8);
            if (signature.Length != 8 || signature[0] != 0x89 || signature[1] != (byte)'P' || signature[2] != (byte)'N' || signature[3] != (byte)'G')
                return null;
            int width = 0, height = 0, depth = 0, colorType = 0, interlace = 0;
            byte[] paletteAlpha = null;
            var idat = new MemoryStream();
            while (true)
            {
                var lenBytes = reader.ReadBytes(4);
                if (lenBytes.Length < 4) break;
                var length = (lenBytes[0] << 24) | (lenBytes[1] << 16) | (lenBytes[2] << 8) | lenBytes[3];
                var type = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
                var data = reader.ReadBytes(length);
                reader.ReadBytes(4);   // CRC
                if (type == "IHDR")
                {
                    width = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
                    height = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
                    depth = data[8];
                    colorType = data[9];
                    interlace = data[12];
                }
                else if (type == "tRNS")
                    paletteAlpha = data;
                else if (type == "IDAT")
                    idat.Write(data, 0, data.Length);
                else if (type == "IEND")
                    break;
            }
            // RGBA (6), grey + alpha (4) and palette with transparency (3) at 8 bits; anything else is left untrimmed.
            int channels;
            switch (colorType)
            {
                case 6: channels = 4; break;
                case 4: channels = 2; break;
                case 3: channels = 1; break;
                default: return null;
            }
            if (depth != 8 || interlace != 0 || width <= 0 || height <= 0 || (colorType == 3 && paletteAlpha == null))
                return null;

            idat.Position = 2;   // zlib header; DeflateStream reads the raw stream
            var stride = width * channels;
            var previous = new byte[stride];
            var current = new byte[stride];
            int minX = width, minY = height, maxX = -1, maxY = -1;
            using (var inflate = new DeflateStream(idat, CompressionMode.Decompress))
            {
                for (var y = 0; y < height; y++)
                {
                    var filter = inflate.ReadByte();
                    if (filter < 0) return null;
                    ReadFully(inflate, current);
                    Unfilter(filter, current, previous, channels);
                    for (var x = 0; x < width; x++)
                    {
                        int alpha;
                        if (colorType == 3)
                        {
                            var index = current[x];
                            alpha = index < paletteAlpha.Length ? paletteAlpha[index] : 255;
                        }
                        else
                            alpha = current[x * channels + channels - 1];
                        if (alpha <= AlphaThreshold) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        maxY = y;
                    }
                    var swap = previous;
                    previous = current;
                    current = swap;
                }
            }
            if (maxX < 0)
                return null;   // fully transparent: nothing sensible to fit
            var bounds = new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
            return bounds.W == width && bounds.H == height ? (PixelRect?)null : bounds;
        }

        /// Largest logo a row card draws (its box is about 255x80, a little more when focused).
        public const int CardLogoW = 320, CardLogoH = 120;

        /// A copy of a PNG logo cut to its opaque part and scaled down to fit maxW x maxH, written next to the
        /// original; returns its path, or null when the picture is not a PNG this reader handles or is already that
        /// small. Row cards draw their logo about 255 px wide from an 800x310 file: the toolkit decodes the file it is
        /// given on the main loop, so the card gets a file a twelfth of the size. Call it off the main loop. Never throws.
        public static string FittedCopy(string path, int maxW = CardLogoW, int maxH = CardLogoH)
        {
            try
            {
                var target = path + ".fit" + maxW + "x" + maxH + ".png";
                if (File.Exists(target))
                    return target;
                int width, height;
                byte[] rgba;
                using (var stream = File.OpenRead(path))
                    rgba = Decode(stream, out width, out height);
                if (rgba == null)
                    return null;
                int minX = width, minY = height, maxX = -1, maxY = -1;
                for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                        if (rgba[(y * width + x) * 4 + 3] > AlphaThreshold)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            maxY = y;
                        }
                if (maxX < 0)
                    return null;
                int cw = maxX - minX + 1, ch = maxY - minY + 1;
                var scale = Math.Min(1.0, Math.Min((double)maxW / cw, (double)maxH / ch));
                if (scale >= 1 && cw == width && ch == height)
                    return null;   // nothing to gain
                int ow = Math.Max(1, (int)Math.Round(cw * scale)), oh = Math.Max(1, (int)Math.Round(ch * scale));
                var output = new byte[ow * oh * 4];
                // Box filter over premultiplied colour, so transparent pixels do not darken the edges.
                for (var oy = 0; oy < oh; oy++)
                {
                    int y0 = minY + oy * ch / oh, y1 = Math.Max(y0 + 1, minY + (oy + 1) * ch / oh);
                    for (var ox = 0; ox < ow; ox++)
                    {
                        int x0 = minX + ox * cw / ow, x1 = Math.Max(x0 + 1, minX + (ox + 1) * cw / ow);
                        long r = 0, g = 0, b = 0, a = 0;
                        for (var y = y0; y < y1; y++)
                            for (var x = x0; x < x1; x++)
                            {
                                var i = (y * width + x) * 4;
                                int alpha = rgba[i + 3];
                                r += rgba[i] * alpha; g += rgba[i + 1] * alpha; b += rgba[i + 2] * alpha; a += alpha;
                            }
                        var n = (y1 - y0) * (x1 - x0);
                        var o = (oy * ow + ox) * 4;
                        if (a > 0)
                        {
                            output[o] = (byte)(r / a); output[o + 1] = (byte)(g / a); output[o + 2] = (byte)(b / a);
                            output[o + 3] = (byte)(a / n);
                        }
                    }
                }
                var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temp, Png.Encode(ow, oh, output));
                try
                {
                    if (File.Exists(target)) File.Delete(temp); else File.Move(temp, target);
                }
                catch (IOException)
                {
                    try { File.Delete(temp); } catch (IOException) { }
                    if (!File.Exists(target)) return null;
                }
                return target;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// The whole picture as RGBA, for the 8-bit PNG layouts Scan handles (RGBA, grey + alpha, palette); else null.
        private static byte[] Decode(Stream stream, out int width, out int height)
        {
            width = height = 0;
            var reader = new BinaryReader(stream);
            var signature = reader.ReadBytes(8);
            if (signature.Length != 8 || signature[0] != 0x89 || signature[1] != (byte)'P' || signature[2] != (byte)'N' || signature[3] != (byte)'G')
                return null;
            int depth = 0, colorType = 0, interlace = 0;
            byte[] palette = null, paletteAlpha = null;
            var idat = new MemoryStream();
            while (true)
            {
                var lenBytes = reader.ReadBytes(4);
                if (lenBytes.Length < 4) break;
                var length = (lenBytes[0] << 24) | (lenBytes[1] << 16) | (lenBytes[2] << 8) | lenBytes[3];
                var type = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
                var data = reader.ReadBytes(length);
                reader.ReadBytes(4);   // CRC
                if (type == "IHDR")
                {
                    width = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
                    height = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
                    depth = data[8];
                    colorType = data[9];
                    interlace = data[12];
                }
                else if (type == "PLTE") palette = data;
                else if (type == "tRNS") paletteAlpha = data;
                else if (type == "IDAT") idat.Write(data, 0, data.Length);
                else if (type == "IEND") break;
            }
            int channels;
            switch (colorType)
            {
                case 6: channels = 4; break;
                case 4: channels = 2; break;
                case 3: channels = 1; break;
                default: return null;
            }
            if (depth != 8 || interlace != 0 || width <= 0 || height <= 0 || width * (long)height > 4000000 || (colorType == 3 && (palette == null || paletteAlpha == null)))
                return null;
            idat.Position = 2;
            var stride = width * channels;
            var previous = new byte[stride];
            var current = new byte[stride];
            var rgba = new byte[width * height * 4];
            using (var inflate = new DeflateStream(idat, CompressionMode.Decompress))
            {
                for (var y = 0; y < height; y++)
                {
                    var filter = inflate.ReadByte();
                    if (filter < 0) return null;
                    ReadFully(inflate, current);
                    Unfilter(filter, current, previous, channels);
                    var o = y * width * 4;
                    for (var x = 0; x < width; x++, o += 4)
                    {
                        if (colorType == 6)
                        {
                            rgba[o] = current[x * 4]; rgba[o + 1] = current[x * 4 + 1]; rgba[o + 2] = current[x * 4 + 2]; rgba[o + 3] = current[x * 4 + 3];
                        }
                        else if (colorType == 4)
                        {
                            rgba[o] = rgba[o + 1] = rgba[o + 2] = current[x * 2]; rgba[o + 3] = current[x * 2 + 1];
                        }
                        else
                        {
                            var index = current[x];
                            if (index * 3 + 2 >= palette.Length) return null;
                            rgba[o] = palette[index * 3]; rgba[o + 1] = palette[index * 3 + 1]; rgba[o + 2] = palette[index * 3 + 2];
                            rgba[o + 3] = index < paletteAlpha.Length ? paletteAlpha[index] : (byte)255;
                        }
                    }
                    var swap = previous;
                    previous = current;
                    current = swap;
                }
            }
            return rgba;
        }

        private static void ReadFully(Stream stream, byte[] buffer)
        {
            var read = 0;
            while (read < buffer.Length)
            {
                var n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
        }

        private static void Unfilter(int filter, byte[] row, byte[] prior, int bpp)
        {
            for (var i = 0; i < row.Length; i++)
            {
                int a = i >= bpp ? row[i - bpp] : 0, b = prior[i], c = i >= bpp ? prior[i - bpp] : 0;
                switch (filter)
                {
                    case 0: break;
                    case 1: row[i] = (byte)(row[i] + a); break;
                    case 2: row[i] = (byte)(row[i] + b); break;
                    case 3: row[i] = (byte)(row[i] + ((a + b) >> 1)); break;
                    case 4:
                        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        row[i] = (byte)(row[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
                        break;
                    default: throw new InvalidDataException("PNG filter " + filter);
                }
            }
        }

        /// Where to draw the whole picture (image W x H) so its opaque part fits inside the box, at the box's left edge
        /// and its bottom edge (anchorBottom) or vertical centre. The transparent margins fall outside the box; they draw
        /// nothing. Without an opaque rect the whole picture is fitted the same way.
        public static (int X, int Y, int W, int H) Fit(int boxX, int boxY, int boxW, int boxH, int imageW, int imageH, PixelRect? opaque, bool anchorBottom)
        {
            var o = opaque ?? new PixelRect(0, 0, imageW, imageH);
            var scale = Math.Min((double)boxW / o.W, (double)boxH / o.H);
            var w = (int)Math.Round(imageW * scale);
            var h = (int)Math.Round(imageH * scale);
            var x = boxX - (int)Math.Round(o.X * scale);
            var visibleH = o.H * scale;
            var top = anchorBottom ? boxY + boxH - visibleH : boxY + (boxH - visibleH) / 2;
            var y = (int)Math.Round(top - o.Y * scale);
            return (x, y, w, h);
        }
    }
}
