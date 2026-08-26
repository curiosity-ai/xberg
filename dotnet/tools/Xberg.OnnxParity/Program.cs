using System.Diagnostics;
using System.Numerics;
using Xberg.Internal.Onnx.Ops;
using System.Globalization;
using System.Text.Json;
using Xberg.Internal.Onnx;

namespace Xberg.OnnxParity;

/// <summary>
/// Compares the C# ONNX runtime against a reference dump produced by
/// <c>tools/onnx-parity/dump_reference.py</c>, one graph value at a time.
/// <para>
/// The comparison is per node rather than per model on purpose. A single wrong kernel deep
/// in a 2676-node graph produces a final output that is merely <em>different</em>, with
/// nothing to say where it went wrong; walking values in topological order and reporting the
/// first mismatch names the failing operator directly, and everything after it is
/// downstream noise.
/// </para>
/// </summary>
internal static class Program
{
    private sealed record ReferenceTensor(string File, string Dtype, int[] Shape);

    /// <summary>
    /// An input of the right shape and type for a timing run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A benchmark measures time, and the time a convolution takes does not depend on the
    /// numbers going through it — so it needs the shape the graph declares, not the reference's
    /// own values. Requiring a reference dump for a timing run means regenerating hundreds of
    /// megabytes of intermediates before anyone can measure a kernel change.
    /// </para>
    /// <para>
    /// A symbolic dimension becomes 1: it is the batch, and one page is the unit the numbers in
    /// the README are quoted in. Integer inputs are filled with the page size a detector expects
    /// rather than zeros, because a zero-size page can take a short path through the decode and
    /// time something the real graph never does.
    /// </para>
    /// </remarks>
    private static Tensor SyntheticFeed(OnnxValueInfo input)
    {
        int[] shape = input.Shape.Length > 0
            ? input.Shape.Select(d => d <= 0 ? 1 : d).ToArray()
            : [1];

        if (input.ElementType == ElementType.Float)
        {
            var tensor = Tensor.AllocateFloat(shape);
            // A mid-grey page: the values do not matter, but a buffer of zeros can be
            // denormal-free in a way real activations are not.
            Array.Fill(tensor.Floats, 0.5f);
            return tensor;
        }

        var integral = Tensor.AllocateLong(input.ElementType, shape);
        Array.Fill(integral.Longs, 640L);
        return integral;
    }


    private sealed record NodeInfo(int Index, string Name, string OpType, string[] Inputs, string[] Outputs);

    /// <summary>Warm-up executions discarded before timing anything.</summary>
    /// <remarks>
    /// .NET starts methods in a quick-JIT tier and only promotes them to optimised code after
    /// roughly thirty invocations, and a graph this size also has to fault in 169 MB of
    /// weights and spin up the thread pool. Timing before that settles measures the compiler,
    /// not the runtime. Three full passes put every kernel well past the promotion threshold,
    /// since each pass invokes them dozens to hundreds of times.
    /// </remarks>
    private const int WarmupRuns = 3;

    /// <summary>
    /// Peak fused-multiply-add throughput measured in this process, moments before the timings.
    /// <para>
    /// Every rate below is also reported as a fraction of it. On this VM the host's own
    /// throughput has been observed to move by more than two to one between consecutive runs of
    /// the same binary, which is larger than most of what optimisation buys — so an absolute
    /// GFLOP/s is not comparable across runs, and a fraction of the ceiling measured alongside
    /// it is.
    /// </para>
    /// </summary>
    private static double _ceilingGflops;

    /// <summary>Format a rate against the ceiling measured in this same process.</summary>
    private static string OfCeiling(double gflops) =>
        _ceilingGflops > 0 ? $"{gflops / _ceilingGflops,4:P0} of peak" : new string(' ', 12);

    /// <summary>
    /// Time whole-model inference and attribute the result to individual nodes.
    /// </summary>
    private static void BenchmarkModel(OnnxModel rawModel, Dictionary<string, Tensor> feeds, int runs)
    {
        // Benchmarking measures the path a real caller takes, which is the optimised graph.
        var optimized = GraphOptimizer.Optimize(rawModel);
        var session = new OnnxSession(optimized, optimize: false);
        var model = optimized;
        Console.WriteLine($"graph optimisation: {rawModel.Nodes.Length} nodes -> {model.Nodes.Length} " +
                          $"({rawModel.Nodes.Length - model.Nodes.Length} fused away)");

        for (int i = 0; i < WarmupRuns; i++) session.Run(feeds);

        var stopwatch = new Stopwatch();
        var timings = new List<double>(runs);
        for (int run = 0; run < runs; run++)
        {
            stopwatch.Restart();
            session.Run(feeds);
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }
        timings.Sort();
        Console.WriteLine();
        Console.WriteLine(
            $"inference over {runs} runs (after {WarmupRuns} warm-up runs): " +
            $"median {timings[timings.Count / 2]:F0} ms, best {timings[0]:F0} ms, worst {timings[^1]:F0} ms");

        // A pool miss in the steady state is not a small thing: the buffer is fresh pages, and
        // first touch of each one traps into the kernel, which shows up as time charged to
        // whichever node happened to allocate it.
        int reusedBefore = session.Pool.Reused, allocatedBefore = session.Pool.Allocated;
        session.Pool.AllocationsByLength.Clear();

        var profile = new OnnxSession.ExecutionProfile
        {
            NodeMicroseconds = new double[model.Nodes.Length],
            NodeOutputShapes = new string[model.Nodes.Length],
        };
        session.Run(feeds, capture: null, profile);

        Console.WriteLine(
            $"buffer pool over the profiled run: {session.Pool.Reused - reusedBefore} reused, " +
            $"{session.Pool.Allocated - allocatedBefore} allocated, " +
            $"{session.Pool.RetainedBytes / (1024.0 * 1024):F0} MiB retained");
        foreach (var (length, count) in session.Pool.AllocationsByLength.OrderByDescending(p => (long)p.Key * p.Value).Take(8))
            Console.WriteLine($"    {count,4} x {length,10} floats  ({(double)length * count * 4 / (1024 * 1024),6:F1} MiB)");

        double total = profile.NodeMicroseconds.Sum();
        Console.WriteLine();
        Console.WriteLine($"by operator ({total / 1000:F0} ms attributed):");
        var byOperator = new Dictionary<string, (double Time, int Count)>(StringComparer.Ordinal);
        for (int i = 0; i < model.Nodes.Length; i++)
        {
            var entry = byOperator.GetValueOrDefault(model.Nodes[i].OpType);
            byOperator[model.Nodes[i].OpType] = (entry.Time + profile.NodeMicroseconds[i], entry.Count + 1);
        }
        foreach (var (op, entry) in byOperator.OrderByDescending(p => p.Value.Time).Take(10))
            Console.WriteLine($"  {op,-20} {entry.Time / 1000,8:F1} ms  {entry.Time / total,6:P1}  " +
                              $"over {entry.Count} nodes");

        Console.WriteLine();
        Console.WriteLine("hottest individual nodes:");
        var hottest = Enumerable.Range(0, model.Nodes.Length)
            .OrderByDescending(i => profile.NodeMicroseconds[i])
            .Take(28);
        foreach (int i in hottest)
        {
            var node = model.Nodes[i];
            double flops = FloatingPointOperations(model, node, profile.NodeOutputShapes[i]);
            // Only the arithmetic-bound operators get a rate; for the rest it would be noise.
            string rate = flops > 0
                ? $"{flops / (profile.NodeMicroseconds[i] * 1e3),6:F0} GFLOP/s"
                : new string(' ', 14);
            Console.WriteLine($"  #{i,-5} {node.OpType,-16} {profile.NodeMicroseconds[i] / 1000,7:F1} ms  " +
                              $"{rate}  out {profile.NodeOutputShapes[i],-22} {node.Name}");
        }

        // The aggregate rate is what parity is measured against: the arithmetic is fixed, so
        // this is the one number that says how much of the machine the runtime is using.
        double totalFlops = 0, arithmeticMicroseconds = 0;
        for (int i = 0; i < model.Nodes.Length; i++)
        {
            double flops = FloatingPointOperations(model, model.Nodes[i], profile.NodeOutputShapes[i]);
            if (flops <= 0) continue;
            totalFlops += flops;
            arithmeticMicroseconds += profile.NodeMicroseconds[i];
        }
        Console.WriteLine();
        Console.WriteLine(
            $"arithmetic-bound nodes: {totalFlops / 1e9:F1} GFLOP in {arithmeticMicroseconds / 1000:F0} ms " +
            $"= {totalFlops / (arithmeticMicroseconds * 1e3):F0} GFLOP/s, " +
            $"{OfCeiling(totalFlops / (arithmeticMicroseconds * 1e3))}");

        // How much of the graph is doing nothing measurable: shape arithmetic and other
        // scalar bookkeeping that could be folded away entirely rather than made faster.
        int trivial = profile.NodeMicroseconds.Count(t => t < 10);
        double trivialTime = profile.NodeMicroseconds.Where(t => t < 10).Sum();
        Console.WriteLine();
        Console.WriteLine($"{trivial} of {model.Nodes.Length} nodes take under 10 us each " +
                          $"({trivialTime / 1000:F1} ms, {trivialTime / total:P1} of runtime)");
    }

    /// <summary>
    /// Multiply-accumulates a node performs, counted as two operations each, or zero for the
    /// operators where the figure would not mean anything.
    /// <para>
    /// The point is to separate "this node is slow because it has a lot of arithmetic to do"
    /// from "this node is slow", which the wall-clock column alone cannot do. Only the reduction
    /// extent has to be recovered — the output shape is recorded during profiling, and for
    /// these operators the other operand is a weight, whose shape is known from the graph.
    /// </para>
    /// </summary>
    private static double FloatingPointOperations(OnnxModel model, OnnxNode node, string outputShape)
    {
        long elements = 1;
        foreach (var part in outputShape.Trim('[', ']').Split(','))
        {
            if (!int.TryParse(part.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int dimension))
                return 0;
            elements *= dimension;
        }
        if (elements <= 0) return 0;

        switch (node.OpType)
        {
            case "Conv" when node.Inputs.Length >= 2
                             && model.Initializers.TryGetValue(node.Inputs[1], out var weight)
                             && weight.Rank == 4:
                // [filters, inputChannels/group, kh, kw] — the trailing three are the reduction.
                return 2.0 * elements * weight.Shape[1] * weight.Shape[2] * weight.Shape[3];

            case "MatMul" when node.Inputs.Length >= 2
                               && model.Initializers.TryGetValue(node.Inputs[1], out var b)
                               && b.Rank >= 2:
                return 2.0 * elements * b.Shape[b.Rank - 2];

            case "Gemm" when node.Inputs.Length >= 2
                             && model.Initializers.TryGetValue(node.Inputs[1], out var g)
                             && g.Rank == 2:
                // transB decides which axis of the weight is the reduction.
                return 2.0 * elements * (node.AttrInt("transB", 0) != 0 ? g.Shape[1] : g.Shape[0]);

            default:
                return 0;
        }
    }

    /// <summary>
    /// Time the matrix-multiply kernel on the shapes RT-DETR's convolutions actually lower
    /// to, so its throughput can be judged directly rather than inferred from whole-model
    /// timings that also include im2col, allocation and everything else.
    /// <para>
    /// Each shape is also measured with the packing done alone, over the same panels in the
    /// same order but with no arithmetic. Packing and multiplying are the only two things the
    /// kernel does, so that one extra number says which of them a slow shape is spending its
    /// time in — a distinction the total cannot make, and one that has already been guessed
    /// wrong once here.
    /// </para>
    /// </summary>
    private static void BenchmarkGemm()
    {
        (string Label, int M, int K, int N)[] shapes =
        [
            ("1x1 conv  256->256 @160x160", 256, 256, 25600),
            ("1x1 conv  256->128 @160x160", 128, 256, 25600),
            ("1x1 conv  512->512 @80x80  ", 512, 512, 6400),
            ("1x1 conv  256->512 @80x80  ", 512, 256, 6400),
            ("1x1 conv  512->1024 @40x40 ", 1024, 512, 1600),
            ("1x1 conv  256->1024 @40x40 ", 1024, 256, 1600),
            ("1x1 conv 1024->2048 @20x20 ", 2048, 1024, 400),
            ("3x3 conv  256->256 @80x80  ", 256, 2304, 6400),
            ("3x3 conv   64->64  @160x160", 64, 576, 25600),
            ("  same, m rounded to 60    ", 60, 576, 25600),
            ("  same, m rounded to 48    ", 48, 576, 25600),
            ("decoder projection         ", 256, 256, 300),
            ("decoder value_proj         ", 8400, 256, 256),
            ("decoder memory  proj       ", 8400, 256, 512),
        ];

        Console.WriteLine("  shape                            total                           of which packing");
        foreach (var (label, m, k, n) in shapes)
        {
            var a = new float[m * k];
            var b = new float[k * n];
            var c = new float[m * n];
            var random = new Random(1);
            for (int i = 0; i < a.Length; i++) a[i] = (float)random.NextDouble();
            for (int i = 0; i < b.Length; i++) b[i] = (float)random.NextDouble();

            for (int i = 0; i < WarmupRuns; i++) Linear.MultiplyInto(a, b, c, m, k, n);

            // Small shapes finish in well under a millisecond, where thread-pool wake-up and
            // timer granularity dominate — the 256x300 product has read anywhere from 16 to
            // 104 GFLOP/s across runs. Repeat until each shape has had enough work to measure.
            int repeats = Math.Clamp((int)(3e9 / (2.0 * m * k * n)), 5, 2000);

            var stopwatch = Stopwatch.StartNew();
            for (int r = 0; r < repeats; r++) Linear.MultiplyInto(a, b, c, m, k, n);
            double seconds = stopwatch.Elapsed.TotalSeconds / repeats;

            double packSeconds = TimePackingOnly(b, k, n, repeats);

            double gflops = 2.0 * m * k * n / seconds / 1e9;
            Console.WriteLine(
                $"  {label}  {seconds * 1000,8:F1} ms   {gflops,6:F1} GFLOP/s  {OfCeiling(gflops)}   " +
                $"{packSeconds * 1000,7:F1} ms  {packSeconds / seconds,5:P0}");
        }
    }

    /// <summary>
    /// Walk exactly the panels the multiply would, packing each and doing nothing else.
    /// </summary>
    private static double TimePackingOnly(float[] b, int k, int n, int repeats)
    {
        var (strideN, strideK) = GemmKernel.PanelExtents(k, n);
        int panels = (n + strideN - 1) / strideN;

        // The scratch buffer is per-thread and reused, exactly as the kernel's is. Allocating
        // it inside the loop instead would have this measuring the allocator, and did.
        int scratchLength = ((strideN + 15) / 16) * strideK * 16;
        var stopwatch = Stopwatch.StartNew();
        for (int r = 0; r < repeats; r++)
        {
            Parallel.For(
                0, panels,
                () => new float[scratchLength],
                (index, _, scratch) =>
                {
                    int jc = index * strideN;
                    int countN = Math.Min(strideN, n - jc);
                    for (int pc = 0; pc < k; pc += strideK)
                        GemmKernel.PackB(scratch, 0, b, n, pc, Math.Min(strideK, k - pc), jc, countN);
                    return scratch;
                },
                _ => { });
        }
        return stopwatch.Elapsed.TotalSeconds / repeats;
    }

    private static int Main(string[] args)
    {
        string? modelPath = null;
        string? referenceDir = null;
        float absoluteTolerance = 2e-4f;
        float relativeTolerance = 2e-3f;
        int reportLimit = 10;
        bool listOps = false;
        string? isolateOp = null;
        float? detectionThreshold = null;
        int benchmarkRuns = 0;
        bool gemmBenchmark = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--model": modelPath = args[++i]; break;
                case "--reference": referenceDir = args[++i]; break;
                case "--atol": absoluteTolerance = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--rtol": relativeTolerance = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--limit": reportLimit = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--list-ops": listOps = true; break;
                case "--benchmark": benchmarkRuns = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--gemm": gemmBenchmark = true; break;
                case "--isolate": isolateOp = args[++i]; break;
                case "--detections": detectionThreshold = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default:
                    Console.Error.WriteLine($"unknown argument '{args[i]}'");
                    return 2;
            }
        }

        // Any timing at all is preceded by the machine description and a fresh calibration:
        // these runs happen on a VM whose host can change underneath us, and without a
        // re-measured hardware ceiling a slower host is indistinguishable from a regression.
        if (gemmBenchmark || benchmarkRuns > 0)
        {
            MachineProbe.PrintMachine();
            Console.WriteLine();
            _ceilingGflops = MachineProbe.Calibrate().MultiThreadGflops;
            Console.WriteLine();
        }

        // Given both, the kernel shapes are timed in the same process as the model, so an
        // in-model rate and a standalone rate for the same shape can actually be compared —
        // across processes the host moves enough to swamp the difference being looked for.
        if (gemmBenchmark && modelPath is null)
        {
            BenchmarkGemm();
            return 0;
        }

        // A benchmark needs a graph and inputs of the right shape, not the reference's own
        // numbers: it measures time, and time does not depend on the values. Requiring a
        // reference dump for it means regenerating hundreds of megabytes of intermediates
        // before anyone can measure a kernel change.
        bool needsReference = referenceDir is not null || benchmarkRuns == 0;

        if (modelPath is null || (needsReference && referenceDir is null))
        {
            Console.Error.WriteLine(
                "usage: xberg-onnx-parity --model MODEL.onnx --reference REF_DIR [--atol A] [--rtol R] [--limit N] [--list-ops]");
            Console.Error.WriteLine(
                "       xberg-onnx-parity --model MODEL.onnx --benchmark N   (no reference needed)");
            return 2;
        }

        var stopwatch = Stopwatch.StartNew();
        var model = OnnxModel.Load(modelPath);
        Console.WriteLine(
            $"model: {Path.GetFileName(modelPath)}  opset {model.OpsetVersion}  " +
            $"{model.Nodes.Length} nodes  {model.Initializers.Count} initializers  ({stopwatch.ElapsedMilliseconds} ms)");

        if (listOps)
        {
            foreach (var group in model.Nodes.GroupBy(n => n.OpType).OrderByDescending(g => g.Count()))
                Console.WriteLine($"  {group.Key,-24} {group.Count()}");
            return 0;
        }

        var tensors = new Dictionary<string, ReferenceTensor>(StringComparer.Ordinal);
        var feeds = new Dictionary<string, Tensor>(StringComparer.Ordinal);
        JsonDocument? manifest = null;

        if (referenceDir is not null)
        {
            using var manifestStream = File.OpenRead(Path.Combine(referenceDir, "manifest.json"));
            manifest = JsonDocument.Parse(manifestStream);
            var root = manifest.RootElement;

            foreach (var property in root.GetProperty("tensors").EnumerateObject())
            {
                var value = property.Value;
                tensors[property.Name] = new ReferenceTensor(
                    value.GetProperty("file").GetString()!,
                    value.GetProperty("dtype").GetString()!,
                    value.GetProperty("shape").EnumerateArray().Select(d => d.GetInt32()).ToArray());
            }

            // Feed the exact inputs the reference ran with, so any divergence is the runtime's.
            foreach (var input in root.GetProperty("graph_inputs").EnumerateArray())
            {
                string name = input.GetProperty("name").GetString()!;
                if (!tensors.TryGetValue(name, out var reference))
                {
                    Console.Error.WriteLine($"reference dump has no tensor for input '{name}'");
                    return 3;
                }
                feeds[name] = NpyFile.Load(Path.Combine(referenceDir, reference.File));
            }
        }
        else
        {
            foreach (var input in model.FeedInputs)
                feeds[input.Name] = SyntheticFeed(input);
        }

        // Fusion removes the intermediate values the per-node comparison is built on, so the
        // harness executes the graph verbatim.
        var session = new OnnxSession(model, optimize: false);

        if (isolateOp is not null)
            return IsolateOperator(model, session, isolateOp, tensors, referenceDir,
                absoluteTolerance, relativeTolerance, reportLimit);

        if (benchmarkRuns > 0)
        {
            BenchmarkModel(model, feeds, benchmarkRuns);
            if (gemmBenchmark)
            {
                Console.WriteLine();
                BenchmarkGemm();
            }
            return 0;
        }

        var capture = new Dictionary<string, Tensor>(StringComparer.Ordinal);

        stopwatch.Restart();
        try
        {
            session.Run(feeds, capture);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"execution failed: {ex.Message}");
            // A partial capture still localises the failure, so report what did run.
            Console.Error.WriteLine($"produced {capture.Count} values before failing");
            ReportMismatches(model, capture, tensors, referenceDir, absoluteTolerance, relativeTolerance, reportLimit);
            return 1;
        }
        long runMs = stopwatch.ElapsedMilliseconds;
        Console.WriteLine($"executed {model.Nodes.Length} nodes in {runMs} ms, captured {capture.Count} values");

        int failures = ReportMismatches(
            model, capture, tensors, referenceDir, absoluteTolerance, relativeTolerance, reportLimit);

        if (detectionThreshold is { } threshold)
        {
            bool unfusedOk = CompareDetections(model, capture, tensors, referenceDir, threshold, "unfused");

            // Fusion rewrites the graph, so it has to be checked on its own terms: the
            // intermediate values it removes no longer exist to compare, but the declared
            // outputs do, and those are what a caller actually receives.
            Console.WriteLine();
            var fused = GraphOptimizer.Optimize(model);
            Console.WriteLine($"fused graph: {model.Nodes.Length} nodes -> {fused.Nodes.Length}");
            var fusedOutputs = new OnnxSession(fused, optimize: false).Run(feeds);
            bool fusedOk = CompareDetections(model, fusedOutputs, tensors, referenceDir, threshold, "fused");

            return unfusedOk && fusedOk ? 0 : 1;
        }

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Test every instance of one operator in isolation: each node is executed against the
    /// reference's own recorded inputs, so its output is judged on its own merits with no
    /// upstream drift folded in. A failure here is unambiguously a kernel bug.
    /// </summary>
    private static int IsolateOperator(
        OnnxModel model,
        OnnxSession session,
        string opType,
        Dictionary<string, ReferenceTensor> tensors,
        string referenceDir,
        float absoluteTolerance,
        float relativeTolerance,
        int reportLimit)
    {
        int tested = 0, failures = 0, skipped = 0;
        Console.WriteLine($"isolating '{opType}' nodes against reference inputs");

        for (int i = 0; i < model.Nodes.Length; i++)
        {
            var node = model.Nodes[i];
            if (!string.Equals(node.OpType, opType, StringComparison.OrdinalIgnoreCase)) continue;

            var env = new Dictionary<string, Tensor>(StringComparer.Ordinal);
            bool complete = true;
            foreach (string input in node.Inputs)
            {
                if (input.Length == 0) continue;
                if (model.Initializers.TryGetValue(input, out var initializer)) { env[input] = initializer; continue; }
                if (tensors.TryGetValue(input, out var reference))
                {
                    env[input] = NpyFile.Load(Path.Combine(referenceDir, reference.File));
                    continue;
                }
                // Values the dump skipped as too large cannot be reconstructed here.
                complete = false;
                break;
            }
            if (!complete) { skipped++; continue; }

            var outputs = session.ExecuteNode(node, env);
            for (int o = 0; o < node.Outputs.Length && o < outputs.Length; o++)
            {
                string name = node.Outputs[o];
                if (name.Length == 0 || outputs[o] is null) continue;
                if (!tensors.TryGetValue(name, out var reference)) { skipped++; continue; }

                var expected = NpyFile.Load(Path.Combine(referenceDir, reference.File));
                var diff = Compare(expected, outputs[o]!, absoluteTolerance, relativeTolerance);
                tested++;
                if (diff.Ok) continue;

                failures++;
                if (failures <= reportLimit)
                {
                    Console.WriteLine($"  FAIL #{i} '{node.Name}' -> {name}");
                    Console.WriteLine($"       {diff.Summary}");
                    foreach (var attribute in node.Attributes)
                        Console.WriteLine($"       attr {attribute.Name}: {Describe(attribute)}");
                }
            }
        }

        Console.WriteLine($"{tested - failures}/{tested} '{opType}' outputs match with reference inputs" +
                          (skipped > 0 ? $" ({skipped} skipped: inputs or outputs absent from the dump)" : ""));
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Walk node outputs in topological order and report those that diverge, first one first.
    /// Returns the number of diverging values.
    /// </summary>
    private static int ReportMismatches(
        OnnxModel model,
        Dictionary<string, Tensor> capture,
        Dictionary<string, ReferenceTensor> tensors,
        string referenceDir,
        float absoluteTolerance,
        float relativeTolerance,
        int reportLimit)
    {
        int compared = 0, failures = 0, missing = 0;
        double worstAbsolute = 0;
        var byOpType = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < model.Nodes.Length; i++)
        {
            var node = model.Nodes[i];
            foreach (string outputName in node.Outputs)
            {
                if (outputName.Length == 0) continue;
                if (!capture.TryGetValue(outputName, out var actual)) continue;
                if (!tensors.TryGetValue(outputName, out var reference)) { missing++; continue; }

                var expected = NpyFile.Load(Path.Combine(referenceDir, reference.File));
                compared++;

                var diff = Compare(expected, actual, absoluteTolerance, relativeTolerance);
                worstAbsolute = Math.Max(worstAbsolute, diff.MaxAbsolute);

                if (diff.Ok) continue;
                failures++;
                byOpType[node.OpType] = byOpType.GetValueOrDefault(node.OpType) + 1;
                if (failures > reportLimit)
                {
                    // Past the detailed budget, one line each: enough to see whether the
                    // divergence is one operator misbehaving or a single error propagating.
                    Console.WriteLine($"  #{i,-5} {node.OpType,-18} {diff.Summary}");
                    continue;
                }
                {
                    Console.WriteLine();
                    Console.WriteLine($"MISMATCH at node #{i} {node.OpType} '{node.Name}'");
                    Console.WriteLine($"  output      : {outputName}");
                    Console.WriteLine($"  expected    : {reference.Dtype}[{string.Join(",", reference.Shape)}]");
                    Console.WriteLine($"  actual      : {actual}");
                    Console.WriteLine($"  {diff.Summary}");
                    Console.WriteLine($"  inputs      : {string.Join(", ", node.Inputs)}");
                    foreach (var attribute in node.Attributes)
                        Console.WriteLine($"  attr {attribute.Name,-12}: {Describe(attribute)}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"compared {compared} values: {compared - failures} match, {failures} diverge" +
            (missing > 0 ? $", {missing} absent from the reference dump" : ""));
        Console.WriteLine($"largest absolute difference across all values: {worstAbsolute:G6}");

        if (byOpType.Count > 0)
        {
            Console.WriteLine("diverging values by operator:");
            foreach (var (op, count) in byOpType.OrderByDescending(p => p.Value))
                Console.WriteLine($"  {op,-20} {count}");
        }

        // The declared outputs are what callers actually consume, so they get reported
        // whatever happened upstream — intermediate drift that damps out before the end is a
        // very different situation from a wrong result.
        Console.WriteLine();
        Console.WriteLine("declared graph outputs:");
        foreach (string name in model.Outputs.Select(o => o.Name))
        {
            if (!capture.TryGetValue(name, out var actual) || !tensors.TryGetValue(name, out var reference))
            {
                Console.WriteLine($"  {name}: not comparable");
                continue;
            }
            var expected = NpyFile.Load(Path.Combine(referenceDir, reference.File));
            var diff = Compare(expected, actual, absoluteTolerance, relativeTolerance);
            Console.WriteLine($"  {name}: {(diff.Ok ? "MATCH" : "DIVERGE")} — {diff.Summary}");
        }
        return failures;
    }

    /// <summary>
    /// Decode both sides through RT-DETR's real postprocessing and compare the detections a
    /// caller would actually receive.
    /// <para>
    /// This is the comparison that decides whether the runtime is usable. Raw tensor drift
    /// is not: the network's box head runs through an inverse sigmoid, so values pinned near
    /// the clip bounds are amplified by four orders of magnitude before being squashed back
    /// down, and a difference of one float ULP upstream shows up as a large intermediate
    /// difference that never reaches the output. What matters is whether the same regions
    /// come out, with the same classes, at the same places.
    /// </para>
    /// </summary>
    private static bool CompareDetections(
        OnnxModel model,
        Dictionary<string, Tensor> capture,
        Dictionary<string, ReferenceTensor> tensors,
        string referenceDir,
        float threshold,
        string label)
    {
        var outputNames = model.Outputs.Select(o => o.Name).ToArray();
        var actual = new Dictionary<string, Tensor>(StringComparer.Ordinal);
        var expected = new Dictionary<string, Tensor>(StringComparer.Ordinal);
        foreach (string name in outputNames)
        {
            if (!capture.TryGetValue(name, out var a) || !tensors.TryGetValue(name, out var reference))
            {
                Console.Error.WriteLine($"cannot decode detections: '{name}' is unavailable");
                return false;
            }
            actual[name] = a;
            expected[name] = NpyFile.Load(Path.Combine(referenceDir, reference.File));
        }

        var mine = Decode(actual, outputNames, threshold);
        var theirs = Decode(expected, outputNames, threshold);

        Console.WriteLine();
        Console.WriteLine($"detections at threshold {threshold:F2} ({label}): " +
                          $"reference {theirs.Count}, C# {mine.Count}");

        int matched = 0;
        int rows = Math.Max(mine.Count, theirs.Count);
        for (int i = 0; i < rows; i++)
        {
            string left = i < theirs.Count ? Format(theirs[i]) : "—";
            string right = i < mine.Count ? Format(mine[i]) : "—";
            bool same = i < theirs.Count && i < mine.Count && Same(theirs[i], mine[i]);
            if (same) matched++;
            Console.WriteLine($"  {(same ? " " : "!")} {left,-52} | {right}");
        }

        bool ok = matched == rows && rows > 0;
        Console.WriteLine(ok
            ? $"all {rows} detections agree in class, confidence and geometry ({label})"
            : $"{matched}/{rows} detections agree ({label})");
        return ok;
    }

    /// <summary>Apply the confidence filter, class mapping and clamp, then sort by confidence.</summary>
    private static List<(long Label, float Score, float[] Box)> Decode(
        Dictionary<string, Tensor> outputs, string[] names, float threshold)
    {
        Tensor? labels = null, boxes = null, scores = null;
        foreach (string name in names)
        {
            var tensor = outputs[name];
            if (!tensor.IsFloat) labels ??= tensor;
            else if (tensor.Rank >= 3 || tensor.Shape[^1] == 4) boxes ??= tensor;
            else scores ??= tensor;
        }
        if (labels is null || boxes is null || scores is null) return [];

        var result = new List<(long, float, float[])>();
        for (int i = 0; i < scores.Count; i++)
        {
            float score = scores.GetFloat(i);
            if (score < threshold) continue;
            result.Add((labels.GetLong(i), score,
                [boxes.GetFloat(i * 4), boxes.GetFloat(i * 4 + 1), boxes.GetFloat(i * 4 + 2), boxes.GetFloat(i * 4 + 3)]));
        }
        result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return result;
    }

    /// <summary>
    /// Two detections agree when they name the same class and their geometry lands within a
    /// pixel — sub-pixel box differences are below what any downstream consumer resolves.
    /// </summary>
    private static bool Same((long Label, float Score, float[] Box) a, (long Label, float Score, float[] Box) b)
    {
        if (a.Label != b.Label) return false;
        if (Math.Abs(a.Score - b.Score) > 0.01f) return false;
        for (int i = 0; i < 4; i++) if (Math.Abs(a.Box[i] - b.Box[i]) > 1.0f) return false;
        return true;
    }

    private static string Format((long Label, float Score, float[] Box) d) => string.Create(
        CultureInfo.InvariantCulture,
        $"label {d.Label,2} conf {d.Score:F4} [{d.Box[0]:F1}, {d.Box[1]:F1}, {d.Box[2]:F1}, {d.Box[3]:F1}]");

    private readonly record struct Difference(bool Ok, double MaxAbsolute, string Summary);

    /// <summary>
    /// Element-wise comparison with a combined absolute/relative tolerance.
    /// <para>
    /// Both bounds are needed. Activations deep in the network reach magnitudes in the
    /// hundreds, where float32 rounding alone exceeds any fixed absolute bound; logits near
    /// zero would pass any relative bound no matter how wrong. A value is accepted when it
    /// is within <em>either</em>.
    /// </para>
    /// </summary>
    private static Difference Compare(Tensor expected, Tensor actual, float absoluteTolerance, float relativeTolerance)
    {
        if (expected.Count != actual.Count)
            return new Difference(false, double.PositiveInfinity,
                $"element count differs: expected {expected.Count}, got {actual.Count}");

        if (!expected.Shape.AsSpan().SequenceEqual(actual.Shape))
            return new Difference(false, double.PositiveInfinity,
                $"shape differs: expected [{string.Join(",", expected.Shape)}], got [{string.Join(",", actual.Shape)}]");

        double maxAbsolute = 0, maxRelative = 0;
        int worstIndex = -1, offending = 0;

        for (int i = 0; i < expected.Count; i++)
        {
            double e = expected.GetFloat(i);
            double a = actual.GetFloat(i);

            if (double.IsNaN(e) && double.IsNaN(a)) continue;
            if (double.IsNaN(e) != double.IsNaN(a) || double.IsInfinity(e) != double.IsInfinity(a))
            {
                offending++;
                if (worstIndex < 0) worstIndex = i;
                maxAbsolute = double.PositiveInfinity;
                continue;
            }

            double absolute = Math.Abs(e - a);
            double relative = Math.Abs(e) > 0 ? absolute / Math.Abs(e) : (absolute > 0 ? double.PositiveInfinity : 0);

            if (absolute > maxAbsolute) { maxAbsolute = absolute; worstIndex = i; }
            maxRelative = Math.Max(maxRelative, double.IsInfinity(relative) ? maxRelative : relative);

            if (absolute > absoluteTolerance && relative > relativeTolerance) offending++;
        }

        bool ok = offending == 0;
        string summary = ok
            ? $"max |diff| {maxAbsolute:G4}"
            : $"{offending}/{expected.Count} elements outside tolerance; max |diff| {maxAbsolute:G4} " +
              $"(max relative {maxRelative:G4})" +
              (worstIndex >= 0
                  ? $"; worst at [{worstIndex}]: expected {expected.GetFloat(worstIndex):G8}, got {actual.GetFloat(worstIndex):G8}"
                  : "");
        return new Difference(ok, maxAbsolute, summary);
    }

    private static string Describe(OnnxAttribute attribute) => attribute.Type switch
    {
        AttributeType.Int => attribute.Int.ToString(CultureInfo.InvariantCulture),
        AttributeType.Float => attribute.Float.ToString("G6", CultureInfo.InvariantCulture),
        AttributeType.String => attribute.String,
        AttributeType.Ints => "[" + string.Join(",", attribute.Ints) + "]",
        AttributeType.Floats => "[" + string.Join(",", attribute.Floats) + "]",
        AttributeType.Tensor => attribute.Tensor?.ToString() ?? "<tensor>",
        _ => attribute.Type.ToString(),
    };
}
