// Ported from the `outlook-pst` crate's src/ltp/table_context.rs.
//
// A Table Context stores a fixed column layout (TCINFO/TCOLDESC) plus a row matrix: every row is
// the same width, packed 8-byte-aligned values first, then 2-byte, then 1-byte, then a bitmap
// saying which columns the row actually sets. Wide column values are not in the row at all — the
// row holds an HNID naming a heap allocation or a subnode.

namespace Xberg.Internal.Pst;

/// <summary>TCOLDESC: one column's property id/type and where its bytes sit inside a row.</summary>
internal readonly struct PstTableColumn
{
    public PstTableColumn(PstPropertyType type, ushort propertyId, ushort offset, byte size, byte existenceBit)
    {
        Type = type;
        PropertyId = propertyId;
        Offset = offset;
        Size = size;
        ExistenceBit = existenceBit;
    }

    public PstPropertyType Type { get; }
    public ushort PropertyId { get; }
    public ushort Offset { get; }
    public byte Size { get; }
    public byte ExistenceBit { get; }
}

/// <summary>
/// A table context, read whole. Row values are resolved lazily through
/// <see cref="ReadColumn"/> because a wide value may live outside the row matrix.
/// </summary>
internal sealed class PstTableContext
{
    private readonly PstFile _pst;
    private readonly PstHeapNode _heap;
    private readonly Dictionary<uint, PstSubNodeEntry> _subNodes;
    private readonly ushort _end4ByteValues;
    private readonly ushort _end2ByteValues;
    private readonly ushort _end1ByteValues;
    private readonly ushort _rowWidth;

    private PstTableContext(
        PstFile pst,
        PstHeapNode heap,
        Dictionary<uint, PstSubNodeEntry> subNodes,
        ushort end4ByteValues,
        ushort end2ByteValues,
        ushort end1ByteValues,
        ushort rowWidth,
        List<PstTableColumn> columns,
        List<byte[]> rows)
    {
        _pst = pst;
        _heap = heap;
        _subNodes = subNodes;
        _end4ByteValues = end4ByteValues;
        _end2ByteValues = end2ByteValues;
        _end1ByteValues = end1ByteValues;
        _rowWidth = rowWidth;
        Columns = columns;
        Rows = rows;
    }

    public List<PstTableColumn> Columns { get; }

    /// <summary>The raw row matrix, one buffer per row, in stored order.</summary>
    public List<byte[]> Rows { get; }

    /// <summary>`dwRowID` — for a hierarchy or contents table this is the child folder/message node id.</summary>
    public static uint RowId(byte[] row) => PstFile.ReadU32(row, 0);

    public static PstTableContext Read(PstFile pst, PstNodeEntry node)
    {
        var heap = new PstHeapNode(pst, node.DataBid);
        var subNodes = pst.ReadSubNodeTree(node.SubNodeBid);

        var info = heap.Find(heap.UserRoot);
        if (info.Length < 22) throw new InvalidDataException("PST TCINFO is truncated");
        if (info[0] != PstHeapNodeType.Table) throw new InvalidDataException("PST TCINFO has the wrong bType");

        int columnCount = info[1];
        ushort end4 = PstFile.ReadU16(info, 2);
        ushort end2 = PstFile.ReadU16(info, 4);
        ushort end1 = PstFile.ReadU16(info, 6);
        ushort endBitmap = PstFile.ReadU16(info, 8);
        uint rowsHnid = PstFile.ReadU32(info, 14);

        if (endBitmap == 0) throw new InvalidDataException("PST TCINFO declares a zero-width row");

        var columns = new List<PstTableColumn>(columnCount);
        for (int i = 0; i < columnCount; i++)
        {
            int at = 22 + i * 8;
            columns.Add(new PstTableColumn(
                PstPropertyTypes.Parse(PstFile.ReadU16(info, at)),
                PstFile.ReadU16(info, at + 2),
                PstFile.ReadU16(info, at + 4),
                info[at + 6],
                info[at + 7]));
        }

        var rows = new List<byte[]>();
        foreach (var buffer in ReadRowMatrix(pst, heap, subNodes, rowsHnid))
        {
            // Rows never straddle a block boundary, so each buffer holds a whole number of them.
            int count = buffer.Length / endBitmap;
            for (int i = 0; i < count; i++)
                rows.Add(buffer[(i * endBitmap)..((i + 1) * endBitmap)]);
        }

        return new PstTableContext(pst, heap, subNodes, end4, end2, end1, endBitmap, columns, rows);
    }

    private static List<byte[]> ReadRowMatrix(
        PstFile pst,
        PstHeapNode heap,
        Dictionary<uint, PstSubNodeEntry> subNodes,
        uint rowsHnid)
    {
        if (rowsHnid == 0) return new List<byte[]>();

        if (PstNodeType.TypeOf(rowsHnid) == PstNodeType.HeapNode)
            return new List<byte[]> { heap.Find(rowsHnid) };

        if (!subNodes.TryGetValue(rowsHnid, out var subNode))
            throw new InvalidDataException($"PST table row matrix subnode 0x{rowsHnid:X} not found");

        return pst.ReadDataTree(subNode.DataBid);
    }

    /// <summary>
    /// The value of one column in one row, or null when the row's existence bitmap says the
    /// column is unset.
    /// </summary>
    public PstValue? ReadColumn(byte[] row, PstTableColumn column)
    {
        int bit = column.ExistenceBit;
        int bitmapAt = _end1ByteValues + (bit / 8);
        if (bitmapAt >= _rowWidth || bitmapAt >= row.Length) return null;
        if ((row[bitmapAt] & (1 << (7 - (bit % 8)))) == 0) return null;

        switch (column.Type)
        {
            case PstPropertyType.Integer16:
                return new PstValue { Type = column.Type, Integer = (short)PstFile.ReadU16(row, column.Offset) };

            // The first two 4-byte slots are the row id and its version stamp, exposed as ordinary
            // Integer32 columns.
            case PstPropertyType.Integer32:
                return new PstValue { Type = column.Type, Integer = (int)PstFile.ReadU32(row, column.Offset) };

            case PstPropertyType.ErrorCode:
                return new PstValue { Type = column.Type, Integer = (int)PstFile.ReadU32(row, column.Offset) };

            case PstPropertyType.Floating32:
                return new PstValue { Type = column.Type, Real = BitConverter.Int32BitsToSingle((int)PstFile.ReadU32(row, column.Offset)) };

            case PstPropertyType.Floating64:
            case PstPropertyType.FloatingTime:
                return new PstValue { Type = column.Type, Real = BitConverter.Int64BitsToDouble((long)PstFile.ReadU64(row, column.Offset)) };

            case PstPropertyType.Currency:
            case PstPropertyType.Integer64:
            case PstPropertyType.Time:
                return new PstValue { Type = column.Type, Integer = (long)PstFile.ReadU64(row, column.Offset) };

            case PstPropertyType.Boolean:
            {
                byte value = row[column.Offset];
                if (value > 1) throw new InvalidDataException($"PST table boolean column holds {value}");
                return new PstValue { Type = column.Type, Integer = value };
            }

            default:
            {
                // Everything wider than 8 bytes is stored out of line and named by an HNID.
                uint hnid = PstFile.ReadU32(row, column.Offset);

                // An out-of-line column with a zero HNID names nothing; the column is simply
                // not set on this row.
                if (hnid == 0) return null;

                if (PstNodeType.TypeOf(hnid) == PstNodeType.HeapNode)
                    return PstValue.Read(_heap.Find(hnid), column.Type);

                if (!_subNodes.TryGetValue(hnid, out var subNode))
                    throw new InvalidDataException($"PST table column subnode 0x{hnid:X} not found");

                return PstValue.Read(_pst.ReadNodeData(subNode.DataBid), column.Type);
            }
        }
    }

    /// <summary>Every set column of one row, keyed by property id.</summary>
    public Dictionary<ushort, PstValue> ReadRow(byte[] row)
    {
        var values = new Dictionary<ushort, PstValue>();
        foreach (var column in Columns)
        {
            PstValue? value;
            try
            {
                value = ReadColumn(row, column);
            }
            catch (InvalidDataException)
            {
                // A single unreadable column must not lose the rest of the row.
                continue;
            }
            if (value is not null) values[column.PropertyId] = value;
        }
        return values;
    }
}
