using System;
using System.Buffers.Binary;

namespace OpenRTS.Shared.Serialization
{
    /// <summary>리틀 엔디언 바이너리 쓰기. 내부 버퍼를 재사용하므로 Reset 후 다시 쓴다.</summary>
    public sealed class PacketWriter
    {
        private byte[] _buffer;
        private int _length;

        public PacketWriter(int initialCapacity = 256)
        {
            _buffer = new byte[initialCapacity];
        }

        public byte[] Buffer => _buffer;
        public int Length => _length;

        public void Reset() => _length = 0;

        public byte[] ToArray()
        {
            var result = new byte[_length];
            Array.Copy(_buffer, result, _length);
            return result;
        }

        public void WriteByte(byte value)
        {
            Ensure(1);
            _buffer[_length++] = value;
        }

        public void WriteUShort(ushort value)
        {
            Ensure(2);
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length), value);
            _length += 2;
        }

        public void WriteShort(short value)
        {
            Ensure(2);
            BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(_length), value);
            _length += 2;
        }

        public void WriteInt(int value)
        {
            Ensure(4);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_length), value);
            _length += 4;
        }

        public void WriteUInt(uint value)
        {
            Ensure(4);
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), value);
            _length += 4;
        }

        public void WriteFloat(float value) => WriteInt(BitConverter.SingleToInt32Bits(value));

        private void Ensure(int extra)
        {
            if (_length + extra <= _buffer.Length)
            {
                return;
            }

            var size = _buffer.Length * 2;
            while (size < _length + extra)
            {
                size *= 2;
            }

            Array.Resize(ref _buffer, size);
        }
    }
}
