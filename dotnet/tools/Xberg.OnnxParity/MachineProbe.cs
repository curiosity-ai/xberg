using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Xberg.OnnxParity;

/// <summary>
/// Describes the machine and calibrates it before anything else is timed.
/// <para>
/// These runs happen on a VM whose underlying host can change between invocations, and a
/// 20% swing in available memory bandwidth looks exactly like a 20% optimisation. So every
/// measurement session starts by re-measuring two hardware ceilings — peak fused
/// multiply-add throughput and streaming memory bandwidth — against which the model timings
/// can be read. If those two numbers move between runs, the model numbers are not
/// comparable and nothing else in the output should be trusted.
/// </para>
/// <para>
/// Everything here is warmed up before it is timed. A cold .NET method runs interpreted or
/// through the quick JIT tier, and tiered compilation only promotes to optimised code after
/// roughly thirty calls, so an unwarmed microbenchmark measures the compiler rather than the
/// machine.
/// </para>
/// </summary>
internal static class MachineProbe
{
    /// <summary>Working set per array for the bandwidth probe. Must exceed last-level cache
    /// by a wide margin or the probe measures cache, not memory.</summary>
    private const int BandwidthElements = 24 * 1024 * 1024;   // 96 MB per array, 3 arrays

    public readonly record struct Calibration(
        double SingleThreadGflops, double MultiThreadGflops, double MulAddGflops, double BandwidthGBs);

    public static void PrintMachine()
    {
        Console.WriteLine("machine");
        Console.WriteLine($"  cpu            : {CpuModel()}");
        Console.WriteLine($"  logical cores  : {Environment.ProcessorCount}");
        Console.WriteLine($"  memory         : {DescribeMemory()}");
        Console.WriteLine($"  os             : {RuntimeInformation.OSDescription.Trim()} / {RuntimeInformation.OSArchitecture}");
        Console.WriteLine($"  runtime        : {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"  simd           : Vector<float>.Count={Vector<float>.Count}, " +
                          $"hardware accelerated={Vector.IsHardwareAccelerated}");
        Console.WriteLine($"  gc             : server={System.Runtime.GCSettings.IsServerGC}, " +
                          $"latency={System.Runtime.GCSettings.LatencyMode}");
    }

    /// <summary>
    /// Measure the machine's compute and bandwidth ceilings. Cheap enough (about a second)
    /// to run before every measurement.
    /// </summary>
    public static Calibration Calibrate()
    {
        double single = MeasureFlops(threads: 1, fused: true);
        double multi = MeasureFlops(threads: Environment.ProcessorCount, fused: true);
        double mulAdd = MeasureFlops(threads: Environment.ProcessorCount, fused: false);
        double wide = Avx512F.IsSupported ? Measure512Flops(Environment.ProcessorCount) : 0;
        double bandwidth = MeasureBandwidth();

        int cores = Environment.ProcessorCount;
        Console.WriteLine("calibration (re-measured each run; compare across runs to detect a changed host)");
        Console.WriteLine($"  fma 256-bit  1 thread  : {single,7:F1} GFLOP/s");
        Console.WriteLine($"  fma 256-bit {cores,2} threads: {multi,7:F1} GFLOP/s");
        Console.WriteLine($"  mul+add     {cores,2} threads: {mulAdd,7:F1} GFLOP/s   " +
                          $"({multi / Math.Max(mulAdd, 1e-9):F1}x slower; the JIT will not contract these into an FMA)");
        if (wide > 0)
            Console.WriteLine($"  fma 512-bit {cores,2} threads: {wide,7:F1} GFLOP/s   " +
                              $"<- the real ceiling ({wide / Math.Max(multi, 1e-9):F1}x the 256-bit path)");
        Console.WriteLine($"  triad bandwidth        : {bandwidth,7:F1} GB/s");
        return new Calibration(single, Math.Max(multi, wide), mulAdd, bandwidth);
    }

    /// <summary>
    /// Peak fused-multiply-add throughput, using eight independent accumulators so the
    /// measurement reflects issue width rather than the latency of a single dependency chain.
    /// All operands stay in registers, so this touches no memory.
    /// </summary>
    private static double MeasureFlops(int threads, bool fused)
    {
        const long IterationsPerThread = 20_000_000;
        Func<long, float> loop = fused ? FmaLoop : MulAddLoop;

        loop(1000);   // warm up: force tier-1 compilation before timing

        var stopwatch = Stopwatch.StartNew();
        if (threads == 1) loop(IterationsPerThread);
        else Parallel.For(0, threads, _ => loop(IterationsPerThread));
        double seconds = stopwatch.Elapsed.TotalSeconds;

        // Eight vector FMAs per iteration, each worth 2 * Vector<float>.Count flops.
        double flops = (double)IterationsPerThread * threads * 8 * 2 * Vector<float>.Count;
        return flops / seconds / 1e9;
    }

    /// <summary>
    /// Peak with explicit 512-bit vectors.
    /// <para>
    /// Measured separately because the portable <c>Vector&lt;T&gt;</c> stays 256 bits wide on
    /// these parts, so a ceiling derived from it understates the machine by roughly a factor
    /// of three and would make a kernel running at a fifth of the hardware's capability look
    /// nearly optimal.
    /// </para>
    /// </summary>
    private static double Measure512Flops(int threads)
    {
        const long IterationsPerThread = 20_000_000;
        Fma512Loop(1000);

        var stopwatch = Stopwatch.StartNew();
        Parallel.For(0, threads, _ => Fma512Loop(IterationsPerThread));
        double seconds = stopwatch.Elapsed.TotalSeconds;

        return (double)IterationsPerThread * threads * 8 * 2 * 16 / seconds / 1e9;
    }

    private static float Fma512Loop(long iterations)
    {
        Vector512<float> a0 = Vector512.Create(1.0000001f), a1 = Vector512.Create(1.0000002f);
        Vector512<float> a2 = Vector512.Create(1.0000003f), a3 = Vector512.Create(1.0000004f);
        Vector512<float> a4 = Vector512.Create(1.0000005f), a5 = Vector512.Create(1.0000006f);
        Vector512<float> a6 = Vector512.Create(1.0000007f), a7 = Vector512.Create(1.0000008f);
        var m = Vector512.Create(0.9999999f);
        var b = Vector512.Create(0.0000001f);

        for (long i = 0; i < iterations; i++)
        {
            a0 = Vector512.FusedMultiplyAdd(a0, m, b); a1 = Vector512.FusedMultiplyAdd(a1, m, b);
            a2 = Vector512.FusedMultiplyAdd(a2, m, b); a3 = Vector512.FusedMultiplyAdd(a3, m, b);
            a4 = Vector512.FusedMultiplyAdd(a4, m, b); a5 = Vector512.FusedMultiplyAdd(a5, m, b);
            a6 = Vector512.FusedMultiplyAdd(a6, m, b); a7 = Vector512.FusedMultiplyAdd(a7, m, b);
        }
        return (a0 + a1 + a2 + a3 + a4 + a5 + a6 + a7)[0];
    }

    /// <summary>Peak with explicitly fused multiply-adds — the real hardware ceiling.</summary>
    private static float FmaLoop(long iterations)
    {
        Vector<float> a0 = new(1.0000001f), a1 = new(1.0000002f), a2 = new(1.0000003f), a3 = new(1.0000004f);
        Vector<float> a4 = new(1.0000005f), a5 = new(1.0000006f), a6 = new(1.0000007f), a7 = new(1.0000008f);
        var m = new Vector<float>(0.9999999f);
        var b = new Vector<float>(0.0000001f);

        for (long i = 0; i < iterations; i++)
        {
            a0 = Vector.FusedMultiplyAdd(a0, m, b); a1 = Vector.FusedMultiplyAdd(a1, m, b);
            a2 = Vector.FusedMultiplyAdd(a2, m, b); a3 = Vector.FusedMultiplyAdd(a3, m, b);
            a4 = Vector.FusedMultiplyAdd(a4, m, b); a5 = Vector.FusedMultiplyAdd(a5, m, b);
            a6 = Vector.FusedMultiplyAdd(a6, m, b); a7 = Vector.FusedMultiplyAdd(a7, m, b);
        }
        // Consume the accumulators so nothing above can be optimised away.
        return (a0 + a1 + a2 + a3 + a4 + a5 + a6 + a7)[0];
    }

    /// <summary>
    /// The same arithmetic written as a separate multiply and add. Measured alongside the
    /// fused form because the gap between them is large and easy to pay by accident: the JIT
    /// leaves <c>a * b + c</c> as two instructions, since contracting it would change the
    /// rounding, so a kernel written the natural way silently runs at a fraction of peak.
    /// </summary>
    private static float MulAddLoop(long iterations)
    {
        Vector<float> a0 = new(1.0000001f), a1 = new(1.0000002f), a2 = new(1.0000003f), a3 = new(1.0000004f);
        Vector<float> a4 = new(1.0000005f), a5 = new(1.0000006f), a6 = new(1.0000007f), a7 = new(1.0000008f);
        var m = new Vector<float>(0.9999999f);
        var b = new Vector<float>(0.0000001f);

        for (long i = 0; i < iterations; i++)
        {
            a0 = a0 * m + b; a1 = a1 * m + b; a2 = a2 * m + b; a3 = a3 * m + b;
            a4 = a4 * m + b; a5 = a5 * m + b; a6 = a6 * m + b; a7 = a7 * m + b;
        }
        return (a0 + a1 + a2 + a3 + a4 + a5 + a6 + a7)[0];
    }

    /// <summary>
    /// STREAM triad — <c>a[i] = b[i] + scalar * c[i]</c> over arrays far larger than cache.
    /// Counts 16 bytes moved per element: two reads, one write, and the read-for-ownership
    /// the write itself causes.
    /// </summary>
    private static double MeasureBandwidth()
    {
        var a = GC.AllocateUninitializedArray<float>(BandwidthElements);
        var b = GC.AllocateUninitializedArray<float>(BandwidthElements);
        var c = GC.AllocateUninitializedArray<float>(BandwidthElements);
        Array.Fill(b, 1.5f);
        Array.Fill(c, 2.5f);

        Triad(a, b, c, 3.0f);   // warm up and fault the pages in

        var stopwatch = Stopwatch.StartNew();
        const int repeats = 3;
        for (int r = 0; r < repeats; r++) Triad(a, b, c, 3.0f);
        double seconds = stopwatch.Elapsed.TotalSeconds / repeats;

        double bytes = (double)BandwidthElements * 16;
        return bytes / seconds / 1e9;
    }

    private static void Triad(float[] a, float[] b, float[] c, float scalar)
    {
        // Parallel: one core rarely saturates a modern memory controller, and the model's
        // elementwise kernels are multi-threaded too, so this is the ceiling that matters.
        int chunk = Math.Max(1, a.Length / Environment.ProcessorCount);
        Parallel.For(0, (a.Length + chunk - 1) / chunk, block =>
        {
            int start = block * chunk;
            int end = Math.Min(start + chunk, a.Length);
            var scale = new Vector<float>(scalar);
            int width = Vector<float>.Count;
            int i = start;
            for (; i + width <= end; i += width)
            {
                var bv = new Vector<float>(b.AsSpan(i, width));
                var cv = new Vector<float>(c.AsSpan(i, width));
                (bv + scale * cv).CopyTo(a.AsSpan(i, width));
            }
            for (; i < end; i++) a[i] = b[i] + scalar * c[i];
        });
    }

    private static string CpuModel()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
            {
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.Ordinal))
                        return line[(line.IndexOf(':') + 1)..].Trim();
                }
            }
        }
        catch (IOException)
        {
            // Probing hardware details is best-effort; the calibration numbers below are the
            // part that actually matters.
        }
        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    private static string DescribeMemory()
    {
        string total = "unknown total";
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
            {
                foreach (string line in File.ReadLines("/proc/meminfo"))
                {
                    if (!line.StartsWith("MemTotal:", StringComparison.Ordinal)) continue;
                    var parts = line.Split(':', StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 && long.TryParse(parts[1].Replace(" kB", ""), out long kilobytes))
                        total = $"{kilobytes / 1024.0 / 1024.0:F1} GiB total";
                    break;
                }
            }
        }
        catch (IOException)
        {
            // Best-effort, as above.
        }

        var info = GC.GetGCMemoryInfo();
        string limit = info.TotalAvailableMemoryBytes > 0
            ? $", {info.TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0:F1} GiB visible to the runtime"
            : "";
        return total + limit;
    }
}
