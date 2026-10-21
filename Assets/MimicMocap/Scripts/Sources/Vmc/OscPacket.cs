using System;
using System.Text;

namespace MimicMocap.Vmc
{
    /// <summary>
    /// Minimal OSC 1.0 decoder for VMC traffic: nested bundles, int32/float32/string/blob arguments.
    /// Parsing allocates nothing; callers read arguments straight out of the datagram buffer.
    /// </summary>
    public sealed class OscPacket
    {
        /// <summary>One decoded message: address and argument offsets into the source buffer.</summary>
        public struct Message
        {
            public byte[] Data;
            public int AddressStart, AddressLength;
            public int ArgCount;
            public int[] ArgOffsets;
            public char[] ArgTypes;

            public bool AddressIs(string address)
            {
                if (address.Length != AddressLength) return false;
                for (var i = 0; i < AddressLength; i++)
                    if (Data[AddressStart + i] != (byte)address[i]) return false;
                return true;
            }

            public string Address => Encoding.ASCII.GetString(Data, AddressStart, AddressLength);

            public char TypeOf(int i) => i < ArgCount ? ArgTypes[i] : '\0';

            public float GetFloat(int i)
            {
                if (i >= ArgCount) return 0f;
                var o = ArgOffsets[i];
                switch (ArgTypes[i])
                {
                    case 'f': return ReadFloat(Data, o);
                    case 'i': return ReadInt(Data, o);
                    case 'd': return (float)BitConverter.Int64BitsToDouble(ReadLong(Data, o));
                    default: return 0f;
                }
            }

            public int GetInt(int i)
            {
                if (i >= ArgCount) return 0;
                var o = ArgOffsets[i];
                switch (ArgTypes[i])
                {
                    case 'i': return ReadInt(Data, o);
                    case 'f': return (int)ReadFloat(Data, o);
                    case 'T': return 1;
                    case 'F': return 0;
                    default: return 0;
                }
            }

            /// <summary>Byte range of a string argument (without the terminator).</summary>
            public bool GetString(int i, out int start, out int length)
            {
                start = 0; length = 0;
                if (i >= ArgCount || ArgTypes[i] != 's') return false;
                start = ArgOffsets[i];
                var end = start;
                while (end < Data.Length && Data[end] != 0) end++;
                length = end - start;
                return true;
            }

            public string GetString(int i) => GetString(i, out var s, out var l) ? Encoding.UTF8.GetString(Data, s, l) : null;

            /// <summary>FNV-1a hash of a string argument, for allocation-free name lookups.</summary>
            public uint GetStringHash(int i)
            {
                if (!GetString(i, out var s, out var l)) return 0;
                return Hash(Data, s, l);
            }
        }

        public const int MaxArgs = 32;
        readonly int[] m_Offsets = new int[MaxArgs];
        readonly char[] m_Types = new char[MaxArgs];

        /// <summary>Messages decoded from the last packet.</summary>
        public int MessageCount { get; private set; }

        /// <summary>Decode one datagram, invoking <paramref name="onMessage"/> for each message inside it.</summary>
        public void Parse(byte[] data, int length, Action<Message> onMessage)
        {
            MessageCount = 0;
            ParseElement(data, 0, Math.Min(length, data.Length), onMessage, 0);
        }

        void ParseElement(byte[] data, int start, int end, Action<Message> onMessage, int depth)
        {
            if (depth > 8 || end - start < 4) return;
            if (IsBundle(data, start, end))
            {
                var pos = start + 16; // "#bundle\0" + 64-bit time tag
                while (pos + 4 <= end)
                {
                    var size = ReadInt(data, pos);
                    pos += 4;
                    if (size < 0 || pos + size > end) break;
                    ParseElement(data, pos, pos + size, onMessage, depth + 1);
                    pos += size;
                }
                return;
            }

            if (data[start] != (byte)'/') return;
            var msg = new Message { Data = data, AddressStart = start, ArgOffsets = m_Offsets, ArgTypes = m_Types };
            var p = start;
            while (p < end && data[p] != 0) p++;
            msg.AddressLength = p - start;
            p = Align4(p + 1, start);
            if (p >= end || data[p] != (byte)',')
            {
                // Message without a type tag string: no arguments.
                MessageCount++;
                onMessage(msg);
                return;
            }
            var tagStart = p + 1;
            var tagEnd = tagStart;
            while (tagEnd < end && data[tagEnd] != 0) tagEnd++;
            p = Align4(tagEnd + 1, start);

            var n = 0;
            for (var t = tagStart; t < tagEnd && n < MaxArgs; t++)
            {
                var type = (char)data[t];
                m_Types[n] = type;
                m_Offsets[n] = p;
                var stop = false;
                switch (type)
                {
                    case 'i': case 'f': case 'c': case 'r': case 'm': p += 4; break;
                    case 'h': case 't': case 'd': p += 8; break;
                    case 's': case 'S':
                        {
                            var e = p;
                            while (e < end && data[e] != 0) e++;
                            p = Align4(e + 1, start);
                            break;
                        }
                    case 'b':
                        {
                            if (p + 4 > end) { stop = true; break; }
                            var size = ReadInt(data, p);
                            p = Align4(p + 4 + Math.Max(0, size), start);
                            break;
                        }
                    case 'T': case 'F': case 'N': case 'I': break;
                    default: stop = true; break; // unknown tag: stop here
                }
                if (stop || p > end) break;
                n++;
            }
            msg.ArgCount = n;
            MessageCount++;
            onMessage(msg);
        }

        static bool IsBundle(byte[] d, int s, int e)
        {
            return e - s >= 16 && d[s] == (byte)'#' && d[s + 1] == (byte)'b' && d[s + 2] == (byte)'u' && d[s + 3] == (byte)'n'
                && d[s + 4] == (byte)'d' && d[s + 5] == (byte)'l' && d[s + 6] == (byte)'e' && d[s + 7] == 0;
        }

        static int Align4(int p, int origin)
        {
            var rel = p - origin;
            var pad = (4 - (rel & 3)) & 3;
            return p + pad;
        }

        public static int ReadInt(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];

        public static long ReadLong(byte[] d, int o) => ((long)ReadInt(d, o) << 32) | (uint)ReadInt(d, o + 4);

        public static float ReadFloat(byte[] d, int o) => BitConverter.Int32BitsToSingle(ReadInt(d, o));

        public static uint Hash(byte[] d, int start, int length)
        {
            uint h = 2166136261;
            for (var i = 0; i < length; i++) { h ^= d[start + i]; h *= 16777619; }
            return h;
        }

        public static uint Hash(string s)
        {
            uint h = 2166136261;
            for (var i = 0; i < s.Length; i++) { h ^= (byte)s[i]; h *= 16777619; }
            return h;
        }
    }

    /// <summary>Tiny OSC encoder, enough to build VMC bundles for tests and tools.</summary>
    public sealed class OscWriter
    {
        readonly System.IO.MemoryStream m_Stream = new System.IO.MemoryStream();

        public byte[] ToArray() => m_Stream.ToArray();

        /// <summary>Encode one message. Arguments may be float, int, string, double or bool.</summary>
        public static byte[] Message(string address, params object[] args)
        {
            var w = new OscWriter();
            w.WriteString(address);
            var tags = new StringBuilder(",");
            foreach (var a in args)
            {
                switch (a)
                {
                    case float: tags.Append('f'); break;
                    case int: tags.Append('i'); break;
                    case string: tags.Append('s'); break;
                    case double: tags.Append('d'); break;
                    case bool b: tags.Append(b ? 'T' : 'F'); break;
                    default: throw new ArgumentException("unsupported OSC argument type " + a?.GetType());
                }
            }
            w.WriteString(tags.ToString());
            foreach (var a in args)
            {
                switch (a)
                {
                    case float f: w.WriteInt(BitConverter.SingleToInt32Bits(f)); break;
                    case int i: w.WriteInt(i); break;
                    case string s: w.WriteString(s); break;
                    case double d: w.WriteLong(BitConverter.DoubleToInt64Bits(d)); break;
                }
            }
            return w.ToArray();
        }

        /// <summary>Wrap encoded messages in a bundle with an immediate time tag.</summary>
        public static byte[] Bundle(params byte[][] messages)
        {
            var w = new OscWriter();
            w.WriteString("#bundle");
            w.WriteLong(1);
            foreach (var m in messages)
            {
                w.WriteInt(m.Length);
                w.m_Stream.Write(m, 0, m.Length);
            }
            return w.ToArray();
        }

        void WriteString(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            m_Stream.Write(bytes, 0, bytes.Length);
            var pad = 4 - (bytes.Length & 3);
            for (var i = 0; i < pad; i++) m_Stream.WriteByte(0);
        }

        void WriteInt(int v)
        {
            m_Stream.WriteByte((byte)(v >> 24));
            m_Stream.WriteByte((byte)(v >> 16));
            m_Stream.WriteByte((byte)(v >> 8));
            m_Stream.WriteByte((byte)v);
        }

        void WriteLong(long v)
        {
            WriteInt((int)(v >> 32));
            WriteInt((int)v);
        }
    }
}
