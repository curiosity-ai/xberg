//! Regression test for GitHub #1387.
//!
//! On 1.0.9-1.0.14, `crates/xberg-ffi/Cargo.toml` dropped the baked `"full"` feature from
//! the `xberg` dependency (commit cf7fa0533d, "stop forcing heic/candle into no-heic
//! consumers") and replaced it with an explicit, hand-maintained feature list for the
//! `not(android/ios/windows/macos-x86_64)` target — the block that builds the native
//! libraries shipped for linux-x64, linux-aarch64, and macos-arm64 (osx-arm64) across every
//! `xberg-ffi` consumer (C, C#, Go, Java). That list omitted `"excel"`, so the sole extractor
//! for xlsx/xlsm/xlsb/xltx/ods (`crates/xberg/src/extractors/excel.rs`, gated by
//! `#[cfg(feature = "excel")]`, see `crates/xberg/Cargo.toml:55`) was compiled out of those
//! native libraries while the static format catalogue in `crates/xberg/src/core/mime.rs`
//! kept advertising xlsx/xlsm/xlsb/xltx/ods unconditionally — producing
//! `UnsupportedFormatException` for a format `ListSupportedFormats()` still reports as
//! supported.
//!
//! `crates/xberg/Cargo.toml` already carries a near-identical historical fix for
//! `windows-target` (see the comment above its own `"excel"` entry, referencing "the 1.0.4
//! Windows XLSX bug") — this test locks in the analogous fix for the desktop/server target
//! and prevents a future edit of the hand-maintained feature list from silently dropping
//! `"excel"` again.
//!
//! The manifest is parsed with the real `toml` crate (already a non-optional dependency of
//! this crate, see `crates/xberg/Cargo.toml`) rather than hand-rolled string slicing: an
//! earlier version of this test assumed the `default = [...]` feature array was always
//! formatted one entry per line, and silently produced a single bogus entry (the whole
//! array, as one string with embedded quotes) once `alef`'s regen reformatted it onto a
//! single line — turning the safeguard into a false positive that no longer proved anything.
#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test binaries print by design

use std::fs;
use std::path::Path;

use toml::Table;

/// Locates the `crates/xberg-ffi/Cargo.toml` manifest relative to this crate.
fn ffi_manifest_source() -> String {
    let manifest_dir = Path::new(env!("CARGO_MANIFEST_DIR"));
    let ffi_manifest = manifest_dir
        .parent()
        .expect("crates/xberg has a parent crates/ directory")
        .join("xberg-ffi")
        .join("Cargo.toml");
    fs::read_to_string(&ffi_manifest)
        .unwrap_or_else(|error| panic!("failed to read {}: {error}", ffi_manifest.display()))
}

/// Parses `source` as a TOML document, panicking with the parse error on malformed input.
fn parse_manifest(source: &str) -> Table {
    source
        .parse::<Table>()
        .unwrap_or_else(|error| panic!("failed to parse xberg-ffi/Cargo.toml as TOML: {error}"))
}

/// Reads the string array at `path` (a sequence of table keys) from `manifest`, panicking
/// with a precise description of what was expected if any segment is missing or not the
/// expected shape.
fn string_array_at(manifest: &Table, path: &[&str]) -> Vec<String> {
    let mut current = manifest
        .get(path[0])
        .unwrap_or_else(|| panic!("expected top-level key `{}` in xberg-ffi/Cargo.toml", path[0]));
    for (depth, key) in path.iter().enumerate().skip(1) {
        current = current.get(key).unwrap_or_else(|| {
            let traversed = path[..depth].join(".");
            panic!("expected key `{key}` under `{traversed}` in xberg-ffi/Cargo.toml")
        });
    }
    current
        .as_array()
        .unwrap_or_else(|| panic!("expected `{}` to be a TOML array, got {current:?}", path.join(".")))
        .iter()
        .map(|entry| {
            entry
                .as_str()
                .unwrap_or_else(|| panic!("expected `{}` entries to be strings, got {entry:?}", path.join(".")))
                .to_owned()
        })
        .collect()
}

/// The exact `cfg(...)` target key (without surrounding brackets/quotes) used for the
/// default native build target: linux-x64, linux-aarch64, and macos-arm64.
const DEFAULT_NATIVE_TARGET_CFG: &str = r#"cfg(not(any(target_os = "android", target_os = "ios", target_os = "windows", all(target_os = "macos", target_arch = "x86_64"))))"#;

/// The default target — used for linux-x64/linux-arm64/macos-arm64 native builds (Go, C,
/// Java, and C# runtime packs) — must carry `"excel"`, or xlsx/xlsm/xlsb/xltx/ods extraction
/// silently regresses to `UnsupportedFormat` on those platforms while `list_supported_formats`
/// keeps advertising the formats (GH#1387).
#[test]
fn ffi_default_target_dependency_must_enable_excel_feature() {
    let source = ffi_manifest_source();
    let manifest = parse_manifest(&source);

    let features = string_array_at(
        &manifest,
        &["target", DEFAULT_NATIVE_TARGET_CFG, "dependencies", "xberg", "features"],
    );

    assert!(
        features.iter().any(|feature| feature == "excel"),
        "the default (linux-x64/linux-arm64/macos-arm64) xberg-ffi target dependency must \
         request the \"excel\" feature so xlsx/xlsm/xlsb/xltx/ods extraction is compiled into \
         the shipped native library (GH#1387); got features = {features:?}"
    );
}

/// `xberg-ffi` must expose its own `excel` feature (mapping to `xberg/excel`) so binding
/// authors and CI can request it explicitly, and must enable it in its `default` feature set
/// so a plain `cargo build --release -p xberg-ffi` restores spreadsheet extraction.
#[test]
fn ffi_crate_declares_and_defaults_to_excel_feature() {
    let source = ffi_manifest_source();
    let manifest = parse_manifest(&source);

    let excel_feature = string_array_at(&manifest, &["features", "excel"]);
    assert_eq!(
        excel_feature,
        vec!["xberg/excel".to_owned()],
        "xberg-ffi must declare `excel = [\"xberg/excel\"]` in its [features] table; got {excel_feature:?}"
    );

    let default_features = string_array_at(&manifest, &["features", "default"]);
    assert!(
        default_features.iter().any(|feature| feature == "excel"),
        "xberg-ffi's default feature set must include \"excel\"; got {default_features:?}"
    );
}
