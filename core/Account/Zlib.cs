using System;
using System.IO;
using System.IO.Compression;

namespace Ru7100.Core.Account
{
    /// zlib (RFC 1950) framing around raw DEFLATE, compatible with pako.deflate / pako.inflate.
    /// ZLibStream is not available on the TV's netstandard2.0-era BCL, so the 2-byte header and
    /// Adler-32 trailer are written by hand.
    public static class Zlib
    {
        public static byte[] Compress(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            using (var output = new MemoryStream())
            {
                // CMF 0x78 (deflate, 32K window), FLG 0x9C (default compression, no dict, FCHECK ok).
                output.WriteByte(0x78);
                output.WriteByte(0x9C);
                using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
                {
                    deflate.Write(data, 0, data.Length);
                }
                uint adler = Adler32(data);
                output.WriteByte((byte)(adler >> 24));
                output.WriteByte((byte)(adler >> 16));
                output.WriteByte((byte)(adler >> 8));
                output.WriteByte((byte)adler);
                return output.ToArray();
            }
        }

        public static byte[] Decompress(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length < 6) throw new InvalidDataException("zlib data too short");
            int cmf = data[0];
            int flg = data[1];
            if ((cmf & 0x0F) != 8 || ((cmf << 8) | flg) % 31 != 0) throw new InvalidDataException("invalid zlib header");
            if ((flg & 0x20) != 0) throw new InvalidDataException("zlib preset dictionary not supported");
            byte[] result;
            using (var input = new MemoryStream(data, 2, data.Length - 2))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                deflate.CopyTo(output);
                result = output.ToArray();
            }
            uint expected = ((uint)data[data.Length - 4] << 24) | ((uint)data[data.Length - 3] << 16)
                | ((uint)data[data.Length - 2] << 8) | data[data.Length - 1];
            if (expected != Adler32(result)) throw new InvalidDataException("zlib Adler-32 mismatch");
            return result;
        }

        public static uint Adler32(byte[] data)
        {
            const uint Mod = 65521;
            uint a = 1, b = 0;
            int i = 0;
            while (i < data.Length)
            {
                int n = Math.Min(3800, data.Length - i);
                for (int k = 0; k < n; k++)
                {
                    a += data[i++];
                    b += a;
                }
                a %= Mod;
                b %= Mod;
            }
            return (b << 16) | a;
        }
    }
}
