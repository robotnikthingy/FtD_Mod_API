using System;
using System.IO;
using System.Text;
using BrilliantSkies.Core.Types;
using UnityEngine;

namespace FtdModApi
{
    /// <summary>
    /// Builds the contents of a ModNet message. Write values in an order, and read them back in the same order with
    /// <see cref="ModNetReader"/>.
    /// </summary>
    public sealed class ModNetWriter
    {
        private readonly MemoryStream _stream = new MemoryStream();
        private readonly BinaryWriter _w;

        public ModNetWriter()
        {
            _w = new BinaryWriter(_stream, Encoding.UTF8);
        }

        /// <summary>Bytes written so far.</summary>
        public int Length => (int)_stream.Length;

        public ModNetWriter Write(bool v) { _w.Write(v); return this; }
        public ModNetWriter Write(byte v) { _w.Write(v); return this; }
        public ModNetWriter Write(int v) { _w.Write(v); return this; }
        public ModNetWriter Write(uint v) { _w.Write(v); return this; }
        public ModNetWriter Write(long v) { _w.Write(v); return this; }
        public ModNetWriter Write(ulong v) { _w.Write(v); return this; }
        public ModNetWriter Write(float v) { _w.Write(v); return this; }
        public ModNetWriter Write(double v) { _w.Write(v); return this; }

        /// <summary>Writes text (null is written as an empty string).</summary>
        public ModNetWriter Write(string v)
        {
            var bytes = Encoding.UTF8.GetBytes(v ?? string.Empty);
            _w.Write(bytes.Length);
            _w.Write(bytes);
            return this;
        }

        /// <summary>Writes a byte array (null is written as empty).</summary>
        public ModNetWriter Write(byte[] v)
        {
            v = v ?? Array.Empty<byte>();
            _w.Write(v.Length);
            _w.Write(v);
            return this;
        }

        public ModNetWriter Write(Guid v) { _w.Write(v.ToByteArray()); return this; }

        public ModNetWriter Write(Vector3 v) { _w.Write(v.x); _w.Write(v.y); _w.Write(v.z); return this; }

        public ModNetWriter Write(Vector3d v) { _w.Write(v.x); _w.Write(v.y); _w.Write(v.z); return this; }

        public ModNetWriter Write(Quaternion v) { _w.Write(v.x); _w.Write(v.y); _w.Write(v.z); _w.Write(v.w); return this; }

        public ModNetWriter Write(Color v) { _w.Write(v.r); _w.Write(v.g); _w.Write(v.b); _w.Write(v.a); return this; }

        /// <summary>The message contents.</summary>
        public byte[] ToArray()
        {
            _w.Flush();
            return _stream.ToArray();
        }
    }

    /// <summary>Thrown when a message is shorter than what is read from it, or otherwise malformed.</summary>
    public sealed class ModNetFormatException : Exception
    {
        public ModNetFormatException(string message) : base(message)
        {
        }
    }

    /// <summary>Reads the contents of a received ModNet message, in the order it was written.</summary>
    public sealed class ModNetReader
    {
        private readonly byte[] _data;
        private int _pos;

        public ModNetReader(byte[] data)
        {
            _data = data ?? Array.Empty<byte>();
        }

        /// <summary>Bytes not read yet.</summary>
        public int Remaining => _data.Length - _pos;

        /// <summary>The whole message.</summary>
        public byte[] Bytes => _data;

        private int Take(int n)
        {
            if (n < 0 || n > _data.Length - _pos)
                throw new ModNetFormatException($"The message is shorter than expected (wanted {n} more bytes at {_pos}, it has {_data.Length}).");
            int at = _pos;
            _pos += n;
            return at;
        }

        public bool ReadBool() => _data[Take(1)] != 0;
        public byte ReadByte() => _data[Take(1)];
        public int ReadInt() => BitConverter.ToInt32(_data, Take(4));
        public uint ReadUInt() => BitConverter.ToUInt32(_data, Take(4));
        public long ReadLong() => BitConverter.ToInt64(_data, Take(8));
        public ulong ReadULong() => BitConverter.ToUInt64(_data, Take(8));
        public float ReadFloat() => BitConverter.ToSingle(_data, Take(4));
        public double ReadDouble() => BitConverter.ToDouble(_data, Take(8));

        public string ReadString()
        {
            int n = ReadInt();
            return Encoding.UTF8.GetString(_data, Take(n), n);
        }

        public byte[] ReadBytes()
        {
            int n = ReadInt();
            int at = Take(n);
            var b = new byte[n];
            Buffer.BlockCopy(_data, at, b, 0, n);
            return b;
        }

        public Guid ReadGuid()
        {
            var b = new byte[16];
            Buffer.BlockCopy(_data, Take(16), b, 0, 16);
            return new Guid(b);
        }

        public Vector3 ReadVector3() => new Vector3(ReadFloat(), ReadFloat(), ReadFloat());

        public Vector3d ReadVector3d() => new Vector3d(ReadDouble(), ReadDouble(), ReadDouble());

        public Quaternion ReadQuaternion() => new Quaternion(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());

        public Color ReadColor() => new Color(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());
    }
}
