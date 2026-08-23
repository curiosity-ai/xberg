// Ported from the `outlook-pst` crate's src/ltp/{heap,tree}.rs.
//
// A Heap-on-Node (HN) is a private allocator laid over a node's data blocks: each block ends in a
// page map of allocation offsets, and a Heap ID (HID) names one allocation as (block index,
// allocation index). A BTree-on-Heap (BTH) is then a fixed-record B-tree whose pages are heap
// allocations — the storage both Property Contexts and Table Contexts are built on.

namespace Xberg.Internal.Pst;

/// <summary>`bClientSig` values that say what a heap's user root holds.</summary>
internal static class PstHeapNodeType
{
    public const byte Table = 0x7C;
    public const byte Tree = 0xB5;
    public const byte Properties = 0xBC;
}

/// <summary>A heap-on-node: the node's data blocks plus HID-addressed lookup into them.</summary>
internal sealed class PstHeapNode
{
    private readonly List<byte[]> _blocks;

    public PstHeapNode(PstFile pst, ulong dataBid)
    {
        _blocks = pst.ReadDataTree(dataBid);
        if (_blocks.Count == 0) throw new InvalidDataException("PST heap node has no data blocks");

        var header = _blocks[0];
        if (header.Length < 12) throw new InvalidDataException("PST heap node header is truncated");
        if (header[2] != 0xEC) throw new InvalidDataException("PST heap node has an invalid bSig");

        ClientSignature = header[3];
        UserRoot = PstFile.ReadU32(header, 4);
    }

    public byte ClientSignature { get; }

    /// <summary>`hidUserRoot`: the HID of whatever structure the client stored in this heap.</summary>
    public uint UserRoot { get; }

    /// <summary>Resolve a HID to its allocation. Throws when the HID names something absent.</summary>
    public byte[] Find(uint heapId)
    {
        int blockIndex = (int)(heapId >> 16);
        uint index = (heapId >> 5) & 0x7FF;
        if (index < 1) throw new InvalidDataException($"PST heap id 0x{heapId:X} has no allocation index");
        if (blockIndex >= _blocks.Count) throw new InvalidDataException($"PST heap block {blockIndex} not found");

        var block = _blocks[blockIndex];

        // Every heap block starts with its page-map offset: HNHDR for block 0, HNBITMAPHDR for
        // each 128th block after the eighth, HNPAGEHDR otherwise — all three lead with the same
        // u16, so one read covers them.
        int pageMapOffset = PstFile.ReadU16(block, 0);
        int allocCount = PstFile.ReadU16(block, pageMapOffset);
        int i = (int)index - 1;
        if (i >= allocCount) throw new InvalidDataException($"PST heap allocation {i} not found");

        int offsets = pageMapOffset + 4;
        int start = PstFile.ReadU16(block, offsets + i * 2);
        int end = PstFile.ReadU16(block, offsets + (i + 1) * 2);
        if (end < start || end > block.Length)
            throw new InvalidDataException($"PST heap allocation {i} is out of bounds");

        return block[start..end];
    }
}

/// <summary>One leaf record of a BTree-on-Heap: fixed-width key bytes and value bytes.</summary>
internal readonly struct PstHeapTreeEntry
{
    public PstHeapTreeEntry(byte[] key, byte[] value)
    {
        Key = key;
        Value = value;
    }

    public byte[] Key { get; }
    public byte[] Value { get; }
}

internal static class PstHeapTree
{
    /// <summary>
    /// Walk a BTH from its BTHHEADER down to the leaves. Intermediate records are (key, HID of the
    /// next level); the key sizes are checked against what the caller expects, as upstream does,
    /// so a tree of the wrong shape is rejected rather than misread.
    /// </summary>
    public static List<PstHeapTreeEntry> Entries(PstHeapNode heap, uint rootHeapId, int keySize, int valueSize)
    {
        var header = heap.Find(rootHeapId);
        if (header.Length < 8) throw new InvalidDataException("PST BTH header is truncated");
        if (header[0] != PstHeapNodeType.Tree) throw new InvalidDataException("PST BTH header has the wrong bType");
        if (header[1] != keySize) throw new InvalidDataException($"PST BTH key size {header[1]} is not the expected {keySize}");
        if (header[2] != valueSize) throw new InvalidDataException($"PST BTH entry size {header[2]} is not the expected {valueSize}");

        int levels = header[3];
        uint root = PstFile.ReadU32(header, 4);

        var results = new List<PstHeapTreeEntry>();
        if (root == 0) return results;

        var current = new List<uint> { root };
        for (int level = levels; level > 0; level--)
        {
            var next = new List<uint>();
            int recordSize = keySize + 4;
            foreach (uint heapId in current)
            {
                var page = heap.Find(heapId);
                for (int at = 0; at + recordSize <= page.Length; at += recordSize)
                    next.Add(PstFile.ReadU32(page, at + keySize));
            }
            current = next;
        }

        int leafSize = keySize + valueSize;
        foreach (uint heapId in current)
        {
            var page = heap.Find(heapId);
            for (int at = 0; at + leafSize <= page.Length; at += leafSize)
                results.Add(new PstHeapTreeEntry(page[at..(at + keySize)], page[(at + keySize)..(at + leafSize)]));
        }

        return results;
    }
}
