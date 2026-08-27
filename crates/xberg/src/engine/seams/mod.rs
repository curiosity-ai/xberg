//! Rust-only engine extension seams.
//!
//! Cache and progress are injectable through [`EngineBuilder`](super::EngineBuilder).
//! The remaining traits expose standalone Rust primitives for structured extraction,
//! preset lookup, LLM calls, and model resolution; they are not Engine configuration.
//!
//! These are deliberately **not** part of the language-binding surface: the
//! whole `engine` module is a bare `pub mod engine;` in `lib.rs` whose files are
//! not listed in `alef.toml` `sources`, so the binding generator emits nothing
//! for them. The trait names and public seam types are also listed in
//! `alef.toml` `[crates.exclude] types` as belt-and-suspenders.
//!

mod cache;
mod progress;

#[cfg(feature = "liter-llm")]
mod llm_client;
#[cfg(feature = "presets")]
mod preset_resolver;
#[cfg(feature = "heuristics")]
mod structured_policy;

#[cfg(feature = "layout-detection")]
mod model_provider;

pub use cache::{CacheBackend, NoopCache};
pub use progress::{NoopProgressSink, ProgressEvent, ProgressSink};

#[cfg(feature = "liter-llm")]
pub use llm_client::{LiterLlmClient, LlmClient};
#[cfg(feature = "presets")]
pub use preset_resolver::{CorePresetResolver, PresetResolver};
#[cfg(feature = "heuristics")]
pub use structured_policy::{DefaultStructuredPolicy, StructuredPolicy};

#[cfg(feature = "layout-detection")]
pub use model_provider::{DefaultModelProvider, ModelId, ModelProvider};
