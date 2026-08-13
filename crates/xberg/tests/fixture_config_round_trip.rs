//! Assert every e2e fixture's `config` block actually takes effect.
//!
//! Only two config structs carry `#[serde(deny_unknown_fields)]` — `ExtractionConfig`
//! and `UrlExtractionConfig`. Every nested config silently ignores unknown keys, so
//! `{"chunking":{"maxChars":500}}` deserializes cleanly, the setting never applies, and
//! the generated e2e test still passes because it asserts on output that looks plausible
//! either way. A typo'd nested key is invisible at every layer.
//!
//! This closes that hole without changing runtime behaviour: parse each fixture's config
//! into the real `ExtractionConfig`, serialize it back, and assert every leaf the fixture
//! asked for survives the round trip. A key that serde dropped is a key that did nothing.
//!
//! It also catches the subtler variant that bit a fixture-authoring pass: using the Rust
//! field name where serde declares a different wire name. `ChunkingConfig::max_characters`
//! is `#[serde(rename = "max_chars", alias = "max_characters")]` and `overlap` is
//! `rename = "max_overlap"`, so a fixture written from the struct definition rather than
//! the wire contract can be wrong in a way that greps clean.
//!
//! Fixtures whose config names a feature this build does not enable are skipped with a
//! message rather than failed, so the test stays honest under any feature set.

#![allow(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro)] // ~keep: test/bench binaries print by design; org logging policy exempts tests

use std::path::{Path, PathBuf};

use xberg::core::config::ExtractionConfig;

/// Repository root, derived from this crate's manifest directory.
fn repo_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .and_then(Path::parent)
        .expect("crates/xberg has a grandparent")
        .to_path_buf()
}

/// Every `fixtures/**/*.json` path, sorted for deterministic reporting.
fn fixture_paths(root: &Path) -> Vec<PathBuf> {
    let mut found = Vec::new();
    let mut stack = vec![root.join("fixtures")];
    while let Some(dir) = stack.pop() {
        let Ok(entries) = std::fs::read_dir(&dir) else {
            continue;
        };
        for entry in entries.flatten() {
            let path = entry.path();
            if path.is_dir() {
                stack.push(path);
            } else if path.extension().is_some_and(|ext| ext == "json") {
                found.push(path);
            }
        }
    }
    found.sort();
    found
}

/// Collect every leaf path in `value` as (dotted_path, leaf), skipping nulls.
///
/// Nulls are skipped because an explicitly-null optional is indistinguishable from an
/// absent one after a round trip through `skip_serializing_if = "Option::is_none"`.
fn leaves(value: &serde_json::Value, prefix: &str, out: &mut Vec<(String, serde_json::Value)>) {
    match value {
        serde_json::Value::Object(map) => {
            for (key, child) in map {
                let path = if prefix.is_empty() {
                    key.clone()
                } else {
                    format!("{prefix}.{key}")
                };
                leaves(child, &path, out);
            }
        }
        serde_json::Value::Null => {}
        leaf => out.push((prefix.to_string(), leaf.clone())),
    }
}

/// Look a dotted path up in a JSON object.
fn lookup<'a>(value: &'a serde_json::Value, path: &str) -> Option<&'a serde_json::Value> {
    let mut current = value;
    for segment in path.split('.') {
        current = current.get(segment)?;
    }
    Some(current)
}

/// Prove the detector above actually detects — a check that cannot fail is not a check.
///
/// Uses a literal config rather than mutating a fixture on disk, because the fixture
/// corpus is shared with other agents mid-run.
#[test]
fn a_typod_nested_key_is_reported_as_dropped() {
    // `maxChars` is camelCase; the wire name is `max_chars`. ChunkingConfig does not
    // deny unknown fields, so this parses cleanly and the setting never applies.
    let typod = serde_json::json!({"chunking": {"chunker_type": "markdown", "maxChars": 500}});
    let parsed: ExtractionConfig = serde_json::from_value(typod.clone()).expect("parses despite the typo");
    let round_tripped = serde_json::to_value(&parsed).expect("serializes");

    let mut requested = Vec::new();
    leaves(&typod, "", &mut requested);

    let dropped: Vec<&String> = requested
        .iter()
        .filter(|(path, wanted)| lookup(&round_tripped, path) != Some(wanted))
        .map(|(path, _)| path)
        .collect();

    assert_eq!(
        dropped,
        vec!["chunking.maxChars"],
        "the round-trip check must flag `chunking.maxChars` as dropped and leave the valid \
         `chunker_type` alone; got {dropped:?}"
    );
}

#[test]
fn every_fixture_config_key_survives_a_round_trip() {
    let root = repo_root();
    let paths = fixture_paths(&root);
    assert!(
        !paths.is_empty(),
        "no fixtures found under {}/fixtures — has the corpus moved?",
        root.display()
    );

    let mut checked = 0usize;
    let mut skipped = Vec::new();
    let mut failures = Vec::new();

    for path in &paths {
        let Ok(text) = std::fs::read_to_string(path) else {
            continue;
        };
        let Ok(fixture) = serde_json::from_str::<serde_json::Value>(&text) else {
            continue;
        };
        let Some(config_json) = fixture.get("config") else {
            continue;
        };
        if config_json.as_object().is_none_or(serde_json::Map::is_empty) {
            continue;
        }

        let parsed: ExtractionConfig = match serde_json::from_value(config_json.clone()) {
            Ok(config) => config,
            Err(error) => {
                // An unknown top-level key here means the feature is compiled out of this
                // build, not that the fixture is wrong — `ExtractionConfig` does deny
                // unknown fields, so a genuine typo is already a hard error elsewhere.
                skipped.push(format!("{}: {error}", path.display()));
                continue;
            }
        };

        let round_tripped = serde_json::to_value(&parsed).expect("ExtractionConfig serializes");

        let mut requested = Vec::new();
        leaves(config_json, "", &mut requested);

        for (leaf_path, wanted) in requested {
            match lookup(&round_tripped, &leaf_path) {
                Some(got) if got == &wanted => {}
                Some(got) => failures.push(format!(
                    "{}: `{leaf_path}` round-tripped to {got} instead of {wanted}",
                    path.display()
                )),
                None => failures.push(format!(
                    "{}: `{leaf_path}` was DROPPED — the key does nothing. Check it against the \
                     serde wire name (e.g. ChunkingConfig uses `max_chars`, not `max_characters`).",
                    path.display()
                )),
            }
        }
        checked += 1;
    }

    for message in &skipped {
        eprintln!("SKIP (feature not enabled in this build): {message}");
    }

    assert!(
        failures.is_empty(),
        "{} fixture config key(s) do not survive a round trip through ExtractionConfig, so they \
         silently do nothing:\n  {}",
        failures.len(),
        failures.join("\n  ")
    );

    assert!(
        checked > 0,
        "no fixture carried a non-empty config that this build could parse — the assertion above \
         proved nothing. Re-run with more features enabled."
    );
    eprintln!(
        "checked {checked} fixture config(s) across {} fixture file(s)",
        paths.len()
    );
}
