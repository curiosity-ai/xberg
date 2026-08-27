//! Thread-safety tests: concurrent reads and renders of a shared document.
//!
//! This replaces a test that previously built its fixture through
//! `crate::ffi::pdf_document_builder_*`, the C FFI builder API. That module
//! (`src/ffi.rs`) has been removed from this fork; a second migration moved
//! the fixtures onto `xberg_native_pdf::api::Pdf`; with the PDF writer/editor
//! stack removed, they are now built as raw hand-written PDF bytes (see
//! `tests/common`). The subject under test is unchanged: `PdfDocument`'s
//! internal `Mutex`-guarded reader (`lock_or_recover`, see `src/document.rs`)
//! must make concurrent access to one shared handle safe, whether that
//! handle is reached through the C ABI or, as here, directly through
//! `Arc<PdfDocument>` on the Rust API.
//!
//! * `concurrent_document_reads_no_panic` — 8 threads each open their own
//!   handle from shared bytes and extract text.
//! * `concurrent_renders_no_panic` — 8 threads render the same page from
//!   independent handles opened from shared bytes.
//! * `concurrent_render_page_fit_one_shared_handle_no_spurious_parse` — many
//!   threads call `render_page_fit` on a single *shared* `PdfDocument`,
//!   exercising the same internal reader-lock serialization that the C ABI
//!   depends on.
//! * `concurrent_render_multi_page_no_spurious_parse` — same shared-handle
//!   pattern, but against a 3-page PDF. NOTE: the original version of this
//!   test used a markdown-rendered PDF with an embedded TrueType font to
//!   exercise the embedded-font cmap classifier under concurrency; that
//!   fixture required the (now-removed) writer to produce a real subset
//!   font (FontFile2/CIDToGIDMap/W array), which cannot be hand-built
//!   without either running the writer or committing a generated binary
//!   fixture. This version only exercises the built-in Standard-14 path
//!   across multiple pages -- the embedded-font-specific concurrency
//!   coverage is a known gap; see the removal report for `xberg-native-pdf`.

mod common;

use std::sync::Arc;
use xberg_native_pdf::document::PdfDocument;

fn build_single_page_pdf(text: &str) -> Vec<u8> {
    let content = common::text_run_op(text, 72.0, 700.0, "Helvetica", 12.0);
    common::build_pdf_with_standard_fonts(content.as_bytes(), b"/Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]")
}

/// Build an N-page PDF, each page showing one line of Standard-14 text.
fn build_multi_page_pdf(pages_text: &[&str]) -> Vec<u8> {
    let n = pages_text.len();
    let font_obj = 3 + 2 * n;

    let mut objs: Vec<String> = Vec::with_capacity(2 + 2 * n + 1);
    objs.push("<< /Type /Catalog /Pages 2 0 R >>".to_string());

    let kids: String = (0..n).map(|i| format!("{} 0 R ", 3 + i)).collect();
    objs.push(format!("<< /Type /Pages /Kids [{}] /Count {n} >>", kids.trim_end()));

    for i in 0..n {
        let content_id = 3 + n + i;
        objs.push(format!(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {content_id} 0 R \
             /Resources << /Font << /Helvetica {font_obj} 0 R >> >> >>"
        ));
    }

    for text in pages_text {
        let content = common::text_run_op(text, 72.0, 700.0, "Helvetica", 12.0);
        objs.push(format!(
            "<< /Length {} >>\nstream\n{}\nendstream",
            content.len(),
            content
        ));
    }

    objs.push("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>".to_string());

    let mut buf = b"%PDF-1.4\n".to_vec();
    let mut offsets = vec![0usize];
    for (i, body) in objs.iter().enumerate() {
        offsets.push(buf.len());
        buf.extend_from_slice(format!("{} 0 obj\n{}\nendobj\n", i + 1, body).as_bytes());
    }
    common::finalize_pdf(buf, &offsets)
}

#[test]
fn concurrent_document_reads_no_panic() {
    let pdf_bytes: Arc<Vec<u8>> = Arc::new(build_single_page_pdf("Concurrent read test"));

    let handles: Vec<_> = (0..8)
        .map(|_| {
            let bytes = Arc::clone(&pdf_bytes);
            std::thread::spawn(move || {
                let doc = PdfDocument::from_bytes((*bytes).clone()).expect("open failed in thread");
                let text = doc.extract_text(0).expect("extract_text failed in thread");
                assert!(text.contains("Concurrent"), "unexpected text content: {text:.100}");
            })
        })
        .collect();

    for h in handles {
        h.join().expect("thread panicked");
    }
}

/// Rendering pipeline (tiny-skia, font rasteriser, etc.) must be safe to
/// call from multiple threads at once when each thread has its own handle
/// opened from shared bytes.
#[test]
fn concurrent_renders_no_panic() {
    use xberg_native_pdf::rendering::RenderOptions;

    let bytes: Arc<Vec<u8>> = Arc::new(build_single_page_pdf("Concurrent render test"));
    let opts = Arc::new(RenderOptions::with_dpi(72));

    let handles: Vec<_> = (0..8)
        .map(|_| {
            let b = Arc::clone(&bytes);
            let o = Arc::clone(&opts);
            std::thread::spawn(move || {
                let doc = PdfDocument::from_bytes((*b).clone()).expect("open PDF in thread");
                let img = xberg_native_pdf::rendering::render_page(&doc, 0, &o).expect("render must not fail");
                assert!(!img.data.is_empty(), "rendered image data must not be empty");
                assert!(img.width > 0 && img.height > 0, "rendered dimensions must be positive");
            })
        })
        .collect();

    for h in handles {
        h.join().expect("render thread panicked");
    }
}

/// Many threads calling `render_page_fit` against ONE shared `PdfDocument`
/// must never surface a spurious parse error. This is the direct Rust
/// equivalent of hammering a single shared FFI document handle from
/// multiple threads — `Arc<PdfDocument>` here plays the role the C binding's
/// shared native pointer played, and both routes rely on the same internal
/// `lock_or_recover()` serialization.
#[test]
fn concurrent_render_page_fit_one_shared_handle_no_spurious_parse() {
    use xberg_native_pdf::rendering::{RenderOptions, render_page_fit};

    const THREADS: usize = 8;
    const ITERS: usize = 16;

    let bytes = build_single_page_pdf("Shared-handle render race regression");
    let doc = Arc::new(PdfDocument::from_bytes(bytes).expect("open_from_bytes failed"));
    let opts = Arc::new(RenderOptions::default());

    let barrier = Arc::new(std::sync::Barrier::new(THREADS));
    let handles: Vec<_> = (0..THREADS)
        .map(|_| {
            let doc = Arc::clone(&doc);
            let opts = Arc::clone(&opts);
            let b = Arc::clone(&barrier);
            std::thread::spawn(move || -> Result<(), String> {
                b.wait();
                for i in 0..ITERS {
                    match render_page_fit(&doc, 0, 200, 200, &opts) {
                        Ok(img) => {
                            if img.data.is_empty() {
                                return Err(format!("iter {i}: render produced empty data"));
                            }
                        }
                        Err(e) => {
                            return Err(format!("iter {i}: render failed: {e}"));
                        }
                    }
                }
                Ok(())
            })
        })
        .collect();

    let mut failures = Vec::new();
    for h in handles {
        match h.join() {
            Ok(Ok(())) => {}
            Ok(Err(e)) => failures.push(e),
            Err(_) => failures.push("render thread panicked".to_string()),
        }
    }

    assert!(failures.is_empty(), "shared-handle render race: {failures:?}");
}

/// Same shared-handle pattern as above, but against a 3-page PDF, exercising
/// per-page state reset (font/color-space caches, etc.) under concurrency.
/// See the module doc comment for the embedded-font coverage this test used
/// to carry and no longer does.
#[test]
fn concurrent_render_multi_page_no_spurious_parse() {
    use xberg_native_pdf::rendering::{RenderOptions, render_page, render_page_fit};

    const THREADS: usize = 8;
    const ITERS: usize = 16;

    let bytes = build_multi_page_pdf(&["Page 1.", "Page 2.", "Page 3."]);
    let doc = Arc::new(PdfDocument::from_bytes(bytes).expect("open_from_bytes failed"));
    let opts = Arc::new(RenderOptions::default());
    let pages = doc.page_count().expect("page_count failed");
    assert_eq!(pages, 3, "expected 3 pages, got {pages}");

    let barrier = Arc::new(std::sync::Barrier::new(THREADS));
    let handles: Vec<_> = (0..THREADS)
        .map(|t| {
            let doc = Arc::clone(&doc);
            let opts = Arc::clone(&opts);
            let b = Arc::clone(&barrier);
            std::thread::spawn(move || -> Result<(), String> {
                b.wait();
                for i in 0..ITERS {
                    let page = i % pages;
                    let result = if (t + i) % 2 == 0 {
                        render_page_fit(&doc, page, 200, 260, &opts)
                    } else {
                        render_page(&doc, page, &opts)
                    };
                    match result {
                        Ok(img) if !img.data.is_empty() => {}
                        Ok(_) => {
                            return Err(format!("thread {t} iter {i} page {page}: empty render"));
                        }
                        Err(e) => return Err(format!("thread {t} iter {i} page {page}: {e}")),
                    }
                }
                Ok(())
            })
        })
        .collect();

    let mut failures = Vec::new();
    for h in handles {
        match h.join() {
            Ok(Ok(())) => {}
            Ok(Err(e)) => failures.push(e),
            Err(_) => failures.push("render thread panicked".to_string()),
        }
    }

    assert!(
        failures.is_empty(),
        "multi-page shared-handle render race: {failures:?}"
    );
}
