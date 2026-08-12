// Pure-managed BLAKE3 implementation.
//
// Ported from the official BLAKE3 reference implementation (reference_impl.rs,
// public domain / CC0). Produces byte-identical output to the Rust `blake3` crate,
// which the Xberg element-ID generation depends on.
//
// Only the hashing path is implemented (no keyed hashing / KDF), which is all the
// port needs. `Hasher` supports incremental `Update` + `Finalize`.

namespace Xberg.Internal.Blake3;

/// <summary>Incremental BLAKE3 hasher. Matches the Rust `blake3` crate byte-for-byte.</summary>
public sealed class Blake3Hasher
{
    private const int OutLen = 32;
    private const int BlockLen = 64;
    private const int ChunkLen = 1024;

    private const uint ChunkStart = 1 << 0;
    private const uint ChunkEnd = 1 << 1;
    private const uint Parent = 1 << 2;
    private const uint Root = 1 << 3;

    private static readonly uint[] IV =
    {
        0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
        0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
    };

    private static readonly int[] MsgPermutation =
    {
        2, 6, 3, 10, 7, 0, 4, 13, 1, 11, 12, 5, 9, 14, 15, 8,
    };

    private static uint Rotr(uint x, int n) => (x >> n) | (x << (32 - n));

    private static void G(uint[] s, int a, int b, int c, int d, uint mx, uint my)
    {
        s[a] = s[a] + s[b] + mx;
        s[d] = Rotr(s[d] ^ s[a], 16);
        s[c] = s[c] + s[d];
        s[b] = Rotr(s[b] ^ s[c], 12);
        s[a] = s[a] + s[b] + my;
        s[d] = Rotr(s[d] ^ s[a], 8);
        s[c] = s[c] + s[d];
        s[b] = Rotr(s[b] ^ s[c], 7);
    }

    private static void Round(uint[] state, uint[] m)
    {
        // Mix columns.
        G(state, 0, 4, 8, 12, m[0], m[1]);
        G(state, 1, 5, 9, 13, m[2], m[3]);
        G(state, 2, 6, 10, 14, m[4], m[5]);
        G(state, 3, 7, 11, 15, m[6], m[7]);
        // Mix diagonals.
        G(state, 0, 5, 10, 15, m[8], m[9]);
        G(state, 1, 6, 11, 12, m[10], m[11]);
        G(state, 2, 7, 8, 13, m[12], m[13]);
        G(state, 3, 4, 9, 14, m[14], m[15]);
    }

    private static void Permute(uint[] m)
    {
        var permuted = new uint[16];
        for (int i = 0; i < 16; i++)
            permuted[i] = m[MsgPermutation[i]];
        Array.Copy(permuted, m, 16);
    }

    private static uint[] Compress(uint[] chainingValue, uint[] blockWords, ulong counter, uint blockLen, uint flags)
    {
        var state = new uint[16];
        Array.Copy(chainingValue, 0, state, 0, 8);
        state[8] = IV[0];
        state[9] = IV[1];
        state[10] = IV[2];
        state[11] = IV[3];
        state[12] = (uint)counter;
        state[13] = (uint)(counter >> 32);
        state[14] = blockLen;
        state[15] = flags;

        var m = (uint[])blockWords.Clone();
        for (int r = 0; r < 7; r++)
        {
            Round(state, m);
            if (r < 6)
                Permute(m);
        }

        for (int i = 0; i < 8; i++)
        {
            state[i] ^= state[i + 8];
            state[i + 8] ^= chainingValue[i];
        }

        return state;
    }

    private static uint[] First8(uint[] compressionOutput)
    {
        var cv = new uint[8];
        Array.Copy(compressionOutput, cv, 8);
        return cv;
    }

    private static void WordsFromLe(byte[] block, uint[] outWords)
    {
        for (int i = 0; i < 16; i++)
        {
            int j = i * 4;
            outWords[i] = (uint)(block[j] | (block[j + 1] << 8) | (block[j + 2] << 16) | (block[j + 3] << 24));
        }
    }

    private readonly struct Output
    {
        public readonly uint[] InputChainingValue;
        public readonly uint[] BlockWords;
        public readonly ulong Counter;
        public readonly uint BlockLen;
        public readonly uint Flags;

        public Output(uint[] icv, uint[] blockWords, ulong counter, uint blockLen, uint flags)
        {
            InputChainingValue = icv;
            BlockWords = blockWords;
            Counter = counter;
            BlockLen = blockLen;
            Flags = flags;
        }

        public uint[] ChainingValue() =>
            First8(Compress(InputChainingValue, BlockWords, Counter, BlockLen, Flags));

        public void RootOutputBytes(Span<byte> outBytes)
        {
            ulong outputBlockCounter = 0;
            int offset = 0;
            while (offset < outBytes.Length)
            {
                var words = Compress(InputChainingValue, BlockWords, outputBlockCounter, BlockLen, Flags | Root);
                for (int i = 0; i < 16 && offset < outBytes.Length; i++)
                {
                    uint w = words[i];
                    for (int b = 0; b < 4 && offset < outBytes.Length; b++)
                        outBytes[offset++] = (byte)(w >> (8 * b));
                }
                outputBlockCounter++;
            }
        }
    }

    private sealed class ChunkState
    {
        public uint[] ChainingValue;
        public ulong ChunkCounter;
        public readonly byte[] Block = new byte[BlockLen];
        public int BlockLength;
        public int BlocksCompressed;
        public uint Flags;

        public ChunkState(uint[] key, ulong chunkCounter, uint flags)
        {
            ChainingValue = (uint[])key.Clone();
            ChunkCounter = chunkCounter;
            Flags = flags;
        }

        public int Len() => BlockLen * BlocksCompressed + BlockLength;

        private uint StartFlag() => BlocksCompressed == 0 ? ChunkStart : 0;

        public void Update(ReadOnlySpan<byte> input)
        {
            var blockWords = new uint[16];
            while (input.Length > 0)
            {
                if (BlockLength == BlockLen)
                {
                    WordsFromLe(Block, blockWords);
                    ChainingValue = First8(Compress(ChainingValue, blockWords, ChunkCounter, BlockLen, Flags | StartFlag()));
                    BlocksCompressed++;
                    Array.Clear(Block, 0, BlockLen);
                    BlockLength = 0;
                }

                int want = BlockLen - BlockLength;
                int take = Math.Min(want, input.Length);
                input.Slice(0, take).CopyTo(Block.AsSpan(BlockLength));
                BlockLength += take;
                input = input.Slice(take);
            }
        }

        public Output Output()
        {
            var blockWords = new uint[16];
            WordsFromLe(Block, blockWords);
            return new Output(ChainingValue, blockWords, ChunkCounter, (uint)BlockLength, Flags | StartFlag() | ChunkEnd);
        }
    }

    private static Output ParentOutput(uint[] leftCv, uint[] rightCv, uint[] key, uint flags)
    {
        var blockWords = new uint[16];
        Array.Copy(leftCv, 0, blockWords, 0, 8);
        Array.Copy(rightCv, 0, blockWords, 8, 8);
        return new Output(key, blockWords, 0, BlockLen, Parent | flags);
    }

    private static uint[] ParentCv(uint[] leftCv, uint[] rightCv, uint[] key, uint flags) =>
        ParentOutput(leftCv, rightCv, key, flags).ChainingValue();

    private ChunkState _chunkState;
    private readonly uint[] _key;
    private readonly uint[][] _cvStack = new uint[54][];
    private int _cvStackLen;
    private readonly uint _flags;

    public Blake3Hasher()
    {
        _key = (uint[])IV.Clone();
        _chunkState = new ChunkState(_key, 0, 0);
        _flags = 0;
    }

    private void PushStack(uint[] cv) => _cvStack[_cvStackLen++] = cv;

    private uint[] PopStack() => _cvStack[--_cvStackLen];

    private void AddChunkChainingValue(uint[] newCv, ulong totalChunks)
    {
        while ((totalChunks & 1) == 0)
        {
            newCv = ParentCv(PopStack(), newCv, _key, _flags);
            totalChunks >>= 1;
        }
        PushStack(newCv);
    }

    public void Update(ReadOnlySpan<byte> input)
    {
        while (input.Length > 0)
        {
            if (_chunkState.Len() == ChunkLen)
            {
                var chunkCv = _chunkState.Output().ChainingValue();
                ulong totalChunks = _chunkState.ChunkCounter + 1;
                AddChunkChainingValue(chunkCv, totalChunks);
                _chunkState = new ChunkState(_key, totalChunks, _flags);
            }

            int want = ChunkLen - _chunkState.Len();
            int take = Math.Min(want, input.Length);
            _chunkState.Update(input.Slice(0, take));
            input = input.Slice(take);
        }
    }

    public void Finalize(Span<byte> outBytes)
    {
        var output = _chunkState.Output();
        int parentNodesRemaining = _cvStackLen;
        while (parentNodesRemaining > 0)
        {
            parentNodesRemaining--;
            output = ParentOutput(_cvStack[parentNodesRemaining], output.ChainingValue(), _key, _flags);
        }
        output.RootOutputBytes(outBytes);
    }

    /// <summary>Hash <paramref name="input"/> and return the 32-byte digest.</summary>
    public static byte[] Hash(ReadOnlySpan<byte> input)
    {
        var hasher = new Blake3Hasher();
        hasher.Update(input);
        var outBytes = new byte[OutLen];
        hasher.Finalize(outBytes);
        return outBytes;
    }
}
