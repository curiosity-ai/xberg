// Ported from the `outlook-pst` crate's src/messaging/{store,folder,message}.rs.
//
// The messaging layer names things by Entry ID (the store's 16-byte record key plus a node id)
// and gives back Property Contexts and Table Contexts: a store with its IPM sub-tree pointer, a
// folder with its hierarchy and contents tables, and a message with its recipient and attachment
// tables.

namespace Xberg.Internal.Pst;

/// <summary>An Entry ID: which store, and which node inside it.</summary>
internal readonly struct PstEntryId
{
    public PstEntryId(byte[] recordKey, uint nodeId)
    {
        RecordKey = recordKey;
        NodeId = nodeId;
    }

    /// <summary>The owning store's `PR_STORE_RECORD_KEY` (16 bytes).</summary>
    public byte[] RecordKey { get; }

    public uint NodeId { get; }

    /// <summary>Parse the on-disk form: flags, then the record key, then the node id.</summary>
    public static PstEntryId Parse(byte[] data)
    {
        if (data.Length < 24) throw new InvalidDataException("PST entry id is too short");
        if (PstFile.ReadU32(data, 0) != 0) throw new InvalidDataException("PST entry id has non-zero flags");
        return new PstEntryId(data[4..20], PstFile.ReadU32(data, 20));
    }

    /// <summary>The on-disk form, as folders expose it under `PR_ENTRYID`.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[24];
        Array.Copy(RecordKey, 0, bytes, 4, 16);
        bytes[20] = (byte)NodeId;
        bytes[21] = (byte)(NodeId >> 8);
        bytes[22] = (byte)(NodeId >> 16);
        bytes[23] = (byte)(NodeId >> 24);
        return bytes;
    }
}

/// <summary>The message store node (`NID_MESSAGE_STORE`) and the objects reachable from it.</summary>
internal sealed class PstStore
{
    private const ushort PropertyDisplayName = 0x3001;
    private const ushort PropertyRecordKey = 0x0FF9;
    private const ushort PropertyIpmSubTreeEntryId = 0x35E0;

    private readonly PstFile _pst;

    private PstStore(PstFile pst, SortedDictionary<ushort, PstValue> properties)
    {
        _pst = pst;
        Properties = properties;
    }

    public SortedDictionary<ushort, PstValue> Properties { get; }

    public static PstStore Open(ReadOnlySpan<byte> content)
    {
        var pst = PstFile.Open(content);
        var node = pst.GetNode(PstNodeType.MessageStoreNid);
        return new PstStore(pst, PstPropertyContext.Read(pst, node));
    }

    public byte[] RecordKey =>
        Properties.TryGetValue(PropertyRecordKey, out var value) && value.Type == PstPropertyType.Binary && value.Bytes.Length == 16
            ? value.Bytes
            : throw new InvalidDataException("PST store has no usable record key");

    public string? DisplayName => Properties.TryGetValue(PropertyDisplayName, out var value) ? value.AsString() : null;

    /// <summary>`PR_IPM_SUBTREE_ENTRYID`: the root of the mail folder tree.</summary>
    public PstEntryId IpmSubTreeEntryId()
    {
        if (!Properties.TryGetValue(PropertyIpmSubTreeEntryId, out var value) || value.Type != PstPropertyType.Binary)
            throw new InvalidDataException("PST store has no IPM sub-tree entry id");
        return PstEntryId.Parse(value.Bytes);
    }

    /// <summary>Build an entry id for a node in this store, as `StoreProperties::make_entry_id` does.</summary>
    public PstEntryId MakeEntryId(uint nodeId) => new(RecordKey, nodeId);

    public PstFolder OpenFolder(PstEntryId entryId)
    {
        uint type = PstNodeType.TypeOf(entryId.NodeId);
        if (type != PstNodeType.NormalFolder && type != PstNodeType.SearchFolder)
            throw new InvalidDataException($"PST entry id 0x{entryId.NodeId:X} does not name a folder");
        if (!RecordKey.AsSpan().SequenceEqual(entryId.RecordKey))
            throw new InvalidDataException("PST entry id belongs to a different store");

        return new PstFolder(_pst, entryId);
    }

    public PstMessage OpenMessage(PstEntryId entryId)
    {
        uint type = PstNodeType.TypeOf(entryId.NodeId);
        if (type != PstNodeType.NormalMessage && type != PstNodeType.AssociatedMessage)
            throw new InvalidDataException($"PST entry id 0x{entryId.NodeId:X} does not name a message");
        if (!RecordKey.AsSpan().SequenceEqual(entryId.RecordKey))
            throw new InvalidDataException("PST entry id belongs to a different store");

        return new PstMessage(_pst, _pst.GetNode(entryId.NodeId));
    }
}

/// <summary>A folder: its own properties, plus the two tables that list what it holds.</summary>
internal sealed class PstFolder
{
    private const ushort PropertyDisplayName = 0x3001;
    private const ushort PropertyEntryId = 0x0FFF;
    private const ushort PropertyFolderType = 0x3601;

    private readonly PstFile _pst;
    private readonly uint _nodeId;

    internal PstFolder(PstFile pst, PstEntryId entryId)
    {
        _pst = pst;
        _nodeId = entryId.NodeId;

        var properties = PstPropertyContext.Read(pst, pst.GetNode(entryId.NodeId));

        // The store synthesizes these two: the folder's own entry id, and whether it is the
        // root (0), a normal folder (1) or a search folder (2).
        properties[PropertyEntryId] = new PstValue { Type = PstPropertyType.Binary, Bytes = entryId.ToBytes() };
        properties[PropertyFolderType] = new PstValue
        {
            Type = PstPropertyType.Integer32,
            Integer = entryId.NodeId == PstNodeType.RootFolderNid
                ? 0
                : PstNodeType.TypeOf(entryId.NodeId) == PstNodeType.SearchFolder ? 2 : 1,
        };

        Properties = properties;
    }

    public SortedDictionary<ushort, PstValue> Properties { get; }

    public string? DisplayName => Properties.TryGetValue(PropertyDisplayName, out var value) ? value.AsString() : null;

    /// <summary>The subfolder list, or null when this folder has no hierarchy table node.</summary>
    public PstTableContext? HierarchyTable() => ReadTable(PstNodeType.HierarchyTable);

    /// <summary>The message list, or null when this folder has no contents table node.</summary>
    public PstTableContext? ContentsTable() => ReadTable(PstNodeType.ContentsTable);

    private PstTableContext? ReadTable(uint tableType)
    {
        uint tableNodeId = PstNodeType.Make(tableType, PstNodeType.IndexOf(_nodeId));
        if (!_pst.TryGetNode(tableNodeId, out var node)) return null;
        return PstTableContext.Read(_pst, node);
    }
}

/// <summary>A message: its properties plus the recipient and attachment tables from its subnodes.</summary>
internal sealed class PstMessage
{
    internal PstMessage(PstFile pst, PstNodeEntry node)
    {
        var heap = new PstHeapNode(pst, node.DataBid);
        Properties = PstPropertyContext.Read(pst, node, heap);

        var subNodes = pst.ReadSubNodeTree(node.SubNodeBid);
        RecipientTable = ReadSubTable(pst, subNodes, PstNodeType.RecipientTable);
        AttachmentTable = ReadSubTable(pst, subNodes, PstNodeType.AttachmentTable);
    }

    public SortedDictionary<ushort, PstValue> Properties { get; }

    public PstTableContext? RecipientTable { get; }

    public PstTableContext? AttachmentTable { get; }

    public string? GetString(ushort propertyId) =>
        Properties.TryGetValue(propertyId, out var value) ? value.AsString() : null;

    public byte[]? GetBinary(ushort propertyId) =>
        Properties.TryGetValue(propertyId, out var value) && value.Type == PstPropertyType.Binary ? value.Bytes : null;

    public long? GetTime(ushort propertyId) =>
        Properties.TryGetValue(propertyId, out var value) && value.Type == PstPropertyType.Time ? value.Integer : null;

    private static PstTableContext? ReadSubTable(
        PstFile pst,
        Dictionary<uint, PstSubNodeEntry> subNodes,
        uint tableType)
    {
        PstSubNodeEntry? found = null;
        foreach (var entry in subNodes.Values)
        {
            if (PstNodeType.TypeOf(entry.NodeId) != tableType) continue;
            if (found is not null)
                throw new InvalidDataException($"PST message has more than one table of type 0x{tableType:X}");
            found = entry;
        }

        if (found is null) return null;
        return PstTableContext.Read(pst, new PstNodeEntry(found.Value.NodeId, found.Value.DataBid, found.Value.SubNodeBid));
    }
}
