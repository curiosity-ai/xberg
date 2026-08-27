```rust title="Rust"
use xberg::{extract, ExtractionConfig, ExtractInput, PdfConfig, HierarchyConfig};

#[tokio::main]
async fn main() -> xberg::Result<()> {
    let config = ExtractionConfig {
        pdf_options: Some(PdfConfig {
            hierarchy: Some(HierarchyConfig {
                enabled: true,
                k_clusters: 3,
                include_bbox: true,
            }),
            ..Default::default()
        }),
        ..Default::default()
    };

    let output = extract(ExtractInput::from_uri("document.pdf"), &config).await?;
    for page in output.results[0].pages.iter().flatten() {
        if let Some(hierarchy) = &page.hierarchy {
            println!("Page {}: {} hierarchy blocks", page.page_number, hierarchy.block_count);
        }
    }
    Ok(())
}
```
