//! Render fixtures through the real `StyledHtmlRenderer` and print the result as JSON.
//!
//! Usage: `htmlstyled-probe <fixture> [<fixture>...]`
//!
//! Each fixture is rendered under every combination of theme, `embed_css` and class prefix the
//! C# port's test covers, so a diff of the two JSON blobs pins any divergence to a single case.

use xberg::core::config::{HtmlOutputConfig, HtmlTheme};
use xberg::{ExtractInput, ExtractionConfig, OutputFormat, extract};

fn cases() -> Vec<(&'static str, HtmlOutputConfig)> {
    vec![
        ("unstyled-embed", HtmlOutputConfig::default()),
        (
            "default-embed",
            HtmlOutputConfig {
                theme: HtmlTheme::Default,
                ..Default::default()
            },
        ),
        (
            "github-embed",
            HtmlOutputConfig {
                theme: HtmlTheme::GitHub,
                ..Default::default()
            },
        ),
        (
            "dark-embed",
            HtmlOutputConfig {
                theme: HtmlTheme::Dark,
                ..Default::default()
            },
        ),
        (
            "light-embed",
            HtmlOutputConfig {
                theme: HtmlTheme::Light,
                ..Default::default()
            },
        ),
        (
            "default-noembed",
            HtmlOutputConfig {
                theme: HtmlTheme::Default,
                embed_css: false,
                ..Default::default()
            },
        ),
        (
            "unstyled-prefix",
            HtmlOutputConfig {
                class_prefix: "zz-".to_string(),
                ..Default::default()
            },
        ),
        (
            "unstyled-usercss",
            HtmlOutputConfig {
                css: Some(".kb-p { color: red; }".to_string()),
                ..Default::default()
            },
        ),
    ]
}

#[tokio::main]
async fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let mut out = serde_json::Map::new();

    for path in &args {
        let mut per_case = serde_json::Map::new();
        for (name, html) in cases() {
            let config = ExtractionConfig {
                output_format: OutputFormat::Html,
                html_output: Some(html),
                ..Default::default()
            };
            let content = match extract(ExtractInput::from_uri(path.clone()), &config).await {
                Ok(result) => result
                    .results
                    .into_iter()
                    .next()
                    .map(|r| r.content.clone())
                    .unwrap_or_default(),
                Err(e) => format!("<<error: {e}>>"),
            };
            per_case.insert(name.to_string(), serde_json::Value::String(content));
        }
        out.insert(path.clone(), serde_json::Value::Object(per_case));
    }

    println!("{}", serde_json::to_string_pretty(&out).unwrap());
}
