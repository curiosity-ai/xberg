//! Golden reference generator for the Xberg C# port.
//!
//! For every fixture under a `test_documents` tree, this runs the original Rust
//! `xberg` extractors in each supported output format and writes a
//! `{filename}-results-rust.json` file next to the source. The C# port's test
//! CLI diffs its own extraction output against these golden files.
//!
//! Usage:
//!   xberg-reference-gen <root-dir> [--overwrite] [--filter <substr>] [--out <dir>]
//!
//! `--out <dir>` writes the goldens into a mirror of the fixture tree rooted at
//! `<dir>` instead of next to each source file. That is what keeps a build with extra
//! `xberg` features (the `extended` cargo feature below) from clobbering the goldens
//! the default feature set produced: the two sets answer different questions and a
//! fixture's result differs between them wherever a feature changes the extraction.

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
    let args: Vec<String> = std::env::args().skip(1).collect();
    let root = match args.first() {
        Some(r) if !r.starts_with("--") => PathBuf::from(r),
        _ => {
            eprintln!(
                "usage: xberg-reference-gen <root-dir> [--overwrite] [--filter <substr>] [--out <dir>]"
            );
            std::process::exit(2);
        }
    };
    let overwrite = args.iter().any(|a| a == "--overwrite");
    // `--filter <substr>`: restrict the walk to fixtures whose path contains <substr>.
    // Regenerating one document is otherwise a whole-corpus run; the C# harness has the
    // same flag, so the two sides can be pointed at the same fixture with the same words.
    let filter = args
        .iter()
        .position(|a| a == "--filter")
        .and_then(|i| args.get(i + 1))
        .cloned();
    // `--out <dir>`: mirror the fixture tree under <dir> and write the goldens there.
    let out_root = args
        .iter()
        .position(|a| a == "--out")
        .and_then(|i| args.get(i + 1))
        .map(PathBuf::from);

    if !root.is_dir() {
        eprintln!("error: {} is not a directory", root.display());
        std::process::exit(2);
    }

    let mut total = 0usize;
    let mut written = 0usize;
    let mut skipped = 0usize;
    let mut failed = 0usize;

    // Sorted walk. `xberg` keeps a process-global font cache, so a document's output
    // depends on what was extracted before it in the same process: readdir order would
    // make a run irreproducible on its own machine, let alone across two.
    let entries: Vec<PathBuf> = walkdir::WalkDir::new(&root)
        .sort_by_file_name()
        .into_iter()
        .filter_map(|e| e.ok())
        .filter(|e| e.file_type().is_file())
        .map(|e| e.into_path())
        .filter(|p| is_candidate(p))
        .filter(|p| match &filter {
            Some(f) => p.to_string_lossy().contains(f.as_str()),
            None => true,
        })
        .collect();

    for path in entries {
        total += 1;
        let out_path = reference_path(&path, &root, out_root.as_deref());
        if out_path.exists() && !overwrite {
            skipped += 1;
            continue;
        }

        let rel = path
            .strip_prefix(&root)
            .unwrap_or(&path)
            .to_string_lossy()
            .replace('\\', "/");

        // Run each fixture on its own task so a panic inside a backend parser costs
        // one golden rather than aborting the run and leaving the corpus half-written
        // (`mathemascii` panics on a char boundary parsing one asciidoc fixture).
        let (task_path, task_rel) = (path.clone(), rel.clone());
        let outcome = tokio::spawn(async move { generate(&task_path, &task_rel).await }).await;
        let outcome = match outcome {
            Ok(r) => r,
            Err(join_err) => {
                eprintln!("panic {rel}: {join_err}");
                failed += 1;
                continue;
            }
        };

        match outcome {
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
/// Where a fixture's golden goes.
///
/// Next to the fixture by default. With `--out`, into the same relative position under
/// `out_root`, whose directories are created on demand — so one corpus can carry several
/// golden sets, one per feature configuration, without any of them overwriting another.
fn reference_path(path: &Path, root: &Path, out_root: Option<&Path>) -> PathBuf {
    let name = path.file_name().and_then(|n| n.to_str()).unwrap_or("file");
    let file = format!("{name}-results-rust.json");
    match out_root {
        None => path.with_file_name(file),
        Some(out) => {
            let rel = path.strip_prefix(root).unwrap_or(path);
            let mut dest = out.join(rel);
            dest.set_file_name(file);
            if let Some(parent) = dest.parent() {
                let _ = std::fs::create_dir_all(parent);
            }
            dest
        }
    }
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

        // Guard against pathological inputs that spin forever in a backend parser.
        let fut = extract(input, &config);
        let result = match tokio::time::timeout(std::time::Duration::from_secs(45), fut).await {
            Ok(r) => r,
            Err(_) => Err(xberg::XbergError::Other("timed out after 45s".to_string())),
        };

        match result {
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
