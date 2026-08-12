//! Wall-clock watchdog for blocking HuggingFace model downloads.
//!
//! This is a self-contained copy of the guard in the `xberg` crate's `model_download` module.
//! `xberg-candle-ocr` is a leaf crate that `xberg` depends on, so it cannot reach back into that
//! module; the helper is small and dependency-free, so duplicating it here is cheaper than
//! inverting the dependency. Keep the two copies in sync (same env var, same tracing target).

use std::path::{Path, PathBuf};
use std::time::Duration;

use sha2::{Digest, Sha256};

/// Resolve a pinned model artifact through hf-hub's standard shared cache.
///
/// Cache lookup is always attempted before network access, and the Python-compatible
/// offline flags prevent a cache miss from making a request.
pub(crate) fn hf_download(
    repo_id: &str,
    filename: &str,
    revision: &str,
    cache_dir: Option<&Path>,
    expected_sha256: &str,
) -> Result<PathBuf, String> {
    let mut builder = hf_hub::HFClientBuilder::new();
    if let Some(cache_dir) = cache_dir {
        builder = builder.cache_dir(cache_dir);
    }
    let api = builder
        .build_sync()
        .map_err(|error| format!("HF API init failed: {error}"))?;
    if let Some(path) = cached_file(&api, repo_id, filename, revision)?
        && verify_sha256(&path, expected_sha256)?
    {
        return Ok(path);
    }
    if hf_offline_mode() {
        return Err(format!(
            "Hugging Face offline mode is enabled and '{filename}' from {repo_id}@{revision} is not available in the local cache"
        ));
    }

    let key = format!("{repo_id}/{filename}@{revision}");
    let lock = download_lock(&key);
    let repo_id = repo_id.to_string();
    let filename = filename.to_string();
    let revision = revision.to_string();
    let expected_sha256 = expected_sha256.to_string();
    let lock_file = artifact_lock_file(cache_dir, &key)?;
    with_download_deadline(&key, move || {
        let _guard = lock.lock().unwrap_or_else(std::sync::PoisonError::into_inner);
        fs2::FileExt::lock_exclusive(&lock_file)
            .map_err(|error| format!("Failed to lock Hugging Face cache artifact: {error}"))?;
        if let Some(path) = cached_file(&api, &repo_id, &filename, &revision)? {
            if verify_sha256(&path, &expected_sha256)? {
                return Ok(path);
            }
            remove_corrupt_cache_entry(&path)?;
        }
        let (owner, name) = hf_hub::split_id(&repo_id);
        let path = api
            .model(owner, name)
            .download_file()
            .filename(filename.clone())
            .revision(revision.clone())
            .force_download(true)
            .send()
            .map_err(|error| format!("Failed to download '{filename}' from {repo_id}@{revision}: {error}"))?;
        if !verify_sha256(&path, &expected_sha256)? {
            remove_corrupt_cache_entry(&path)?;
            return Err(format!(
                "SHA-256 mismatch for '{filename}' from {repo_id}@{revision} after refresh"
            ));
        }
        Ok(path)
    })
}

fn verify_sha256(path: &Path, expected: &str) -> Result<bool, String> {
    use std::io::Read;

    let mut file = std::fs::File::open(path)
        .map_err(|error| format!("Failed to open cached model '{}': {error}", path.display()))?;
    let mut hasher = Sha256::new();
    let mut buffer = [0u8; 1024 * 1024];
    loop {
        let read = file
            .read(&mut buffer)
            .map_err(|error| format!("Failed to hash cached model '{}': {error}", path.display()))?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    Ok(hex_digest(hasher.finalize().as_ref()) == expected)
}

fn hex_digest(bytes: &[u8]) -> String {
    use std::fmt::Write;

    let mut output = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        write!(&mut output, "{byte:02x}").expect("writing to a String cannot fail");
    }
    output
}

fn artifact_lock_file(cache_dir: Option<&Path>, key: &str) -> Result<std::fs::File, String> {
    let cache_dir = cache_dir
        .map(Path::to_path_buf)
        .unwrap_or_else(hf_hub::resolve_cache_dir);
    let lock_dir = cache_dir.join(".locks").join("xberg-candle-ocr");
    std::fs::create_dir_all(&lock_dir)
        .map_err(|error| format!("Failed to create Hugging Face lock directory: {error}"))?;
    let digest_bytes = Sha256::digest(key.as_bytes());
    let digest = hex_digest(digest_bytes.as_ref());
    std::fs::OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(lock_dir.join(format!("{digest}.lock")))
        .map_err(|error| format!("Failed to open Hugging Face artifact lock: {error}"))
}

fn remove_corrupt_cache_entry(path: &Path) -> Result<(), String> {
    let target = std::fs::canonicalize(path).ok();
    if path.exists() || path.symlink_metadata().is_ok() {
        std::fs::remove_file(path)
            .map_err(|error| format!("Failed to remove corrupt cache entry '{}': {error}", path.display()))?;
    }
    if let Some(target) = target
        && target != path
        && target.exists()
    {
        std::fs::remove_file(&target)
            .map_err(|error| format!("Failed to remove corrupt cache blob '{}': {error}", target.display()))?;
    }
    Ok(())
}

fn cached_file(
    api: &hf_hub::HFClientSync,
    repo_id: &str,
    filename: &str,
    revision: &str,
) -> Result<Option<std::path::PathBuf>, String> {
    let (owner, name) = hf_hub::split_id(repo_id);
    match api
        .model(owner, name)
        .download_file()
        .filename(filename)
        .revision(revision)
        .local_files_only(true)
        .send()
    {
        Ok(path) => Ok(Some(path)),
        Err(hf_hub::HFError::LocalEntryNotFound { .. } | hf_hub::HFError::EntryNotFound { .. }) => Ok(None),
        Err(error) => Err(format!(
            "Failed to inspect Hugging Face cache for {repo_id}/{filename}: {error}"
        )),
    }
}

fn hf_offline_mode() -> bool {
    ["HF_HUB_OFFLINE", "HUGGINGFACE_HUB_OFFLINE"]
        .iter()
        .filter_map(std::env::var_os)
        .any(|value| {
            value
                .to_str()
                .is_some_and(|value| matches!(value.trim().to_ascii_lowercase().as_str(), "1" | "on" | "yes" | "true"))
        })
}

fn download_lock(key: &str) -> std::sync::Arc<std::sync::Mutex<()>> {
    use std::collections::HashMap;
    use std::sync::{Arc, Mutex, OnceLock};

    static LOCKS: OnceLock<Mutex<HashMap<String, Arc<Mutex<()>>>>> = OnceLock::new();
    let mut locks = LOCKS
        .get_or_init(|| Mutex::new(HashMap::new()))
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    Arc::clone(locks.entry(key.to_string()).or_default())
}

/// Default wall-clock ceiling for a single model-file download. hf-hub builds its ureq agent with
/// no read/connect timeout, so a stalled or firewalled connection to HuggingFace makes the blocking
/// `ApiRepo::get()` hang forever — silently wedging the whole extraction pipeline (observed: OCR /
/// embedding model pulls parked at 0% CPU behind a host firewall). We cap each fetch so a dead
/// network fails fast and the caller can degrade. Generous by default because a cold GB-scale model
/// legitimately takes minutes; override with `XBERG_MODEL_DOWNLOAD_TIMEOUT_SECS`.
const DEFAULT_MODEL_DOWNLOAD_TIMEOUT: Duration = Duration::from_secs(300);

/// Resolve the model-download deadline, honoring `XBERG_MODEL_DOWNLOAD_TIMEOUT_SECS` (seconds; a
/// value of 0 or unparseable falls back to the default).
pub(crate) fn model_download_timeout() -> Duration {
    std::env::var("XBERG_MODEL_DOWNLOAD_TIMEOUT_SECS")
        .ok()
        .and_then(|v| v.parse::<u64>().ok())
        .filter(|&s| s > 0)
        .map(Duration::from_secs)
        .unwrap_or(DEFAULT_MODEL_DOWNLOAD_TIMEOUT)
}

/// Run a blocking model-download closure under a hard wall-clock deadline so a hung network cannot
/// block the pipeline indefinitely. The closure runs on a detached worker thread; if it does not
/// finish within `model_download_timeout()` we log a warning and return `Err`, letting the caller
/// degrade (skip the model-backed backend) rather than hang. The worker thread cannot be
/// force-killed — it stays parked on the socket until the OS tears the connection down — but it
/// holds no lock the pipeline needs, so progress resumes. `label` names the fetch in the log/error.
pub(crate) fn with_download_deadline<T, F>(label: &str, f: F) -> Result<T, String>
where
    F: FnOnce() -> Result<T, String> + Send + 'static,
    T: Send + 'static,
{
    let deadline = model_download_timeout();
    let (tx, rx) = std::sync::mpsc::sync_channel::<Result<T, String>>(1);
    std::thread::Builder::new()
        .name("xberg-model-download".into())
        .spawn(move || {
            let _ = tx.send(f());
        })
        .map_err(|e| format!("failed to spawn model-download thread: {e}"))?;
    match rx.recv_timeout(deadline) {
        Ok(result) => result,
        Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {
            tracing::warn!(
                target: "xberg::model_download",
                label = %label,
                timeout_secs = deadline.as_secs(),
                "model download exceeded deadline (network unreachable / firewalled?); aborting so \
                 the extraction pipeline does not hang. Set XBERG_MODEL_DOWNLOAD_TIMEOUT_SECS to adjust."
            );
            Err(format!(
                "model download '{label}' timed out after {}s (HuggingFace unreachable?)",
                deadline.as_secs()
            ))
        }
        Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => {
            Err(format!("model-download thread for '{label}' died unexpectedly"))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{Duration, Instant};

    #[test]
    fn with_download_deadline_returns_ok_for_fast_closure() {
        let result = with_download_deadline("fast", || Ok::<i32, String>(7));
        assert_eq!(result, Ok(7), "fast closure must return its Ok value verbatim");
    }

    #[test]
    fn deadline_reads_env_override_and_aborts_a_hung_closure() {
        #[allow(unsafe_code)]
        unsafe {
            std::env::set_var("XBERG_MODEL_DOWNLOAD_TIMEOUT_SECS", "1");
        }
        assert_eq!(
            model_download_timeout(),
            Duration::from_secs(1),
            "explicit override must win"
        );

        let started = Instant::now();
        let result = with_download_deadline("hung", || {
            std::thread::sleep(Duration::from_secs(10));
            Ok::<(), String>(())
        });
        let elapsed = started.elapsed();
        #[allow(unsafe_code)]
        unsafe {
            std::env::remove_var("XBERG_MODEL_DOWNLOAD_TIMEOUT_SECS");
        }

        let err = result.expect_err("a closure that outlives the deadline must return Err");
        assert!(err.contains("timed out"), "error must mention the timeout, got: {err}");
        assert!(
            elapsed < Duration::from_secs(3),
            "guard must fire near the 1s deadline, not wait out the 10s sleep (took {elapsed:?})"
        );
    }
}
