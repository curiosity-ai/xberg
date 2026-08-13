// Internal dev tool: stdout IS this binary's report output, so raw printing is intentional. ~keep
#![allow(clippy::print_stdout)]

use std::path::PathBuf;

use benchmark_harness::Result;
use benchmark_harness::runtime_responsiveness::{RuntimeResponsivenessConfig, run_runtime_responsiveness_diagnostic};
use clap::Parser;

#[derive(Debug, Parser)]
#[command(about = "Measure native Xberg cold-start Tokio scheduler responsiveness")]
struct Args {
    /// Input documents. Inputs are cycled until batch-size is reached.
    #[arg(short, long, required = true)]
    input: Vec<PathBuf>,

    #[arg(long, default_value_t = 4)]
    batch_size: usize,

    /// Inline JSON ExtractionConfig. Diagnostic cache and explicit thread flags take precedence.
    #[arg(long, value_name = "JSON")]
    config_json: Option<String>,

    #[arg(long)]
    max_threads: Option<usize>,

    #[arg(long)]
    max_concurrent: Option<usize>,
}

#[tokio::main(flavor = "current_thread")]
async fn main() -> Result<()> {
    let args = Args::parse();
    let report = run_runtime_responsiveness_diagnostic(&RuntimeResponsivenessConfig {
        inputs: args.input,
        batch_size: args.batch_size,
        extraction_config_json: args.config_json,
        max_threads: args.max_threads,
        max_concurrent_extractions: args.max_concurrent,
    })
    .await?;
    println!("{}", serde_json::to_string_pretty(&report)?);
    Ok(())
}
