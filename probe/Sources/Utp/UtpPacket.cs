using System;

namespace Ru7100Probe.Utp
{
    /// uTP (BEP 29) wire constants.
    internal static class UtpWire
    {
        public const int HeaderSize = 20;
        public const int Version = 1;

        public const int TypeData = 0;
        public const int TypeFin = 1;
        public const int TypeState = 2;
        public const int TypeReset = 3;
        public const int TypeSyn = 4;

        public const int ExtensionNone = 0;
        public const int ExtensionSelectiveAck = 1;

        /// Cheap first-byte test used by the UDP demultiplexer: version 1 in the low nibble, a known type in the high
        /// nibble, and room for a header. A bencoded DHT message starts with 'd' (0x64: low nibble 4), so never matches.
        public static bool LooksLikeUtp(byte[] buffer, int offset, int count) =>
            count >= HeaderSize && (buffer[offset] & 0x0F) == Version && (buffer[offset] >> 4) <= TypeSyn;

        public static string TypeName(int type)
        {
            switch (type)
            {
                case TypeData: return "DATA";
                case TypeFin: return "FIN";
                case TypeState: return "STATE";
                case TypeReset: return "RESET";
                case TypeSyn: return "SYN";
                default: return "?" + type;
            }
        }
    }

    /// A decoded uTP header. Extensions are located, not copied: SelectiveAckOffset / Length point into the datagram.
    internal struct UtpHeader
    {
        public int Type;
        public int FirstExtension;
        public ushort ConnectionId;
        public uint TimestampMicros;
        public uint TimestampDifferenceMicros;
        public uint WindowSize;
        public ushort SeqNr;
        public ushort AckNr;

        /// Offset of the payload in the datagram buffer (after the header and every extension).
        public int PayloadOffset;
        public int PayloadLength;
        /// Selective ACK bitmask location in the datagram buffer; length 0 when absent.
        public int SelectiveAckOffset;
        public int SelectiveAckLength;

        /// Parses a datagram. Returns false (never throws) for anything that is not a well-formed uTP v1 packet:
        /// the port is public, so truncated headers and broken extension chains are expected input.
        public static bool TryParse(byte[] buffer, int offset, int count, out UtpHeader header)
        {
            header = default;
            if (buffer == null || offset < 0 || count < UtpWire.HeaderSize || offset + count > buffer.Length)
                return false;
            var first = buffer[offset];
            if ((first & 0x0F) != UtpWire.Version)
                return false;
            var type = first >> 4;
            if (type > UtpWire.TypeSyn)
                return false;
            header.Type = type;
            header.FirstExtension = buffer[offset + 1];
            header.ConnectionId = ReadUInt16(buffer, offset + 2);
            header.TimestampMicros = ReadUInt32(buffer, offset + 4);
            header.TimestampDifferenceMicros = ReadUInt32(buffer, offset + 8);
            header.WindowSize = ReadUInt32(buffer, offset + 12);
            header.SeqNr = ReadUInt16(buffer, offset + 16);
            header.AckNr = ReadUInt16(buffer, offset + 18);

            var end = offset + count;
            var position = offset + UtpWire.HeaderSize;
            var extension = header.FirstExtension;
            var guard = 0;
            while (extension != UtpWire.ExtensionNone)
            {
                if (++guard > 16 || position + 2 > end)
                    return false;
                var next = buffer[position];
                var length = buffer[position + 1];
                position += 2;
                if (position + length > end)
                    return false;
                if (extension == UtpWire.ExtensionSelectiveAck && length > 0 && header.SelectiveAckLength == 0)
                {
                    header.SelectiveAckOffset = position;
                    header.SelectiveAckLength = length;
                }
                position += length;
                extension = next;
            }
            header.PayloadOffset = position;
            header.PayloadLength = end - position;
            return true;
        }

        /// Writes the 20-byte fixed header (the extension byte as given; extension bodies are the caller's).
        public static void Write(byte[] buffer, int offset, int type, int firstExtension, ushort connectionId, uint timestamp,
            uint timestampDifference, uint windowSize, ushort seqNr, ushort ackNr)
        {
            buffer[offset] = (byte)((type << 4) | UtpWire.Version);
            buffer[offset + 1] = (byte)firstExtension;
            WriteUInt16(buffer, offset + 2, connectionId);
            WriteUInt32(buffer, offset + 4, timestamp);
            WriteUInt32(buffer, offset + 8, timestampDifference);
            WriteUInt32(buffer, offset + 12, windowSize);
            WriteUInt16(buffer, offset + 16, seqNr);
            WriteUInt16(buffer, offset + 18, ackNr);
        }

        /// Selective ACK bit i covers sequence number ack_nr + 2 + i; bytes in order, least significant bit first
        /// (libutp: mask[i >> 3] & (1 << (i & 7))).
        public static bool IsSelectivelyAcked(byte[] buffer, in UtpHeader header, int bit) =>
            bit >= 0 && bit < header.SelectiveAckLength * 8 && (buffer[header.SelectiveAckOffset + (bit >> 3)] & (1 << (bit & 7))) != 0;

        public static ushort ReadUInt16(byte[] b, int o) => (ushort)((b[o] << 8) | b[o + 1]);
        public static uint ReadUInt32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

        public static void WriteUInt16(byte[] b, int o, ushort v)
        {
            b[o] = (byte)(v >> 8);
            b[o + 1] = (byte)v;
        }

        public static void WriteUInt32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        public override string ToString() =>
            $"{UtpWire.TypeName(Type)} conn={ConnectionId} seq={SeqNr} ack={AckNr} wnd={WindowSize} ts={TimestampMicros} tdiff={TimestampDifferenceMicros} payload={PayloadLength}" +
            (SelectiveAckLength > 0 ? $" sack={SelectiveAckLength}B" : "");
    }

    /// 16-bit sequence arithmetic. Distances are taken modulo 2^16; "before" means within half the space behind.
    internal static class Seq
    {
        public static int Distance(ushort from, ushort to) => (ushort)(to - from);
        public static bool Before(ushort a, ushort b) => (short)(a - b) < 0;
    }

    /// Fixed-size byte arrays for packets, shared by every connection of a context. Bounded: buffers beyond the
    /// retained count go to the GC, so a burst cannot pin memory forever.
    internal sealed class UtpBufferPool
    {
        public const int BufferSize = 2048;
        private readonly byte[][] items;
        private int count;

        public UtpBufferPool(int retained = 1024) => items = new byte[retained][];

        public byte[] Rent()
        {
            lock (items)
            {
                if (count > 0)
                {
                    var buffer = items[--count];
                    items[count] = null;
                    return buffer;
                }
            }
            return new byte[BufferSize];
        }

        public void Return(byte[] buffer)
        {
            if (buffer == null || buffer.Length != BufferSize)
                return;
            lock (items)
            {
                if (count < items.Length)
                    items[count++] = buffer;
            }
        }
    }
}
