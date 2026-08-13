//! Decode-side smoke tests for the vendored libheif bindings.
//!
//! Confirms the bindings still produce sensible RGBA pixel data and dimensions
//! for HEIC / HEIF / AVIF after the Rust 2024 modernisation. Requires the system
//! `libheif` (with libde265 + libaom) to be installed at runtime.
//!
//! Fixtures live in the repository-root `test_documents/` submodule. No HEIF
//! binaries are stored in the xberg codebase itself.
//!
//! `test_documents/` is a bucket-fetched corpus (see
//! `test_documents/scripts/fetch_corpus.py`) and is not present in a bare
//! checkout, so fixtures are read at runtime rather than baked in via
//! `include_bytes!`; tests skip cleanly with a greppable message when the
//! corpus hasn't been fetched.

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests

use xberg_libheif::{ColorSpace, HeifContext, LibHeif, RgbChroma};

/// Read a `test_documents/` fixture at runtime, skipping the calling test
/// with a greppable message when the bucket-fetched corpus is absent.
fn load_fixture(relative: &str) -> Option<Vec<u8>> {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../test_documents")
        .join(relative);
    match std::fs::read(&path) {
        Ok(bytes) => Some(bytes),
        Err(e) => {
            eprintln!(
                "SKIP: fixture {} not available ({e}); run `python3 test_documents/scripts/fetch_corpus.py` to fetch it",
                path.display()
            );
            None
        }
    }
}

fn decode_to_rgba(bytes: &[u8]) -> (u32, u32, usize) {
    let lib = LibHeif::new();
    let ctx = HeifContext::read_from_bytes(bytes).expect("HeifContext::read_from_bytes");
    let handle = ctx.primary_image_handle().expect("primary_image_handle");
    let image = lib
        .decode(&handle, ColorSpace::Rgb(RgbChroma::Rgba), None)
        .expect("LibHeif::decode");
    let planes = image.planes();
    let plane = planes.interleaved.expect("interleaved RGBA plane");
    (image.width(), image.height(), plane.data.len())
}

#[test]
fn decodes_heic_to_rgba() {
    let Some(bytes) = load_fixture("images/test.heic") else {
        return;
    };
    let (w, h, len) = decode_to_rgba(&bytes);
    assert!(w > 0 && h > 0, "expected positive dimensions, got {w}x{h}");
    assert!(
        len >= (w as usize) * (h as usize) * 4,
        "interleaved plane shorter than declared dimensions"
    );
}

#[test]
fn decodes_heif_to_rgba() {
    let Some(bytes) = load_fixture("images/test.heif") else {
        return;
    };
    let (w, h, _) = decode_to_rgba(&bytes);
    assert!(w > 0 && h > 0);
}

#[test]
fn decodes_avif_to_rgba() {
    let Some(bytes) = load_fixture("images/test.avif") else {
        return;
    };
    let (w, h, _) = decode_to_rgba(&bytes);
    assert!(w > 0 && h > 0);
}

#[test]
fn decodes_heif_with_alpha_to_rgba() {
    let Some(bytes) = load_fixture("images/alpha.heif") else {
        return;
    };
    let (w, h, _) = decode_to_rgba(&bytes);
    assert!(w > 0 && h > 0);
}

#[test]
fn returns_clear_error_for_non_heif_bytes() {
    let result = HeifContext::read_from_bytes(b"not a heif file");
    assert!(result.is_err(), "non-HEIF bytes should not parse");
}
