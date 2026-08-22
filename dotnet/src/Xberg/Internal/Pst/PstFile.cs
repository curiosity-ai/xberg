// Ported from the `outlook-pst` crate's NDB layer (src/ndb/{header,page,block,block_id,node_id}.rs,
// src/crc.rs and src/encode/{permute,cyclic}.rs), which `crates/xberg/src/extraction/pst.rs`
// delegates to. Only the read path is ported — xberg never writes a PST.
//
// The Node Database is three layers: a 512-byte header naming the two root B-tree pages, the
// Node BTree (nid -> data block id + subnode block id) and the Block BTree (bid -> file offset +
// size). Everything above this file addresses content by node id and gets bytes back.

namespace Xberg.Internal.Pst;

/// <summary>`bCryptMethod`: how leaf data blocks are obfuscated on disk.</summary>
internal enum PstCryptMethod : byte
{
    None = 0x00,
    Permute = 0x01,
    Cyclic = 0x02,
}

/// <summary>NBTENTRY: one node — its property/heap data and, when present, its subnode tree.</summary>
internal readonly struct PstNodeEntry
{
    public PstNodeEntry(uint nodeId, ulong dataBid, ulong subNodeBid)
    {
        NodeId = nodeId;
        DataBid = dataBid;
        SubNodeBid = subNodeBid;
    }

    public uint NodeId { get; }
    public ulong DataBid { get; }

    /// <summary>0 when the node has no subnode tree.</summary>
    public ulong SubNodeBid { get; }
}

/// <summary>SLENTRY: one subnode — the same (data, subnode) pair as an NBT entry, keyed locally.</summary>
internal readonly struct PstSubNodeEntry
{
    public PstSubNodeEntry(uint nodeId, ulong dataBid, ulong subNodeBid)
    {
        NodeId = nodeId;
        DataBid = dataBid;
        SubNodeBid = subNodeBid;
    }

    public uint NodeId { get; }
    public ulong DataBid { get; }
    public ulong SubNodeBid { get; }
}

/// <summary>BBTENTRY: where a block lives in the file and how many bytes of it are real data.</summary>
internal readonly struct PstBlockEntry
{
    public PstBlockEntry(ulong offset, ushort size)
    {
        Offset = offset;
        Size = size;
    }

    public ulong Offset { get; }
    public ushort Size { get; }
}

/// <summary>NID type tags (`nidType`), the low 5 bits of a node id.</summary>
internal static class PstNodeType
{
    public const uint HeapNode = 0x00;
    public const uint Internal = 0x01;
    public const uint NormalFolder = 0x02;
    public const uint SearchFolder = 0x03;
    public const uint NormalMessage = 0x04;
    public const uint Attachment = 0x05;
    public const uint AssociatedMessage = 0x08;
    public const uint HierarchyTable = 0x0D;
    public const uint ContentsTable = 0x0E;
    public const uint AssociatedContentsTable = 0x0F;
    public const uint AttachmentTable = 0x11;
    public const uint RecipientTable = 0x12;

    /// <summary>`NID_MESSAGE_STORE`.</summary>
    public const uint MessageStoreNid = 0x21;

    /// <summary>`NID_ROOT_FOLDER`.</summary>
    public const uint RootFolderNid = 0x122;

    public static uint TypeOf(uint nodeId) => nodeId & 0x1F;

    public static uint IndexOf(uint nodeId) => nodeId >> 5;

    /// <summary>`NodeId::new`: rebuild a node id of a different type over the same index.</summary>
    public static uint Make(uint type, uint index) => (index << 5) | (type & 0x1F);
}

/// <summary>
/// A parsed PST file: header plus fully-walked node and block B-trees, over an in-memory image.
/// Mirrors what `outlook_pst::open_store` sets up before any messaging-layer call.
/// </summary>
internal sealed class PstFile
{
    private const uint HeaderMagic = 0x4E44_4221;        // "!BDN"
    private const ushort HeaderMagicClient = 0x4D53;     // "SM"
    private const ushort VersionAnsi = 15;
    private const ushort VersionAnsiAlt = 14;
    private const ushort VersionUnicode = 23;
    private const int PageSize = 512;

    private readonly byte[] _data;
    private readonly Dictionary<uint, PstNodeEntry> _nodes = new();
    private readonly Dictionary<ulong, PstBlockEntry> _blocks = new();

    private PstFile(byte[] data) => _data = data;

    public bool IsUnicode { get; private set; }

    public PstCryptMethod CryptMethod { get; private set; }

    /// <summary>Trailer width that follows every block's payload (BLOCKTRAILER).</summary>
    private int BlockTrailerSize => IsUnicode ? 16 : 12;

    /// <summary>
    /// Parse the header and materialize both B-trees. Throws <see cref="InvalidDataException"/>
    /// for anything that is not a readable PST, matching the upstream open-then-error behaviour.
    /// </summary>
    public static PstFile Open(ReadOnlySpan<byte> content)
    {
        var pst = new PstFile(content.ToArray());
        pst.ReadHeader();
        return pst;
    }

    private void ReadHeader()
    {
        if (_data.Length < 564) throw new InvalidDataException("PST file is too short to hold a header");
        if (ReadU32(0) != HeaderMagic) throw new InvalidDataException("Not a PST file: bad dwMagic");
        if (ReadU16(8) != HeaderMagicClient) throw new InvalidDataException("Not a PST file: bad wMagicClient");

        ushort version = ReadU16(10);
        IsUnicode = version switch
        {
            VersionUnicode => true,
            VersionAnsi or VersionAnsiAlt => false,
            _ => throw new InvalidDataException($"Unsupported PST version {version}"),
        };

        // The header CRC covers the 471 bytes after dwCRCPartial in both formats; upstream
        // refuses to open a file whose header does not check out, so a corrupt PST fails here
        // rather than producing plausible-looking garbage further down.
        if (ReadU32(4) != PstCrc.Compute(0, _data.AsSpan(8, 471)))
            throw new InvalidDataException("PST header CRC mismatch");

        int rootOffset = IsUnicode ? 180 : 164;
        int cryptOffset = IsUnicode ? 513 : 461;
        CryptMethod = _data[cryptOffset] switch
        {
            0x00 => PstCryptMethod.None,
            0x01 => PstCryptMethod.Permute,
            0x02 => PstCryptMethod.Cyclic,
            var other => throw new InvalidDataException($"Unsupported PST crypt method {other}"),
        };

        // ROOT: ... BREFNBT then BREFBBT, each a (bid, ib) pair.
        int brefSize = IsUnicode ? 16 : 8;
        int nbtOffset = rootOffset + (IsUnicode ? 36 : 20);
        int bbtOffset = nbtOffset + brefSize;

        ulong nbtIb = IsUnicode ? ReadU64(nbtOffset + 8) : ReadU32(nbtOffset + 4);
        ulong bbtIb = IsUnicode ? ReadU64(bbtOffset + 8) : ReadU32(bbtOffset + 4);

        WalkBTreePage(nbtIb, new HashSet<ulong>());
        WalkBTreePage(bbtIb, new HashSet<ulong>());
    }

    /// <summary>
    /// Recursively flatten a BTPAGE into <see cref="_nodes"/> or <see cref="_blocks"/>.
    /// Upstream descends these pages lazily on each lookup; walking them once up front is the
    /// same traversal with the same entries, and it makes a page that points back at itself
    /// terminate instead of recursing forever.
    /// </summary>
    private void WalkBTreePage(ulong offset, HashSet<ulong> visited)
    {
        if (!visited.Add(offset)) return;
        if (offset > (ulong)_data.Length - PageSize) throw new InvalidDataException("PST B-tree page is out of bounds");

        int page = (int)offset;
        int entriesSize = IsUnicode ? 488 : 496;
        byte entryCount = _data[page + entriesSize];
        byte entrySize = _data[page + entriesSize + 2];
        byte level = _data[page + entriesSize + 3];

        // The page trailer follows the four count bytes, after a 4-byte pad in the Unicode
        // layout; its first byte is the page type that says which B-tree this page belongs to.
        byte pageType = _data[page + entriesSize + (IsUnicode ? 8 : 4)];

        if (entrySize == 0 || entryCount * entrySize > entriesSize)
            throw new InvalidDataException("PST B-tree page declares more entries than it can hold");

        for (int i = 0; i < entryCount; i++)
        {
            int e = page + i * entrySize;
            if (level > 0)
            {
                // BTENTRY: btkey, then a BREF to the child page.
                int bref = e + (IsUnicode ? 8 : 4);
                ulong childIb = IsUnicode ? ReadU64(bref + 8) : ReadU32(bref + 4);
                WalkBTreePage(childIb, visited);
            }
            else if (pageType == 0x81)
            {
                // NBTENTRY
                uint nid = IsUnicode ? (uint)ReadU64(e) : ReadU32(e);
                int dataAt = e + (IsUnicode ? 8 : 4);
                int subAt = dataAt + (IsUnicode ? 8 : 4);
                ulong dataBid = IsUnicode ? ReadU64(dataAt) : ReadU32(dataAt);
                ulong subBid = IsUnicode ? ReadU64(subAt) : ReadU32(subAt);
                if ((subBid & ~1UL) == 0) subBid = 0;
                _nodes[nid] = new PstNodeEntry(nid, dataBid, subBid);
            }
            else if (pageType == 0x80)
            {
                // BBTENTRY: BREF then cb.
                ulong bid = IsUnicode ? ReadU64(e) : ReadU32(e);
                ulong ib = IsUnicode ? ReadU64(e + 8) : ReadU32(e + 4);
                ushort cb = ReadU16(e + (IsUnicode ? 16 : 8));
                _blocks[bid & ~1UL] = new PstBlockEntry(ib, cb);
            }
        }
    }

    public bool TryGetNode(uint nodeId, out PstNodeEntry entry) => _nodes.TryGetValue(nodeId, out entry);

    public PstNodeEntry GetNode(uint nodeId) =>
        _nodes.TryGetValue(nodeId, out var entry)
            ? entry
            : throw new InvalidDataException($"PST node 0x{nodeId:X} not found");

    private PstBlockEntry GetBlock(ulong blockId) =>
        _blocks.TryGetValue(blockId & ~1UL, out var entry)
            ? entry
            : throw new InvalidDataException($"PST block 0x{blockId:X} not found");

    /// <summary>Blocks on disk are padded up to a multiple of 64 bytes, payload plus trailer.</summary>
    private static int RoundBlockSize(int size) => (size + 63) / 64 * 64;

    /// <summary>
    /// Read one block's payload. Leaf blocks are CRC-checked and de-obfuscated; internal blocks
    /// (XBLOCK/XXBLOCK/SLBLOCK/SIBLOCK) are structural and stay as written.
    /// </summary>
    private byte[] ReadBlock(ulong blockId, out bool isInternal)
    {
        var entry = GetBlock(blockId);
        isInternal = (blockId & 0x2) == 0x2;

        int total = RoundBlockSize(entry.Size + BlockTrailerSize);
        if (entry.Offset > (ulong)_data.Length || (ulong)total > (ulong)_data.Length - entry.Offset)
            throw new InvalidDataException($"PST block 0x{blockId:X} runs past the end of the file");

        int start = (int)entry.Offset;
        var payload = _data.AsSpan(start, entry.Size).ToArray();

        int trailer = start + total - BlockTrailerSize;
        ushort trailerSize = ReadU16(trailer);
        if (trailerSize != entry.Size)
            throw new InvalidDataException($"PST block 0x{blockId:X} trailer size {trailerSize} disagrees with its B-tree entry");

        uint crc = IsUnicode ? ReadU32(trailer + 4) : ReadU32(trailer + 8);
        if (crc != PstCrc.Compute(0, payload))
            throw new InvalidDataException($"PST block 0x{blockId:X} CRC mismatch");

        if (!isInternal)
        {
            switch (CryptMethod)
            {
                case PstCryptMethod.Permute:
                    PstEncoding.PermuteDecode(payload);
                    break;
                case PstCryptMethod.Cyclic:
                    PstEncoding.CyclicDecode(payload, (uint)(blockId & ~1UL));
                    break;
            }
        }

        return payload;
    }

    /// <summary>
    /// Resolve a node's data block id to the ordered list of leaf data blocks it stands for,
    /// flattening any XBLOCK/XXBLOCK levels. The heap layer needs the split preserved because a
    /// heap-on-node addresses its allocations per block.
    /// </summary>
    public List<byte[]> ReadDataTree(ulong blockId)
    {
        var blocks = new List<byte[]>();
        CollectDataTree(blockId, blocks, 0);
        return blocks;
    }

    private void CollectDataTree(ulong blockId, List<byte[]> into, int depth)
    {
        if (depth > 8) throw new InvalidDataException("PST data tree is nested too deeply");

        var data = ReadBlock(blockId, out bool isInternal);
        if (!isInternal)
        {
            into.Add(data);
            return;
        }

        if (data.Length < 8 || data[0] != 0x01)
            throw new InvalidDataException("PST internal block is not a data tree block");

        int count = ReadU16(data, 2);
        int idSize = IsUnicode ? 8 : 4;
        if (8 + count * idSize > data.Length)
            throw new InvalidDataException("PST data tree block declares more entries than it holds");

        for (int i = 0; i < count; i++)
        {
            ulong child = IsUnicode ? ReadU64(data, 8 + i * 8) : ReadU32(data, 8 + i * 4);
            CollectDataTree(child, into, depth + 1);
        }
    }

    /// <summary>The whole of a node's data as one buffer (property values, table row matrices).</summary>
    public byte[] ReadNodeData(ulong blockId)
    {
        var blocks = ReadDataTree(blockId);
        if (blocks.Count == 1) return blocks[0];

        int total = blocks.Sum(b => b.Length);
        var result = new byte[total];
        int at = 0;
        foreach (var block in blocks)
        {
            Buffer.BlockCopy(block, 0, result, at, block.Length);
            at += block.Length;
        }
        return result;
    }

    /// <summary>Flatten a node's subnode tree (SLBLOCK/SIBLOCK) into local node id -> entry.</summary>
    public Dictionary<uint, PstSubNodeEntry> ReadSubNodeTree(ulong blockId)
    {
        var result = new Dictionary<uint, PstSubNodeEntry>();
        if (blockId == 0) return result;
        CollectSubNodes(blockId, result, 0);
        return result;
    }

    private void CollectSubNodes(ulong blockId, Dictionary<uint, PstSubNodeEntry> into, int depth)
    {
        if (depth > 8) throw new InvalidDataException("PST subnode tree is nested too deeply");

        var data = ReadBlock(blockId, out _);
        int headerSize = IsUnicode ? 8 : 4;
        if (data.Length < headerSize || data[0] != 0x02)
            throw new InvalidDataException("PST block is not a subnode tree block");

        byte level = data[1];
        int count = ReadU16(data, 2);

        if (level == 0)
        {
            int entrySize = IsUnicode ? 24 : 12;
            if (headerSize + count * entrySize > data.Length)
                throw new InvalidDataException("PST subnode block declares more entries than it holds");
            for (int i = 0; i < count; i++)
            {
                int e = headerSize + i * entrySize;
                uint nid = IsUnicode ? (uint)ReadU64(data, e) : ReadU32(data, e);
                int dataAt = e + (IsUnicode ? 8 : 4);
                int subAt = dataAt + (IsUnicode ? 8 : 4);
                ulong dataBid = IsUnicode ? ReadU64(data, dataAt) : ReadU32(data, dataAt);
                ulong subBid = IsUnicode ? ReadU64(data, subAt) : ReadU32(data, subAt);
                if ((subBid & ~1UL) == 0) subBid = 0;
                into[nid] = new PstSubNodeEntry(nid, dataBid, subBid);
            }
        }
        else
        {
            int entrySize = IsUnicode ? 16 : 8;
            if (headerSize + count * entrySize > data.Length)
                throw new InvalidDataException("PST subnode block declares more entries than it holds");
            for (int i = 0; i < count; i++)
            {
                int e = headerSize + i * entrySize;
                int childAt = e + (IsUnicode ? 8 : 4);
                ulong child = IsUnicode ? ReadU64(data, childAt) : ReadU32(data, childAt);
                CollectSubNodes(child, into, depth + 1);
            }
        }
    }

    private ushort ReadU16(int offset) => ReadU16(_data, offset);
    private uint ReadU32(int offset) => ReadU32(_data, offset);
    private ulong ReadU64(int offset) => ReadU64(_data, offset);

    internal static ushort ReadU16(byte[] data, int offset)
    {
        if (offset < 0 || offset + 2 > data.Length) throw new InvalidDataException("PST read past end of buffer");
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    internal static uint ReadU32(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length) throw new InvalidDataException("PST read past end of buffer");
        return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
    }

    internal static ulong ReadU64(byte[] data, int offset)
    {
        if (offset < 0 || offset + 8 > data.Length) throw new InvalidDataException("PST read past end of buffer");
        return ReadU32(data, offset) | ((ulong)ReadU32(data, offset + 4) << 32);
    }
}
