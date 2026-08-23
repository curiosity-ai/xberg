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
    /// Fixed part of the per-document wall-clock guard for PDF extraction, in seconds. Also the
    /// floor: a one-page document still gets this long.
    /// </summary>
    public int PdfBaseSeconds { get; init; } = 25;

    /// <summary>
    /// Per-page allowance added to <see cref="PdfBaseSeconds"/>, in milliseconds, so the guard
    /// scales with the work a document actually represents.
    /// </summary>
    /// <remarks>
    /// Measured on the corpus, extraction is linear in page count at roughly 9-20 ms per page
    /// (the 4778-page Intel SDM extracts fully in ~55 s, the 1962-page `algebra_topology` in
    /// ~39 s), with no page behaving differently from its neighbours. 50 ms/page leaves about
    /// 2.5x headroom over the worst rate observed while still bounding a document that has
    /// genuinely stopped making progress.
    /// </remarks>
    public double PdfMillisecondsPerPage { get; init; } = 50.0;

    /// <summary>
    /// Ceiling on the computed guard, in seconds, however many pages a document has. Zero or
    /// negative disables the guard entirely.
    /// </summary>
    public int PdfMaxSecondsPerDocument { get; init; } = 3600;

    /// <summary>
    /// The wall-clock budget for a document of <paramref name="pageCount"/> pages, in seconds:
    /// <see cref="PdfBaseSeconds"/> plus <see cref="PdfMillisecondsPerPage"/> per page, clamped
    /// to <see cref="PdfMaxSecondsPerDocument"/>.
    /// </summary>
    /// <remarks>
    /// A fixed guard cannot serve both ends of this corpus: 25 s is generous for the median
    /// fixture and cuts a 4778-page manual off mid-document, while a flat budget large enough
    /// for the manual lets a small pathological file spin for just as long. Scaling by page
    /// count gives each document a budget proportional to the work it represents.
    ///
    /// For scale at the top end: upstream's own generator takes ~105 s per extraction on that
    /// Intel SDM — this port is roughly twice as fast — and its nominal 45 s guard never fires,
    /// because `extract` is CPU-bound synchronous work inside an async fn and tokio has no await
    /// point at which to cancel it. Goldens for such files are complete ~105 s extractions, so a
    /// guard that trips earlier cannot reproduce them however correct the extraction is.
    /// </remarks>
    internal double PdfBudgetSeconds(int pageCount)
    {
        double budget = PdfBaseSeconds + (PdfMillisecondsPerPage * Math.Max(pageCount, 0) / 1000.0);
        return Math.Clamp(budget, PdfBaseSeconds, PdfMaxSecondsPerDocument);
    }

    /// <summary>
    /// Absolute tick deadline for a document of <paramref name="pageCount"/> pages starting now,
    /// or <see cref="long.MaxValue"/> when <see cref="PdfMaxSecondsPerDocument"/> disables the
    /// guard.
    /// </summary>
    internal long PdfDeadlineFromNow(int pageCount)
    {
        if (PdfMaxSecondsPerDocument <= 0) return long.MaxValue;
        double seconds = PdfBudgetSeconds(pageCount);
        return DateTime.UtcNow.Ticks + (long)(seconds * TimeSpan.TicksPerSecond);
    }

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
            PdfBaseSeconds =
                Integer("XBERG_PDF_BASE_SECONDS") ?? defaults.PdfBaseSeconds,
            PdfMillisecondsPerPage =
                Number("XBERG_PDF_MS_PER_PAGE") ?? defaults.PdfMillisecondsPerPage,
            PdfMaxSecondsPerDocument =
                Integer("XBERG_PDF_MAX_SECONDS") ?? defaults.PdfMaxSecondsPerDocument,
        };

        static bool? Flag(string name) => Environment.GetEnvironmentVariable(name) switch
        {
            null or "" => null,
            "0" or "false" or "no" => false,
            _ => true,
        };

        static int? Integer(string name) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : null;

        static double? Number(string name) =>
            double.TryParse(Environment.GetEnvironmentVariable(name),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : null;
    }
}
