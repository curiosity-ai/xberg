//! Round-trip fixtures through the real DocTags parser and renderer, printing JSON.
//!
//! Usage: `doctags-probe <fixture> [<fixture>...]`
//!
//! For each fixture: render it to DocTags (`OutputFormat::DocTags`), then feed that stream back
//! through the DocTags extractor and render it again. Both stages are printed, so a diff against
//! the C# port pins a divergence to the renderer or to the parser.

use xberg::{ExtractInput, ExtractionConfig, OutputFormat, extract};

async fn render(input: ExtractInput) -> String {
    let config = ExtractionConfig {
        output_format: OutputFormat::DocTags,
        ..Default::default()
    };
    match extract(input, &config).await {
        Ok(result) => result
            .results
            .into_iter()
            .next()
            .map(|r| r.content)
            .unwrap_or_default(),
        Err(e) => format!("<<error: {e}>>"),
    }
}

#[tokio::main]
async fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let mut out = serde_json::Map::new();

    for path in &args {
        // A real Docling stream is fed in as DocTags bytes rather than by extension: the corpus
        // names them `*.doctags.txt`, which resolves as plain text, so the extractor would never
        // otherwise see one.
        let first = if path.ends_with(".doctags.txt") {
            let bytes = std::fs::read(path).unwrap_or_default();
            render(ExtractInput::from_bytes(bytes, "text/vnd.docling.doctags", None)).await
        } else {
            render(ExtractInput::from_uri(path.clone())).await
        };
        // Feed the rendered stream back in under the DocTags MIME so the extractor claims it.
        let second = render(ExtractInput::from_bytes(
            first.clone().into_bytes(),
            "text/vnd.docling.doctags",
            None,
        ));
        let second = second.await;

        let mut entry = serde_json::Map::new();
        entry.insert("render".to_string(), serde_json::Value::String(first));
        entry.insert("roundtrip".to_string(), serde_json::Value::String(second));
        out.insert(path.clone(), serde_json::Value::Object(entry));
    }

    println!("{}", serde_json::to_string_pretty(&out).unwrap());
}
