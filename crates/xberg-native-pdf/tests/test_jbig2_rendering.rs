//! Regression test: JBIG2-compressed scanner PDFs must render non-blank.
//!
//! Scanners emitting Mixed Raster Content put a page's entire text layer in JBIG2
//! `/ImageMask`s. The mask path handled CCITT only and then required packed 1-bit rows,
//! so a compressed JBIG2 bitstream failed the length check, the error was swallowed, and
//! the page still rendered `Ok` — just without ink. Pages then OCR'd to nothing.
//!
//! The always-on coverage for the fix is the packing unit tests in
//! `src/extractors/images.rs` (`image_mask_packing_tests`) and the codec-detection tests
//! in `src/rendering/page_renderer.rs`. This file adds a render-level assertion against a
//! real scanner PDF, which cannot be vendored here — see the constant below.

// ~keep: test/bench binaries print by design; org logging policy exempts tests
#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)]

mod tests {
    use xberg_native_pdf::document::PdfDocument;
    use xberg_native_pdf::rendering::{RenderOptions, render_page};

    /// Point this at a PDF whose pages carry JBIG2 `/ImageMask`s to run the assertion.
    ///
    /// No such PDF is committed here: the ones we have are third-party corpus documents
    /// and this is a public repository. When the variable is unset the test skips; when
    /// it is set the test runs for real and fails on a blank render or a missing file —
    /// it never passes by accident, which is what the previous version of this test did
    /// (it hardcoded an absolute path in a contributor's home directory and `return`ed
    /// when the file was missing, so it could not fail on any other machine).
    const FIXTURE_ENV: &str = "XBERG_NATIVE_PDF_JBIG2_FIXTURE";

    #[test]
    fn jbig2_scanner_pdf_renders_non_blank() {
        let Ok(path) = std::env::var(FIXTURE_ENV) else {
            eprintln!("skipping: set {FIXTURE_ENV} to a PDF with JBIG2 /ImageMask pages");
            return;
        };

        assert!(
            std::path::Path::new(&path).exists(),
            "{FIXTURE_ENV} is set to {path}, which does not exist"
        );

        let doc = PdfDocument::open(&path).expect("open the JBIG2 fixture");

        // as_raw() returns premultiplied RGBA8888 — PNG bytes cannot be scanned for ink. ~keep
        let opts = RenderOptions::with_dpi(72).as_raw();
        let rendered = render_page(&doc, 0, &opts).expect("render page 0");

        // Count DARK pixels, not merely non-white. A Mixed Raster Content page is a
        // low-resolution grayscale background plus a JBIG2 text mask; the background
        // alone contributes a handful of off-white pixels even when every mask is
        // dropped. Measured on a 16-page scanner PDF at 72 dpi, page 0 gives:
        //
        //     mask decoded   non-white 45,605   dark 30,675
        //     mask dropped   non-white    107   dark      0
        //
        // so a "non-white > 100" bar passes on a page with no ink at all. Ink is the
        // only thing that produces dark pixels here. ~keep
        let dark = rendered
            .data
            .chunks(4)
            .filter(|px| px[0] < 100 && px[1] < 100 && px[2] < 100)
            .count();

        assert!(
            dark > 5_000,
            "page 0 of {path} rendered without ink ({dark} dark pixels) — \
             the JBIG2 /ImageMask was dropped instead of decoded"
        );
    }
}
