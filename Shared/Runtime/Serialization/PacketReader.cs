using System;
using System.Buffers.Binary;

namespace OpenRTS.Shared.Serialization
{
    /// <summary>리틀 엔디언 바이너리 읽기. 범위를 벗어나면 <see cref="PacketFormatException"/>.</summary>
    public struct PacketReader
    {
        private readonly byte[] _buffer;
        private readonly int _end;
        private int _position;

        public PacketReader(byte[] buffer, int offset, int length)
        {
            if (offset < 0 || length < 0 || offset + length > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            _buffer = buffer;
            _position = offset;
            _end = offset + length;
        }

        public PacketReader(byte[] buffer) : this(buffer, 0, buffer.Length)
        {
        }

        public int Remaining => _end - _position;

        public byte ReadByte()
        {
            Require(1);
            return _buffer[_position++];
        }

        public ushort ReadUShort()
        {
            Require(2);
            var v = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_position));
            _position += 2;
            return v;
        }

        public short ReadShort()
        {
            Require(2);
            var v = BinaryPrimitives.ReadInt16LittleEndian(_buffer.AsSpan(_position));
            _position += 2;
            return v;
        }

        public int ReadInt()
        {
            Require(4);
            var v = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_position));
            _position += 4;
            return v;
        }

        public uint ReadUInt()
        {
            Require(4);
            var v = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_position));
            _position += 4;
            return v;
        }

        public float ReadFloat() => BitConverter.Int32BitsToSingle(ReadInt());

        private void Require(int count)
        {
            if (_position + count > _end)
            {
                throw new PacketFormatException($"Packet truncated: need {count} bytes, {Remaining} left.");
            }
        }
    }

    public sealed class PacketFormatException : Exception
    {
        public PacketFormatException(string message) : base(message)
        {
        }
    }
}
