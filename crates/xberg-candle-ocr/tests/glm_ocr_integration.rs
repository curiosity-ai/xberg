//! Network-gated smoke test for GLM-OCR end-to-end inference: constructs the engine,
//! runs OCR on a fixture image, and checks the output text for expected content and
//! for degenerate repeated-token runs. Ignored by default; downloads ~3GB of weights.

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests
#![cfg(feature = "glm-ocr")]

use xberg_candle_ocr::DevicePreference;
use xberg_candle_ocr::models::glm_ocr::{GlmOcrEngine, GlmOcrTask};

/// Network-gated smoke test for GLM-OCR end-to-end inference.
///
/// Downloads ~3GB of model weights on first run (cached in ~/.cache/huggingface).
/// Subsequent runs use cached weights. Marked with #[ignore] so it only runs on
/// `cargo test -- --ignored --nocapture`.
#[test]
#[ignore = "downloads 3GB of GLM-OCR weights from HuggingFace Hub"]
fn glm_ocr_smoke_ocr_on_fixture() {
    let image_bytes = include_bytes!("../../../fixtures/images/test_hello_world.png");

    let device = DevicePreference::Auto.select().expect("Failed to select device");

    let dtype = xberg_candle_ocr::DType::F32;

    eprintln!("Constructing GLM-OCR engine (downloading weights if needed)...");
    let engine = GlmOcrEngine::new(GlmOcrTask::Ocr, device, dtype).expect("Failed to construct GLM-OCR engine");

    eprintln!("Engine constructed. Running inference on test image...");

    let output = engine.process_image(image_bytes).expect("Failed to process image");

    eprintln!("Inference completed successfully!");
    eprintln!("Output content length: {} chars", output.content.len());
    eprintln!("Is structured markdown: {}", output.is_structured_markdown);
    eprintln!("Output text:\n{}", output.content);

    assert!(!output.content.is_empty(), "Output text should not be empty");
    assert!(
        output.content.len() > 5,
        "Output text should have more than 5 characters"
    );
    // NOTE: `is_structured_markdown` is informational only. The fixture is a
    eprintln!("is_structured_markdown: {}", output.is_structured_markdown);

    let lower = output.content.to_lowercase();
    assert!(
        lower.contains("hello") || lower.contains("world"),
        "Expected output to contain \"hello\" or \"world\"; got {:?}",
        output.content
    );

    fn longest_repeated_ngram_run(text: &str, n: usize) -> usize {
        let tokens: Vec<&str> = text.split_whitespace().collect();
        if tokens.len() < n * 2 {
            return 0;
        }
        let mut max_run = 0usize;
        for start in 0..tokens.len() - n + 1 {
            let pattern = &tokens[start..start + n];
            let mut run = 1usize;
            let mut next = start + n;
            while next + n <= tokens.len() && &tokens[next..next + n] == pattern {
                run += 1;
                next += n;
            }
            max_run = max_run.max(run);
        }
        max_run
    }

    assert!(
        longest_repeated_ngram_run(&output.content, 3) < 5,
        "Detected degenerate-repeat output: {}...",
        &output.content[..200.min(output.content.len())]
    );

    eprintln!("\n✓ Smoke test passed!");
}
