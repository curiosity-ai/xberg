```rust title="Rust"
use xberg::{extract, ExtractionConfig, ExtractInput, ImagePreprocessingConfig, OcrConfig, TesseractConfig};

#[tokio::main]
async fn main() -> xberg::Result<()> {
    let config = ExtractionConfig {
        ocr: Some(OcrConfig {
            backend: "tesseract".to_string(),
            tesseract_config: Some(TesseractConfig {
                preprocessing: Some(ImagePreprocessingConfig {
                    target_dpi: 300,
                    ..Default::default()
                }),
                ..Default::default()
            }),
            ..Default::default()
        }),
        ..Default::default()
    };

    let _output = extract(ExtractInput::from_uri("scanned.pdf"), &config).await?;
    Ok(())
}
```
