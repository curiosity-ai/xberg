//! Backend and session traits for the engine-neutral inference seam.
//!
//! Two traits split model loading from model execution:
//!
//! - [`InferenceBackend`] is a factory — it turns an ONNX artifact into a
//!   runnable session. The concrete backend is chosen at compile time by
//!   [`super::default_backend`]: ONNX Runtime on native builds, tract on no-ORT
//!   targets (later phase). A byte-buffer `load_from_memory` variant is added in
//!   the WASM/Android phase, where weights are embedded rather than read from a
//!   file.
//! - [`InferenceSession`] is the runner — it takes named [`InferenceTensor`]
//!   inputs and returns named outputs. `run` takes `&self` so a single session can
//!   be shared across threads (page-parallel layout), matching how xberg's ORT
//!   sessions are used today.
//!

use std::path::Path;

use crate::core::config::acceleration::AccelerationConfig;

use super::tensor::InferenceTensor;

/// An error from loading or running a model through the inference seam.
///
/// Callers map this into their module-specific error (e.g. `LayoutError`,
/// `XbergError`) at the migration site, so no engine detail leaks past the seam.
#[derive(Debug, thiserror::Error)]
pub enum InferenceError {
    /// The model could not be loaded (bad path/bytes, or the runtime is missing).
    #[error("failed to load inference model: {0}")]
    Load(String),
    /// Inference execution failed.
    #[error("inference run failed: {0}")]
    Run(String),
    /// A tensor could not be converted across the engine boundary.
    #[error("tensor conversion failed: {0}")]
    Tensor(String),
}

/// A loaded, runnable model.
///
/// `run` is `&self` (not `&mut self`) so one session can serve concurrent
/// callers; backends provide the necessary interior synchronization.
pub trait InferenceSession: Send + Sync {
    /// Run inference on the named inputs, returning the named outputs in the
    /// model's output order.
    fn run(&self, inputs: Vec<(String, InferenceTensor)>) -> Result<Vec<(String, InferenceTensor)>, InferenceError>;

    /// The model's input names, in graph order.
    fn input_names(&self) -> &[String];
}

/// A factory that loads ONNX models into [`InferenceSession`]s.
pub trait InferenceBackend: Send + Sync {
    /// Load a model from a filesystem path.
    fn load(
        &self,
        model_path: &Path,
        accel: Option<&AccelerationConfig>,
    ) -> Result<Box<dyn InferenceSession>, InferenceError>;

    /// Load a model with an explicit intra-op thread budget.
    ///
    /// Backends without configurable session threads may use the default
    /// implementation. Native ORT overrides this for batch layout planning.
    ///
    /// Only called from the layout models (`#[cfg(feature = "layout-detection")]`),
    /// so the default method is dead in feature slices without layout — allowed
    /// rather than cfg-gated, matching [`load_from_memory`](Self::load_from_memory).
    #[allow(dead_code)]
    fn load_with_thread_budget(
        &self,
        model_path: &Path,
        accel: Option<&AccelerationConfig>,
        thread_budget: usize,
    ) -> Result<Box<dyn InferenceSession>, InferenceError> {
        let _ = thread_budget;
        self.load(model_path, accel)
    }

    /// Load a model from an in-memory ONNX byte buffer.
    ///
    /// Used where there is no model file to read — WASM (weights embedded via
    /// `include_bytes!` or streamed from JS) and any caller that already holds the
    /// bytes. Native callers normally use [`load`](Self::load) with a cached path.
    ///
    /// The WASM embedded-weight path already wires a production caller: `crates/xberg-wasm`'s
    /// `detectLayout`/`detectOrientation` entry points hand JS-fetched model bytes to
    /// [`LayoutEngine::from_rtdetr_bytes`](crate::layout::LayoutEngine::from_rtdetr_bytes) and
    /// [`DocOrientationDetector::from_bytes`](crate::doc_orientation::DocOrientationDetector::from_bytes),
    /// both of which call through to this method on the tract backend. Those call sites are
    /// gated behind the `layout-tract`/`auto-rotate-tract` features (part of `wasm-target`), so
    /// in the default ORT-only feature slice this trait method is still reached only by the
    /// cross-engine parity tests — hence it stays `dead_code`-allowed rather than cfg-gated,
    /// matching [`load_with_thread_budget`](Self::load_with_thread_budget).
    #[allow(dead_code)]
    fn load_from_memory(
        &self,
        model_bytes: &[u8],
        accel: Option<&AccelerationConfig>,
    ) -> Result<Box<dyn InferenceSession>, InferenceError>;
}
