//! Golden reference generator for the Xberg C# port.
//!
//! For every fixture under a `test_documents` tree, this runs the original Rust
//! `xberg` extractors in each supported output format and writes a
//! `{filename}-results-rust.json` file next to the source. The C# port's test
//! CLI diffs its own extraction output against these golden files.
//!
//! Usage:
//!   xberg-reference-gen <root-dir> [--overwrite]

use std::path::{Path, PathBuf};

use serde::Serialize;
use xberg::{ExtractInput, ExtractionConfig, OutputFormat, extract};

/// The output formats we capture for each document.
const FORMATS: &[(&str, fn() -> OutputFormat)] = &[
    ("plain", || OutputFormat::Plain),
    ("markdown", || OutputFormat::Markdown),
    ("html", || OutputFormat::Html),
    ("json", || OutputFormat::Json),
];

/// File extensions we never treat as extraction inputs.
/// (Golden result files are excluded separately by their `-results-rust.json` suffix,
/// so real `.json` test inputs are still processed.)
const SKIP_EXTS: &[&str] = &[];

#[derive(Serialize)]
struct ReferenceOutput {
    /// Path relative to the root directory (forward slashes).
    file: String,
    /// Detected MIME type (best-effort).
    mime_type: String,
    /// Whether extraction succeeded for the primary (plain) run.
    success: bool,
    /// Error message if the primary run failed.
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<String>,
    /// Extraction method reported by the engine (native / ocr / mixed).
    #[serde(skip_serializing_if = "Option::is_none")]
    extraction_method: Option<String>,
    /// Full document metadata (serialized from the plain run).
    #[serde(skip_serializing_if = "Option::is_none")]
    metadata: Option<serde_json::Value>,
    /// Extracted tables (serialized from the plain run).
    #[serde(skip_serializing_if = "Option::is_none")]
    tables: Option<serde_json::Value>,
    /// Detected languages, if any.
    #[serde(skip_serializing_if = "Option::is_none")]
    detected_languages: Option<Vec<String>>,
    /// The `content` field rendered in each output format.
    content: std::collections::BTreeMap<String, String>,
}

#[tokio::main]
async fn main() {
    let mut args = std::env::args().skip(1);
    let root = match args.next() {
        Some(r) => PathBuf::from(r),
        None => {
            eprintln!("usage: xberg-reference-gen <root-dir> [--overwrite]");
            std::process::exit(2);
        }
    };
    let overwrite = args.any(|a| a == "--overwrite");

    if !root.is_dir() {
        eprintln!("error: {} is not a directory", root.display());
        std::process::exit(2);
    }

    let mut total = 0usize;
    let mut written = 0usize;
    let mut skipped = 0usize;
    let mut failed = 0usize;

    let entries: Vec<PathBuf> = walkdir::WalkDir::new(&root)
        .into_iter()
        .filter_map(|e| e.ok())
        .filter(|e| e.file_type().is_file())
        .map(|e| e.into_path())
        .filter(|p| is_candidate(p))
        .collect();

    for path in entries {
        total += 1;
        let out_path = reference_path(&path);
        if out_path.exists() && !overwrite {
            skipped += 1;
            continue;
        }

        let rel = path
            .strip_prefix(&root)
            .unwrap_or(&path)
            .to_string_lossy()
            .replace('\\', "/");

        match generate(&path, &rel).await {
            Ok(reference) => {
                let json = serde_json::to_string_pretty(&reference).unwrap();
                if let Err(e) = std::fs::write(&out_path, json) {
                    eprintln!("write error {}: {}", out_path.display(), e);
                    failed += 1;
                } else {
                    written += 1;
                    if !reference.success {
                        eprintln!("  (extraction error captured) {}", rel);
                    }
                }
            }
            Err(e) => {
                eprintln!("fatal {}: {}", rel, e);
                failed += 1;
            }
        }
    }

    eprintln!(
        "\nDone. candidates={total} written={written} skipped(existing)={skipped} failed={failed}"
    );
}

/// Replace machine-specific absolute-path fields in `metadata.additional` with the
/// portable relative path so golden files are reproducible across machines. Also drops
/// `extraction_duration_ms` (timing, non-deterministic).
fn normalize_metadata(mut meta: serde_json::Value, rel: &str) -> serde_json::Value {
    if let Some(obj) = meta.as_object_mut() {
        obj.remove("extraction_duration_ms");
        if let Some(additional) = obj.get_mut("additional").and_then(|a| a.as_object_mut()) {
            for key in ["source_uri", "final_uri"] {
                if additional.contains_key(key) {
                    additional.insert(key.to_string(), serde_json::Value::String(rel.to_string()));
                }
            }
        }
    }
    meta
}

/// Whether a path should be treated as an extraction input.
fn is_candidate(path: &Path) -> bool {
    let name = path.file_name().and_then(|n| n.to_str()).unwrap_or("");
    if name.ends_with("-results-rust.json") {
        return false;
    }
    if let Some(ext) = path.extension().and_then(|e| e.to_str()) {
        let ext = ext.to_ascii_lowercase();
        if SKIP_EXTS.contains(&ext.as_str()) {
            return false;
        }
    }
    // Skip obviously non-document files.
    !name.starts_with('.')
}

/// The `{filename}-results-rust.json` sibling path.
fn reference_path(path: &Path) -> PathBuf {
    let name = path.file_name().and_then(|n| n.to_str()).unwrap_or("file");
    path.with_file_name(format!("{name}-results-rust.json"))
}

async fn generate(path: &Path, rel: &str) -> Result<ReferenceOutput, String> {
    let path_str = path.to_string_lossy().to_string();
    let mime = xberg::detect_mime_type(path_str.clone(), true)
        .unwrap_or_else(|_| "application/octet-stream".to_string());

    let mut content = std::collections::BTreeMap::new();
    let mut success = true;
    let mut error = None;
    let mut extraction_method = None;
    let mut metadata = None;
    let mut tables = None;
    let mut detected_languages = None;

    for (name, fmt) in FORMATS {
        let mut config = ExtractionConfig::default();
        config.output_format = fmt();
        let input = ExtractInput::from_uri(path_str.clone());

        match extract(input, &config).await {
            Ok(result) => {
                if let Some(doc) = result.results.into_iter().next() {
                    content.insert((*name).to_string(), doc.content.clone());
                    if *name == "plain" {
                        extraction_method = doc.extraction_method.map(|m| m.as_str().to_string());
                        metadata = serde_json::to_value(&doc.metadata).ok().map(|m| normalize_metadata(m, rel));
                        tables = serde_json::to_value(&doc.tables).ok();
                        detected_languages = doc.detected_languages.clone();
                    }
                } else {
                    content.insert((*name).to_string(), String::new());
                }
            }
            Err(e) => {
                if *name == "plain" {
                    success = false;
                    error = Some(e.to_string());
                }
                content.insert((*name).to_string(), String::new());
            }
        }
    }

    Ok(ReferenceOutput {
        file: rel.to_string(),
        mime_type: mime,
        success,
        error,
        extraction_method,
        metadata,
        tables,
        detected_languages,
        content,
    })
}
