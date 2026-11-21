using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Ru7100.Core.Account
{
    /// Port of BitField8 from npm stremio-watched-bitfield 1.1.0 (lib/bitfield8.js).
    /// Bit i lives in byte i>>3 at mask 1<<(i%8). Out-of-range reads return false and
    /// out-of-range writes are ignored, like a JS Uint8Array.
    public sealed class BitField8
    {
        public BitField8(int length)
        {
            Length = length;
            Values = new byte[(length + 7) / 8];
        }

        BitField8(byte[] values, int length)
        {
            Values = values;
            Length = length;
        }

        public int Length { get; }
        public byte[] Values { get; private set; }

        public static BitField8 FromPacked(byte[] compressed, int? length)
        {
            var values = Zlib.Decompress(compressed);
            int len = length ?? values.Length * 8;
            int nBytes = (len + 7) / 8;
            if (nBytes > values.Length)
            {
                var grown = new byte[nBytes];
                Buffer.BlockCopy(values, 0, grown, 0, values.Length);
                values = grown;
            }
            return new BitField8(values, len);
        }

        public bool Get(int i)
        {
            if (i < 0) return false;
            int index = i / 8;
            if (index >= Values.Length) return false;
            return (Values[index] & (1 << (i % 8))) != 0;
        }

        public void Set(int i, bool value)
        {
            if (i < 0) return;
            int index = i / 8;
            if (index >= Values.Length) return;
            int mask = 1 << (i % 8);
            if (value) Values[index] = (byte)(Values[index] | mask);
            else Values[index] = (byte)(Values[index] & ~mask);
        }

        public byte[] ToPacked() => Zlib.Compress(Values);
    }

    /// Port of watchedBitfield from npm stremio-watched-bitfield 1.1.0 (lib/watchedBitfield.js).
    /// Serialized form: "{lastVideoId}:{bitLength}:{base64(zlib(bytes))}". Video ids are the ids
    /// of the sorted meta videos (season, episode, released).
    public sealed class WatchedBitField
    {
        readonly List<string> videoIds;

        WatchedBitField(BitField8 bitfield, IReadOnlyList<string> videoIds)
        {
            Bitfield = bitfield;
            this.videoIds = videoIds.ToList();
        }

        public BitField8 Bitfield { get; }
        public IReadOnlyList<string> VideoIds => videoIds;

        public static WatchedBitField ConstructFromArray(IReadOnlyList<bool> values, IReadOnlyList<string> videoIds)
        {
            var bitfield = new BitField8(videoIds.Count);
            for (int i = 0; i < values.Count; i++) bitfield.Set(i, values[i]);
            return new WatchedBitField(bitfield, videoIds);
        }

        public static WatchedBitField Empty(IReadOnlyList<string> videoIds) => ConstructFromArray(Array.Empty<bool>(), videoIds);

        /// Throws FormatException (or InvalidDataException) on malformed input, like the JS original.
        public static WatchedBitField ConstructAndResize(string serialized, IReadOnlyList<string> videoIds)
        {
            if (serialized == null) throw new FormatException("invalid components length");
            var components = serialized.Split(':').ToList();
            if (components.Count < 3) throw new FormatException("invalid components length");

            string serializedBuf = components[components.Count - 1];
            components.RemoveAt(components.Count - 1);
            int? lastLength = ParseIntLikeJs(components[components.Count - 1]);
            components.RemoveAt(components.Count - 1);
            string lastVideoId = string.Join(":", components);
            int lastVideoIdx = IndexOf(videoIds, lastVideoId);

            // JS: offset is NaN when lastLength is NaN; NaN comparisons are false.
            int? offset = lastLength.HasValue ? (lastLength.Value - 1) - lastVideoIdx : (int?)null;
            bool unshiftable = lastVideoIdx == -1 || (offset.HasValue && offset.Value < 0);
            bool shiftable = offset.HasValue && offset.Value > 0;

            if (unshiftable || shiftable)
            {
                var resized = new WatchedBitField(new BitField8(videoIds.Count), videoIds);
                if (unshiftable) return resized;

                var buf = BitField8.FromPacked(DecodeBase64(serializedBuf), lastLength);
                for (int i = offset.Value; i != buf.Length; i++)
                {
                    resized.Set(i - offset.Value, buf.Get(i));
                }
                return resized;
            }

            var plain = BitField8.FromPacked(DecodeBase64(serializedBuf), videoIds.Count);
            return new WatchedBitField(plain, videoIds);
        }

        public bool Get(int idx) => Bitfield.Get(idx);
        public void Set(int idx, bool value) => Bitfield.Set(idx, value);

        public void SetVideo(string videoId, bool value)
        {
            int idx = IndexOf(videoIds, videoId);
            if (idx == -1) return;
            Bitfield.Set(idx, value);
        }

        public bool GetVideo(string videoId)
        {
            int idx = IndexOf(videoIds, videoId);
            if (idx == -1) return false;
            return Bitfield.Get(idx);
        }

        public string Serialize()
        {
            // JS reads videoIds[length-1]; an empty list yields "undefined".
            string last = videoIds.Count > 0 ? videoIds[videoIds.Count - 1] ?? "null" : "undefined";
            var sb = new StringBuilder();
            sb.Append(last).Append(':').Append(Bitfield.Length.ToString(CultureInfo.InvariantCulture)).Append(':');
            sb.Append(Convert.ToBase64String(Bitfield.ToPacked()));
            return sb.ToString();
        }

        /// atob (npm) is lenient about missing padding; Convert.FromBase64String is not.
        static byte[] DecodeBase64(string value)
        {
            var trimmed = value.Trim();
            int pad = (4 - trimmed.Length % 4) % 4;
            if (pad != 0 && pad != 3) trimmed += new string('=', pad);
            return Convert.FromBase64String(trimmed);
        }

        static int IndexOf(IReadOnlyList<string> ids, string id)
        {
            for (int i = 0; i < ids.Count; i++) if (string.Equals(ids[i], id, StringComparison.Ordinal)) return i;
            return -1;
        }

        /// parseInt(s, 10): leading whitespace, optional sign, leading digits; NaN when none.
        static int? ParseIntLikeJs(string s)
        {
            if (s == null) return null;
            int i = 0;
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            bool negative = false;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) { negative = s[i] == '-'; i++; }
            int start = i;
            long value = 0;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9' && value < int.MaxValue)
            {
                value = value * 10 + (s[i] - '0');
                i++;
            }
            if (i == start) return null;
            if (value > int.MaxValue) value = int.MaxValue;
            return negative ? -(int)value : (int)value;
        }
    }
}
