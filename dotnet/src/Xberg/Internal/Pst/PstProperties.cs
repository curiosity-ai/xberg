// Ported from the `outlook-pst` crate's src/ltp/{prop_type,prop_context}.rs.
//
// A Property Context is a BTree-on-Heap keyed by property id whose 6-byte values are
// (property type, HNID). Where the value lives depends on its type and on what the HNID names:
// small fixed-width values sit inline in the record, larger ones are a heap allocation, and
// anything that outgrew the heap is a subnode of its own.

using System.Text;

namespace Xberg.Internal.Pst;

/// <summary>
/// MAPI property data types, restricted to the set upstream accepts. A property whose type is
/// outside this list is an error there, so it is an error here too rather than a silent skip.
/// </summary>
internal enum PstPropertyType : ushort
{
    Null = 0x0001,
    Integer16 = 0x0002,
    Integer32 = 0x0003,
    Floating32 = 0x0004,
    Floating64 = 0x0005,
    Currency = 0x0006,
    FloatingTime = 0x0007,
    ErrorCode = 0x000A,
    Boolean = 0x000B,
    Integer64 = 0x0014,
    String8 = 0x001E,
    Unicode = 0x001F,
    Time = 0x0040,
    Guid = 0x0048,
    Binary = 0x0102,
    MultipleInteger16 = 0x1002,
    MultipleInteger32 = 0x1003,
    MultipleFloating32 = 0x1004,
    MultipleFloating64 = 0x1005,
    MultipleCurrency = 0x1006,
    MultipleFloatingTime = 0x1007,
    MultipleInteger64 = 0x1014,
    MultipleString8 = 0x101E,
    MultipleUnicode = 0x101F,
    MultipleTime = 0x1040,
    MultipleGuid = 0x1048,
    MultipleBinary = 0x1102,
}

internal static class PstPropertyTypes
{
    private static readonly HashSet<ushort> Known = new(Enum.GetValues<PstPropertyType>().Select(t => (ushort)t));

    public static PstPropertyType Parse(ushort value) =>
        Known.Contains(value) ? (PstPropertyType)value : throw new InvalidDataException($"Unsupported PST property type 0x{value:X4}");

    /// <summary>Types whose value is always a heap (or subnode) allocation, never an inline HNID.</summary>
    public static bool IsAlwaysAllocated(PstPropertyType type) => type switch
    {
        PstPropertyType.Floating64 or PstPropertyType.Currency or PstPropertyType.FloatingTime
            or PstPropertyType.Integer64 or PstPropertyType.Time or PstPropertyType.Guid => true,
        _ => false,
    };

    /// <summary>Types small enough to be stored inline in the 4-byte HNID slot.</summary>
    public static bool IsInline(PstPropertyType type) => type switch
    {
        PstPropertyType.Integer16 or PstPropertyType.Integer32 or PstPropertyType.Floating32
            or PstPropertyType.ErrorCode or PstPropertyType.Boolean => true,
        _ => false,
    };
}

/// <summary>
/// A resolved property value. Numeric types keep their scalar; strings, binaries and the
/// multi-valued types keep the raw bytes they were read from.
/// </summary>
internal sealed class PstValue
{
    public static readonly PstValue Null = new() { Type = PstPropertyType.Null };

    public PstPropertyType Type { get; init; }

    /// <summary>Integer16/32/64, ErrorCode, Currency, Time (a Windows FILETIME) and Boolean (0/1).</summary>
    public long Integer { get; init; }

    public double Real { get; init; }

    public byte[] Bytes { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// The value as text, for the property ids the mail extraction reads. Mirrors upstream's
    /// `prop_value_to_string`: String8 is code-page bytes widened one-for-one, Unicode is
    /// UTF-16LE, a binary value is decoded as UTF-8 with replacement, anything else is not text.
    /// </summary>
    public string? AsString() => Type switch
    {
        PstPropertyType.String8 => new string(Array.ConvertAll(Bytes, b => (char)b)),
        PstPropertyType.Unicode => Encoding.Unicode.GetString(Bytes),
        PstPropertyType.Binary => new UTF8Encoding(false, false).GetString(Bytes),
        _ => null,
    };

    /// <summary>Parse a value of <paramref name="type"/> out of a buffer read from heap or subnode.</summary>
    public static PstValue Read(byte[] data, PstPropertyType type)
    {
        switch (type)
        {
            case PstPropertyType.Floating64:
            case PstPropertyType.FloatingTime:
                return new PstValue { Type = type, Real = BitConverter.Int64BitsToDouble((long)PstFile.ReadU64(data, 0)) };

            case PstPropertyType.Currency:
            case PstPropertyType.Integer64:
            case PstPropertyType.Time:
                return new PstValue { Type = type, Integer = (long)PstFile.ReadU64(data, 0) };

            case PstPropertyType.String8:
            {
                int end = Array.IndexOf(data, (byte)0);
                return new PstValue { Type = type, Bytes = end < 0 ? data : data[..end] };
            }

            case PstPropertyType.Unicode:
            {
                int chars = 0;
                while ((chars + 1) * 2 <= data.Length && PstFile.ReadU16(data, chars * 2) != 0) chars++;
                return new PstValue { Type = type, Bytes = data[..(chars * 2)] };
            }

            default:
                // Guid, Binary and the multi-valued types are consumed as raw bytes.
                return new PstValue { Type = type, Bytes = data };
        }
    }

    /// <summary>Build a value from an HNID slot that holds the value itself rather than a pointer.</summary>
    public static PstValue Inline(PstPropertyType type, uint raw) => type switch
    {
        PstPropertyType.Integer16 => new PstValue { Type = type, Integer = (short)(raw & 0xFFFF) },
        PstPropertyType.Integer32 => new PstValue { Type = type, Integer = (int)raw },
        PstPropertyType.Floating32 => new PstValue { Type = type, Real = BitConverter.Int32BitsToSingle((int)raw) },
        PstPropertyType.ErrorCode => new PstValue { Type = type, Integer = (int)raw },
        PstPropertyType.Boolean => new PstValue { Type = type, Integer = (raw & 0xFF) != 0 ? 1 : 0 },
        _ => throw new InvalidDataException($"PST property type 0x{(ushort)type:X4} cannot be stored inline"),
    };
}

/// <summary>Reads every property of one PC node into a property id -> value map.</summary>
internal static class PstPropertyContext
{
    public static SortedDictionary<ushort, PstValue> Read(PstFile pst, PstNodeEntry node)
    {
        var heap = new PstHeapNode(pst, node.DataBid);
        return Read(pst, node, heap);
    }

    public static SortedDictionary<ushort, PstValue> Read(PstFile pst, PstNodeEntry node, PstHeapNode heap)
    {
        var subNodes = pst.ReadSubNodeTree(node.SubNodeBid);
        var properties = new SortedDictionary<ushort, PstValue>();

        foreach (var entry in PstHeapTree.Entries(heap, heap.UserRoot, keySize: 2, valueSize: 6))
        {
            ushort propertyId = PstFile.ReadU16(entry.Key, 0);
            var type = PstPropertyTypes.Parse(PstFile.ReadU16(entry.Value, 0));
            uint hnid = PstFile.ReadU32(entry.Value, 2);
            properties[propertyId] = Resolve(pst, heap, subNodes, type, hnid);
        }

        return properties;
    }

    private static PstValue Resolve(
        PstFile pst,
        PstHeapNode heap,
        Dictionary<uint, PstSubNodeEntry> subNodes,
        PstPropertyType type,
        uint hnid)
    {
        if (PstPropertyTypes.IsInline(type)) return PstValue.Inline(type, hnid);

        // A PtypNull record carries no value anywhere: upstream treats it as an inline slot it
        // then cannot decode, so a PC that stores one fails to read rather than yielding a
        // half-populated property map.
        if (type == PstPropertyType.Null)
            throw new InvalidDataException("PST property has type PtypNull, which holds no value");

        if (hnid == 0) return PstValue.Null;

        if (PstPropertyTypes.IsAlwaysAllocated(type) || PstNodeType.TypeOf(hnid) == PstNodeType.HeapNode)
            return PstValue.Read(heap.Find(hnid), type);

        if (!subNodes.TryGetValue(hnid, out var subNode))
            throw new InvalidDataException($"PST property subnode 0x{hnid:X} not found");

        return PstValue.Read(pst.ReadNodeData(subNode.DataBid), type);
    }
}
