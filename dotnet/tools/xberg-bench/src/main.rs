//! Per-file extraction benchmark for the Rust implementation, written to be comparable with
//! the C# `xberg-bench`: same fixture walk, same output format, same TSV columns.
//!
//! Rust needs no JIT warm-up, but it does need a warm-up pass for a different reason:
//! `pdf_oxide` keeps a process-global font cache, so a document extracted early in a process
//! pays parsing costs that a later one does not. Timing a cold process would charge the first
//! few PDFs for work the rest get free. The same `--warmup` passes therefore run here, so both
//! sides are measured in the same steady state.
//!
//! Usage: xberg-bench <root-dir> [--iters N] [--warmup N] [--ext EXT] [--out FILE]

use std::io::Write;
use std::path::Path;
use std::time::Instant;

use xberg::{ExtractInput, ExtractionConfig, OutputFormat, extract};

async fn run_once(path: &Path) -> bool {
    let config = ExtractionConfig {
        output_format: OutputFormat::Plain,
        ..Default::default()
    };
    match extract(ExtractInput::from_uri(path.to_string_lossy().to_string()), &config).await {
        Ok(results) => !results.is_empty(),
        Err(_) => false,
    }
}

#[tokio::main]
async fn main() {
    let mut iters = 5usize;
    let mut warmup = 2usize;
    let mut root: Option<String> = None;
    let mut out_path: Option<String> = None;
    let mut only: Option<String> = None;

    let args: Vec<String> = std::env::args().skip(1).collect();
    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "--iters" => { i += 1; iters = args[i].parse().unwrap(); }
            "--warmup" => { i += 1; warmup = args[i].parse().unwrap(); }
            "--out" => { i += 1; out_path = Some(args[i].clone()); }
            "--ext" => { i += 1; only = Some(args[i].to_lowercase()); }
            other => { if root.is_none() { root = Some(other.to_string()); } }
        }
        i += 1;
    }

    let Some(root) = root else {
        eprintln!("usage: xberg-bench <root-dir> [--iters N] [--warmup N] [--ext EXT] [--out FILE]");
        std::process::exit(2);
    };

    let mut files: Vec<std::path::PathBuf> = walkdir::WalkDir::new(&root)
        .into_iter()
        .filter_map(|e| e.ok())
        .filter(|e| e.file_type().is_file())
        .map(|e| e.into_path())
        .filter(|p| !p.to_string_lossy().ends_with("-results-rust.json"))
        .filter(|p| match &only {
            None => true,
            Some(ext) => p.extension().map(|e| e.to_string_lossy().to_lowercase()) == Some(ext.clone()),
        })
        .collect();
    files.sort();

    eprintln!("[rs] {} files, warmup={warmup}, iters={iters}", files.len());

    for w in 0..warmup {
        for f in &files {
            let _ = run_once(f).await;
        }
        eprintln!("[rs] warmup pass {}/{} done", w + 1, warmup);
    }

    let mut sink: Box<dyn Write> = match &out_path {
        Some(p) => Box::new(std::fs::File::create(p).unwrap()),
        None => Box::new(std::io::stdout()),
    };
    writeln!(sink, "rel\text\tbytes\tok\tmin_ms\tmedian_ms").unwrap();

    let root_path = Path::new(&root);
    for f in &files {
        let rel = f.strip_prefix(root_path).unwrap_or(f).to_string_lossy().replace('\\', "/");
        let ext = f.extension().map(|e| e.to_string_lossy().to_lowercase()).unwrap_or_default();
        let bytes = std::fs::metadata(f).map(|m| m.len()).unwrap_or(0);
        let mut ok = false;
        let mut samples = Vec::with_capacity(iters);

        for _ in 0..iters {
            let t0 = Instant::now();
            ok = run_once(f).await;
            samples.push(t0.elapsed().as_secs_f64() * 1000.0);
        }

        samples.sort_by(f64::total_cmp);
        let min = samples[0];
        let median = if iters % 2 == 1 {
            samples[iters / 2]
        } else {
            (samples[iters / 2 - 1] + samples[iters / 2]) / 2.0
        };

        writeln!(sink, "{rel}\t{ext}\t{bytes}\t{}\t{min:.4}\t{median:.4}", if ok { 1 } else { 0 }).unwrap();
    }

    sink.flush().unwrap();
    eprintln!("[rs] done");
}
