using System;

namespace Xberg.Core;

/// <summary>
/// Behavioural knobs for this port that have no counterpart in the Rust `ExtractionConfig`.
/// </summary>
/// <remarks>
/// <para>
/// These are deliberately kept out of <see cref="ExtractionConfig"/>, which mirrors upstream's
/// config type field for field and is part of the JSON wire format. What lives here is
/// port-local: guards against pathological input, and switches between two implementations of
/// the same stage that exist only because the port carries both.
/// </para>
/// <para>
/// <b>Nothing in the library reads the environment.</b> A knob that answers to an environment
/// variable read from inside library code is invisible to the consumer who linked it: it cannot
/// be discovered from the API, cannot be set per call, and silently changes behaviour based on
/// ambient process state. Test harnesses that need to drive the port and the Rust original from
/// one variable call <see cref="FromEnvironment"/> themselves and pass the result in — the
/// mapping is theirs to opt into, not the library's to assume.
/// </para>
/// </remarks>
public sealed class XbergOptions
{
    /// <summary>
    /// Options used when a call supplies none. Assign once at startup to configure the library
    /// process-wide; prefer setting <see cref="ExtractionConfig.Options"/> per call where the
    /// choice is not really global.
    /// </summary>
    public static XbergOptions Default { get; set; } = new();

    /// <summary>
    /// Take page spans from the ported pdf_oxide pipeline (default) rather than the older
    /// content-stream interpreter.
    /// </summary>
    /// <remarks>
    /// On by default: the ported producer wins on every dimension the corpus measures. The older
    /// interpreter stays reachable because it is still the source of the drawn paths the table
    /// tiers read, and because a per-fixture A/B is the fastest way to attribute a regression to
    /// the span layer.
    /// </remarks>
    public bool UsePortedPdfSpans { get; init; } = true;

    /// <summary>
    /// Per-document wall-clock guard for PDF extraction, in seconds, so a pathological file
    /// cannot hang extraction. Zero or negative disables the guard entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a guard against pathological input, not a throughput target, so it is set well
    /// clear of what legitimate documents need. Measured: the 4778-page Intel SDM extracts fully
    /// in ~55 s and the 1962-page `algebra_topology` in ~39 s, both scaling linearly at roughly
    /// 9-20 ms per page with no pathological page. The former default of 25 s cut both off
    /// mid-document, which is why they failed `plain` and drifted in and out of the corpus
    /// failure list between runs of identical code on a loaded machine.
    /// </para>
    /// <para>
    /// For scale: upstream's own golden generator takes ~105 s per extraction on that same Intel
    /// SDM — this port is roughly twice as fast — and its nominal 45 s guard never fires there,
    /// because `extract` is CPU-bound synchronous work inside an async fn and tokio has no await
    /// point at which to cancel it. A 25 s guard here could therefore never reproduce goldens
    /// that were themselves produced by a 105 s run.
    /// </para>
    /// </remarks>
    public int PdfMaxSecondsPerDocument { get; init; } = 120;

    /// <summary>
    /// Absolute tick deadline for a document starting now, or <see cref="long.MaxValue"/> when
    /// <see cref="PdfMaxSecondsPerDocument"/> disables the guard.
    /// </summary>
    internal long PdfDeadlineFromNow() =>
        PdfMaxSecondsPerDocument <= 0
            ? long.MaxValue
            : DateTime.UtcNow.Ticks + TimeSpan.FromSeconds(PdfMaxSecondsPerDocument).Ticks;

    /// <summary>
    /// Build options from <c>XBERG_*</c> environment variables, for test harnesses that drive
    /// this port and the Rust original from one set of variables. The library never calls this:
    /// a caller opts in explicitly and passes the result through
    /// <see cref="ExtractionConfig.Options"/> or <see cref="Default"/>.
    /// </summary>
    /// <remarks>
    /// Unset or unparseable variables leave the corresponding default in place, so a harness can
    /// set only the one variable it is varying.
    /// </remarks>
    public static XbergOptions FromEnvironment()
    {
        var defaults = new XbergOptions();
        return new XbergOptions
        {
            UsePortedPdfSpans =
                Flag("XBERG_OXIDE_SPANS") ?? defaults.UsePortedPdfSpans,
            PdfMaxSecondsPerDocument =
                Number("XBERG_PDF_MAX_SECONDS") ?? defaults.PdfMaxSecondsPerDocument,
        };

        static bool? Flag(string name) => Environment.GetEnvironmentVariable(name) switch
        {
            null or "" => null,
            "0" or "false" or "no" => false,
            _ => true,
        };

        static int? Number(string name) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : null;
    }
}
