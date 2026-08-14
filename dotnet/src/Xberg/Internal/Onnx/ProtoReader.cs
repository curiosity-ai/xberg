using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Xberg.Internal.Onnx;

/// <summary>Protobuf wire types, as encoded in the low three bits of a field tag.</summary>
internal enum WireType
{
    Varint = 0,
    Fixed64 = 1,
    LengthDelimited = 2,
    StartGroup = 3,
    EndGroup = 4,
    Fixed32 = 5,
}

/// <summary>
/// A minimal reader for the protobuf wire format, enough to parse ONNX's schema.
/// <para>
/// Hand-written rather than taking a dependency on Google.Protobuf: the wire format is
/// small and stable, and Xberg ships as a portable managed package with no native or
/// code-generation step. The reader is also a <em>view</em> over the model bytes — every
/// nested message and every <c>raw_data</c> blob is a slice, never a copy, which matters
/// when the model is 169 MB and most of it is weights read exactly once.
/// </para>
/// </summary>
internal ref struct ProtoReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public ProtoReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public readonly bool Eof => _pos >= _data.Length;
    public readonly int Position => _pos;

    /// <summary>Read the next field tag, yielding its number and wire type.</summary>
    public bool TryReadTag(out int fieldNumber, out WireType wireType)
    {
        if (Eof)
        {
            fieldNumber = 0;
            wireType = WireType.Varint;
            return false;
        }
        ulong tag = ReadVarint();
        fieldNumber = (int)(tag >> 3);
        wireType = (WireType)(tag & 0x7);
        if (fieldNumber == 0) throw new InvalidDataException("protobuf: field number 0 is not valid");
        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (_pos >= _data.Length) throw new InvalidDataException("protobuf: truncated varint");
            byte b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            // 10 groups of 7 bits covers the full 64-bit range including the sign extension
            // protobuf uses for negative int64.
            if (shift >= 70) throw new InvalidDataException("protobuf: varint longer than 10 bytes");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadInt64() => (long)ReadVarint();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32() => (int)(long)ReadVarint();

    public uint ReadFixed32()
    {
        if (_pos + 4 > _data.Length) throw new InvalidDataException("protobuf: truncated fixed32");
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_pos, 4));
        _pos += 4;
        return v;
    }

    public ulong ReadFixed64()
    {
        if (_pos + 8 > _data.Length) throw new InvalidDataException("protobuf: truncated fixed64");
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(_pos, 8));
        _pos += 8;
        return v;
    }

    public float ReadFloat() => BitConverter.UInt32BitsToSingle(ReadFixed32());

    public double ReadDouble() => BitConverter.UInt64BitsToDouble(ReadFixed64());

    /// <summary>A view over the next length-delimited field. No allocation, no copy.</summary>
    public ReadOnlySpan<byte> ReadBytes()
    {
        int len = checked((int)ReadVarint());
        if (len < 0 || _pos + len > _data.Length) throw new InvalidDataException("protobuf: truncated length-delimited field");
        var slice = _data.Slice(_pos, len);
        _pos += len;
        return slice;
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

    /// <summary>A reader positioned over the next nested message.</summary>
    public ProtoReader ReadMessage() => new(ReadBytes());

    /// <summary>Skip a field whose number this parser does not care about.</summary>
    public void SkipField(WireType wireType)
    {
        switch (wireType)
        {
            case WireType.Varint:
                ReadVarint();
                break;
            case WireType.Fixed64:
                ReadFixed64();
                break;
            case WireType.LengthDelimited:
                ReadBytes();
                break;
            case WireType.Fixed32:
                ReadFixed32();
                break;
            case WireType.StartGroup:
                // Groups are deprecated and absent from the ONNX schema; if one ever appears
                // the safe move is to fail loudly rather than desynchronise the stream.
                throw new InvalidDataException("protobuf: groups are not supported");
            default:
                throw new InvalidDataException($"protobuf: unknown wire type {wireType}");
        }
    }

    /// <summary>
    /// Read a repeated scalar field that may arrive either packed (one length-delimited
    /// run) or unpacked (one tag per element). proto3 writers pack by default, but ONNX
    /// files produced by older exporters carry both forms, so both are accepted.
    /// </summary>
    public void ReadPackedInt64(WireType wireType, List<long> into)
    {
        if (wireType != WireType.LengthDelimited)
        {
            into.Add(ReadInt64());
            return;
        }
        var inner = new ProtoReader(ReadBytes());
        while (!inner.Eof) into.Add(inner.ReadInt64());
    }

    public void ReadPackedFloat(WireType wireType, List<float> into)
    {
        if (wireType != WireType.LengthDelimited)
        {
            into.Add(ReadFloat());
            return;
        }
        var inner = new ProtoReader(ReadBytes());
        while (!inner.Eof) into.Add(inner.ReadFloat());
    }
}
