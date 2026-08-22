// Per-file extraction benchmark for the C# port, written to be comparable with the
// Rust `xberg-bench` binary: same fixture walk, same output format, same TSV columns.
//
// The measurement discipline that matters here is warm-up. A cold .NET process spends its
// first passes in the interpreter and the quick JIT tier, and the extractor graph is large
// enough that tiered promotion takes a while to settle. Timing that measures the compiler,
// not the code. So every file is extracted `--warmup` times before any timing starts, and
// each timed file is then run `--iters` times with the minimum reported alongside the median:
// the minimum is the least noisy estimate of steady-state cost, the median shows dispersion.

using System.Diagnostics;
using System.Globalization;
using Xberg.Core;
using Xberg.Types;

int iters = 5, warmup = 2;
string? root = null, outPath = null;
string? only = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--iters": iters = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--warmup": warmup = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--out": outPath = args[++i]; break;
        case "--ext": only = args[++i]; break;
        default: root ??= args[i]; break;
    }
}

if (root is null)
{
    Console.Error.WriteLine("usage: xberg-bench <root-dir> [--iters N] [--warmup N] [--ext EXT] [--out FILE]");
    return 2;
}

var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    .Where(p => !p.EndsWith("-results-rust.json", StringComparison.Ordinal))
    .Where(p => only is null || Path.GetExtension(p).TrimStart('.').Equals(only, StringComparison.OrdinalIgnoreCase))
    .OrderBy(p => p, StringComparer.Ordinal)
    .ToList();

Console.Error.WriteLine($"[cs] {files.Count} files, warmup={warmup}, iters={iters}");

var extractor = new Extractor();
var config = new ExtractionConfig { OutputFormat = OutputFormat.Plain };

// A run counts as OK only if it produced output; a file both sides fail on is still timed,
// because refusing a malformed document is work the implementation does and a fair
// comparison has to include it.
static bool RunOnce(Extractor ex, string path, ExtractionConfig cfg)
{
    try
    {
        var res = ex.Extract(ExtractInput.FromUri(path), cfg);
        return res.Results.Count > 0;
    }
    catch { return false; }
}

// Warm-up: the whole corpus, so every extractor's code path is promoted out of the
// quick tier before anything is timed. Failures here are ignored on purpose.
for (int w = 0; w < warmup; w++)
{
    foreach (var f in files) RunOnce(extractor, f, config);
    Console.Error.WriteLine($"[cs] warmup pass {w + 1}/{warmup} done");
}

var sw = new Stopwatch();
using var outw = outPath is null ? Console.Out : new StreamWriter(outPath);
outw.WriteLine("rel\text\tbytes\tok\tmin_ms\tmedian_ms");

var samples = new double[iters];
foreach (var f in files)
{
    string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
    string ext = Path.GetExtension(f).TrimStart('.').ToLowerInvariant();
    long bytes = new FileInfo(f).Length;
    bool ok = false;

    for (int i = 0; i < iters; i++)
    {
        sw.Restart();
        ok = RunOnce(extractor, f, config);
        sw.Stop();
        samples[i] = sw.Elapsed.TotalMilliseconds;
    }

    Array.Sort(samples);
    double min = samples[0];
    double median = iters % 2 == 1 ? samples[iters / 2] : (samples[iters / 2 - 1] + samples[iters / 2]) / 2.0;

    outw.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{rel}\t{ext}\t{bytes}\t{(ok ? 1 : 0)}\t{min:F4}\t{median:F4}"));
}

outw.Flush();
Console.Error.WriteLine("[cs] done");
return 0;
