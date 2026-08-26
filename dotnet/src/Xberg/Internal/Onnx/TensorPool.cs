namespace Xberg.Internal.Onnx;

/// <summary>
/// Reusable backing storage for tensors, keyed by exact element count.
/// <para>
/// A single inference materialises well over a gigabyte of activations, most of them large
/// enough to land on the large-object heap, where each one is an expensive allocation and a
/// future collection. Because the graph runs the same shapes every time, a free list keyed
/// by length turns that into a handful of allocations on the first pass and none afterwards.
/// </para>
/// <para>
/// Buckets are keyed by <em>exact</em> length rather than rounded up, which matters more
/// than it looks: kernels routinely hand a tensor's whole array to a vectorised primitive,
/// so an oversized buffer would silently make the operation cover elements that are not part
/// of the tensor.
/// </para>
/// </summary>
internal sealed class TensorPool
{
    /// <summary>
    /// Storage held for reuse, above which returned buffers are dropped instead. Bounds the
    /// pool's own footprint so a graph with many distinct shapes cannot make it grow without
    /// limit.
    /// </summary>
    private const long MaxRetainedBytes = 768L * 1024 * 1024;

    /// <summary>Buffers below this length are cheap to allocate and not worth tracking.</summary>
    private const int MinPooledLength = 4096;

    private readonly Dictionary<int, Stack<float[]>> _buckets = [];
    private long _retainedBytes;

    public long RetainedBytes => _retainedBytes;
    public int Reused { get; private set; }
    public int Allocated { get; private set; }

    /// <summary>
    /// How many buffers of each length the pool has had to allocate. Only misses are recorded,
    /// so this stays cheap, and in the steady state it should be empty — anything left in it
    /// names a shape whose storage is not being recycled.
    /// </summary>
    public Dictionary<int, int> AllocationsByLength { get; } = [];

    /// <summary>The ambient pool for the execution on this thread, if any.</summary>
    /// <remarks>
    /// Thread-static rather than threaded through every kernel signature: graph execution is
    /// sequential — parallelism lives inside the kernels — so a per-thread ambient is exactly
    /// the right scope, and work that a kernel does spawn on other threads simply allocates
    /// normally rather than sharing an unsynchronised pool.
    /// </remarks>
    [ThreadStatic]
    private static TensorPool? _current;

    public static TensorPool? Current => _current;

    /// <summary>Make this pool ambient for the duration of the returned scope.</summary>
    public Scope Activate()
    {
        var previous = _current;
        _current = this;
        return new Scope(previous);
    }

    internal readonly struct Scope(TensorPool? previous) : IDisposable
    {
        public void Dispose() => _current = previous;
    }

    public float[] Rent(int length)
    {
        if (length >= MinPooledLength && _buckets.TryGetValue(length, out var bucket) && bucket.Count > 0)
        {
            var buffer = bucket.Pop();
            _retainedBytes -= (long)length * sizeof(float);
            Reused++;
            return buffer;
        }
        Allocated++;
        if (length >= MinPooledLength)
            AllocationsByLength[length] = AllocationsByLength.GetValueOrDefault(length) + 1;
        return GC.AllocateUninitializedArray<float>(length);
    }

    public void Return(float[] buffer)
    {
        int length = buffer.Length;
        if (length < MinPooledLength) return;

        long bytes = (long)length * sizeof(float);
        if (_retainedBytes + bytes > MaxRetainedBytes) return;

        if (!_buckets.TryGetValue(length, out var bucket)) _buckets[length] = bucket = new Stack<float[]>();
        bucket.Push(buffer);
        _retainedBytes += bytes;
    }

    public void Clear()
    {
        _buckets.Clear();
        _retainedBytes = 0;
    }
}

/// <summary>
/// A tensor's float storage plus the reference count that decides when it can be recycled.
/// <para>
/// Counting on the buffer rather than the tensor is what makes reuse safe in the presence of
/// views. <c>Reshape</c>, <c>Squeeze</c>, <c>Unsqueeze</c> and <c>Flatten</c> all return a new
/// tensor over the <em>same</em> array, and <c>Identity</c> returns the very same tensor, so
/// "this value is dead" says nothing about whether the memory behind it is. Each graph value
/// holding the buffer contributes one count; the array goes back to the pool only when the
/// last of them is gone.
/// </para>
/// </summary>
internal sealed class TensorBuffer
{
    public readonly float[] Array;
    private readonly TensorPool? _pool;
    private int _references;

    private TensorBuffer(float[] array, TensorPool? pool)
    {
        Array = array;
        _pool = pool;
    }

    /// <summary>Wrap an array the pool does not own — an initializer, or caller-supplied data.</summary>
    public static TensorBuffer Wrap(float[] array) => new(array, null);

    /// <summary>Take a buffer of exactly <paramref name="length"/> elements from the ambient pool.</summary>
    public static TensorBuffer Allocate(int length)
    {
        var pool = TensorPool.Current;
        return pool is null
            ? new TensorBuffer(GC.AllocateUninitializedArray<float>(length), null)
            : new TensorBuffer(pool.Rent(length), pool);
    }

    /// <summary>How many graph values currently hold this storage.</summary>
    /// <remarks>
    /// One means a single name owns it, which is the condition under which a node may write
    /// its output over its input: nothing else can observe the change.
    /// </remarks>
    public int References => _references;

    /// <summary>Whether the pool owns this array, rather than a caller or an initializer.</summary>
    /// <remarks>
    /// An initializer is a constant shared by every run and a feed belongs to the caller;
    /// neither may be overwritten, and both arrive wrapped rather than rented.
    /// </remarks>
    public bool IsPooled => _pool is not null;

    public void AddReference() => _references++;

    private bool _recycled;

    /// <summary>Drop one reference; recycle the array when none remain.</summary>
    public void Release()
    {
        if (_references > 0) _references--;
        if (_references > 0 || _pool is null || _recycled) return;
        // Latched, so a double release cannot hand the same array to the pool twice and let
        // two live tensors end up sharing it.
        _recycled = true;
        _pool.Return(Array);
    }
}
