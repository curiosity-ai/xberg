//! Build script for the WASM cdylib.
//!
//! `.cargo/config.toml` sets `-C link-arg=--allow-multiple-definition` under
//! `[target.wasm32-unknown-unknown]`, but cargo discards every `target.*.rustflags`
//! from config the moment the `RUSTFLAGS` env var is set — and CI sets
//! `RUSTFLAGS=-D warnings`. That silently drops the flag, so the final cdylib link
//! fails with duplicate C libc symbols (`__assert_fail`, `__cxa_atexit`) that are
//! vendored independently by the WASI-built tesseract (`ocr-wasm`) and the
//! statically-linked tree-sitter grammar pack (`tree-sitter-wasm`).
//!
//! Build-script link args always reach the link regardless of `RUSTFLAGS`
//! precedence, so emit it here. `--allow-multiple-definition` is safe for these
//! symbols: both copies are standard-behaviour libc stubs, so first-definition-wins
//! is correct. Guarded to wasm so a non-wasm cdylib link never receives a flag its
//! linker (ld64 / link.exe) does not understand.
fn main() {
    if std::env::var("CARGO_CFG_TARGET_ARCH").as_deref() == Ok("wasm32") {
        println!("cargo::rustc-link-arg-cdylib=--allow-multiple-definition");
    }
}
