//! Logging helpers for the Xberg CLI.
//!
//! Provides a [`build_env_filter`] function that layers default third-party
//! transport suppressions on top of whatever the caller or `RUST_LOG` specifies.
//! User-supplied per-target rules in `RUST_LOG` always win because
//! [`EnvFilter::add_directive`] does not override existing per-target directives.

use tracing_subscriber::EnvFilter;

/// Third-party crates that are noisy at their own default level.
///
/// These are added as *fallback* directives: if `RUST_LOG` or `level_override`
/// already contain a per-target rule for any of these crates it takes precedence,
/// so the user can still do `RUST_LOG=ureq=debug` to restore full transport logs.
static QUIET_DIRECTIVES: std::sync::LazyLock<Vec<String>> = std::sync::LazyLock::new(|| {
    // The only mutation below (`directives.push`) is behind `#[cfg(feature = "pdf-surface")]`,
    // so a build with that feature disabled (e.g. the core-cli leg) never mutates `directives`
    // and clippy correctly flags `mut` as unused there; a build with the feature enabled needs
    // it. Silence the lint only for the configuration where it doesn't apply, rather than
    // dropping `mut` and breaking the other one.
    #[cfg_attr(not(feature = "pdf-surface"), allow(unused_mut))]
    let mut directives: Vec<String> = [
        "ureq=warn",
        "ureq_proto=warn",
        "rustls=warn",
        "hyper_util=warn",
        "hf_hub=info",
        "tower_http=info",
    ]
    .iter()
    .map(|directive| (*directive).to_string())
    .collect();

    // `EnvFilter` matches by TARGET PREFIX, and a crate's target prefix is its `[lib] name`,
    // not the dependency key anyone writes. This entry is therefore DERIVED from the engine's
    // own exported `module_path!()` rather than spelled out: in GH#697 it was a literal, the
    // engine's lib name moved, the literal stopped matching, and the engine's font warnings
    // were silently un-suppressed with nothing failing to compile. A derived value cannot
    // drift. This is also why the list is a `LazyLock<Vec<String>>` and not a `const` array.
    // ~keep
    #[cfg(feature = "pdf-surface")]
    directives.push(format!("{}=warn", xberg::pdf::render::ENGINE_LOG_TARGET_ROOT));

    directives
});

/// Extract the target crate name from a directive string like `"ureq=warn"`.
///
/// Returns the part before `=`, or `None` if there is no `=`.
fn directive_target(directive: &str) -> Option<&str> {
    directive.split_once('=').map(|(target, _)| target)
}

/// Build an [`EnvFilter`] with third-party transport crates suppressed by default.
///
/// Precedence (highest first):
/// 1. Per-target directives already present in `RUST_LOG` (or `level_override`).
/// 2. The [`QUIET_DIRECTIVES`] suppressions added here.
/// 3. Root level: `level_override` → `RUST_LOG` → `"info"`.
///
/// Per-target directives that the user has already set are **not** overridden:
/// we skip adding a quiet directive when the base filter already contains a
/// rule for the same target crate. This is necessary because
/// [`EnvFilter::add_directive`] appends rather than guards — a later-added
/// per-target directive for the same crate takes precedence.
///
/// # Arguments
///
/// * `level_override` — explicit root-level string from a CLI flag (e.g. `"debug"`).
///   When `Some`, it replaces `RUST_LOG` entirely for the root level.
pub fn build_env_filter(level_override: Option<&str>) -> EnvFilter {
    let base = level_override
        .and_then(|level| EnvFilter::try_new(level).ok())
        .or_else(|| EnvFilter::try_from_default_env().ok())
        .unwrap_or_else(|| EnvFilter::new("info"));

    let existing_targets: std::collections::HashSet<String> = base
        .to_string()
        .split(',')
        .filter_map(|chunk| directive_target(chunk).map(|t| t.trim().to_string()))
        .collect();

    QUIET_DIRECTIVES
        .iter()
        .filter(|directive| {
            directive_target(directive.as_str())
                .map(|target| !existing_targets.contains(target))
                .unwrap_or(true)
        })
        .fold(base, |filter, directive| {
            filter.add_directive(directive.parse().expect("built-in logging directive must be valid"))
        })
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Parse the directive string from an EnvFilter for assertion-level checks.
    ///
    /// `EnvFilter::to_string()` returns a comma-separated representation of all
    /// directives. We use this as a stable, public inspection surface.
    fn filter_directives(filter: &EnvFilter) -> String {
        filter.to_string()
    }

    #[test]
    fn default_filter_suppresses_ureq() {
        let filter = build_env_filter(None);
        let directives = filter_directives(&filter);
        assert!(
            directives.contains("ureq=warn"),
            "ureq=warn must be present in default filter; got: {directives}"
        );
        assert!(
            directives.contains("ureq_proto=warn"),
            "ureq_proto=warn must be present in default filter; got: {directives}"
        );
        assert!(
            directives.contains("rustls=warn"),
            "rustls=warn must be present in default filter; got: {directives}"
        );
    }

    #[test]
    fn default_filter_keeps_xberg_info() {
        let filter = build_env_filter(None);
        let directives = filter_directives(&filter);
        assert!(
            !directives.contains("xberg=warn") && !directives.contains("xberg=error"),
            "xberg must not be suppressed in the default filter; got: {directives}"
        );
    }

    #[test]
    fn env_override_wins_for_third_party() {
        let filter = build_env_filter(Some("info,ureq=debug"));
        let directives = filter.to_string();
        assert!(
            directives.contains("ureq=debug"),
            "user-supplied ureq=debug must be preserved; got: {directives}"
        );
        assert!(
            !directives.contains("ureq=warn"),
            "ureq=warn suppression must not be added when user already set ureq=debug; got: {directives}"
        );
    }

    #[test]
    fn level_override_wins() {
        let filter = build_env_filter(Some("debug"));
        let directives = filter_directives(&filter);
        assert!(
            directives.contains("debug"),
            "root debug level must appear in filter with --log-level debug; got: {directives}"
        );
        assert!(
            directives.contains("ureq=warn"),
            "ureq=warn suppression must still be present even under --log-level debug; got: {directives}"
        );
    }

    #[test]
    fn tower_http_suppressed_at_default() {
        let filter = build_env_filter(None);
        let directives = filter_directives(&filter);
        assert!(
            directives.contains("tower_http=info") || directives.contains("tower_http=warn"),
            "tower_http must be suppressed at default level; got: {directives}"
        );
    }

    #[test]
    fn all_quiet_directives_are_valid() {
        for directive in super::QUIET_DIRECTIVES.iter() {
            directive
                .parse::<tracing_subscriber::filter::Directive>()
                .expect("built-in directive is invalid");
        }
    }

    #[test]
    fn no_level_override_uses_info_root() {
        let filter = build_env_filter(None);
        let directives = filter_directives(&filter);
        let root_is_noisier_than_info = directives.starts_with("debug") || directives.starts_with("trace");
        assert!(
            !root_is_noisier_than_info,
            "default root level must not be debug/trace without RUST_LOG; got: {directives}"
        );
    }

    #[test]
    fn hf_hub_suppressed_at_default() {
        let filter = build_env_filter(None);
        let directives = filter_directives(&filter);
        assert!(
            directives.contains("hf_hub=info"),
            "hf_hub must be suppressed to info at default; got: {directives}"
        );
    }

    #[test]
    fn hyper_util_suppressed_at_default() {
        let filter = build_env_filter(None);
        let directives = filter_directives(&filter);
        assert!(
            directives.contains("hyper_util=warn"),
            "hyper_util must be suppressed to warn at default; got: {directives}"
        );
    }

    #[cfg(feature = "pdf-surface")]
    #[test]
    fn pdf_engine_suppressed_at_default() {
        let engine = xberg::pdf::render::ENGINE_LOG_TARGET_ROOT;
        let directives = filter_directives(&build_env_filter(None));
        assert!(
            directives.contains(&format!("{engine}=warn")),
            "{engine} must be suppressed to warn by default; got: {directives}"
        );
    }

    #[cfg(feature = "pdf-surface")]
    #[test]
    fn pdf_engine_user_override_wins() {
        let engine = xberg::pdf::render::ENGINE_LOG_TARGET_ROOT;
        let directives = filter_directives(&build_env_filter(Some(&format!("info,{engine}=debug"))));
        // Exact-token comparison rather than `str::contains`: directive strings are prefixes of
        // one another often enough that a substring check false-negatives on the very presence
        // this test exists to rule out.
        let tokens: Vec<&str> = directives.split(',').map(str::trim).collect();
        assert!(
            tokens.contains(&format!("{engine}=debug").as_str()),
            "user-supplied {engine}=debug must be preserved; got: {directives}"
        );
        assert!(
            !tokens.contains(&format!("{engine}=warn").as_str()),
            "default {engine} suppression must not replace a user override; got: {directives}"
        );
    }

    #[test]
    fn malformed_level_override_falls_back_to_info() {
        let filter = build_env_filter(Some(":::garbage"));
        let directives = filter_directives(&filter);
        assert!(
            directives.contains("ureq=warn"),
            "ureq=warn must still be present after malformed override; got: {directives}"
        );
    }

    #[test]
    fn similar_target_name_does_not_block_suppression() {
        let filter = build_env_filter(Some("info,hf_hub_server=debug"));
        let directives = filter.to_string();
        assert!(
            directives.contains("hf_hub_server=debug"),
            "user directive for hf_hub_server must survive; got: {directives}"
        );
        assert!(
            directives.contains("hf_hub=info"),
            "hf_hub=info suppression must still be applied; got: {directives}"
        );
    }

    /// A minimal `Layer` that records the `target` of every event it observes downstream of
    /// the `EnvFilter` under test. `on_event` only runs for events the filter let through, so
    /// anything captured here genuinely passed filtering -- this is what lets the behavioural
    /// test below assert on the filter's actual effect instead of on `QUIET_DIRECTIVES`'
    /// string contents.
    #[derive(Default)]
    struct RecordingLayer {
        targets: std::sync::Arc<std::sync::Mutex<Vec<String>>>,
    }

    impl<S: tracing::Subscriber> tracing_subscriber::Layer<S> for RecordingLayer {
        fn on_event(&self, event: &tracing::Event<'_>, _ctx: tracing_subscriber::layer::Context<'_, S>) {
            self.targets
                .lock()
                .expect("recording layer mutex poisoned")
                .push(event.metadata().target().to_string());
        }
    }

    /// Behavioural regression test for GH#697, where the engine crate was renamed.
    ///
    /// The tests above assert on the *directive string* -- they would have kept passing while
    /// `QUIET_DIRECTIVES` held a stale literal, even though `EnvFilter` matches by target
    /// prefix and the engine's real targets are rooted at its `[lib] name`, which the stale
    /// literal no longer matched. That is exactly why the suppression went dead unnoticed: the
    /// test checked the identifier, not the mechanism. This test builds the real `EnvFilter`
    /// `build_env_filter` returns, fires actual `tracing` events through it, and checks what
    /// a downstream layer actually observes.
    ///
    /// Uses `tracing::subscriber::with_default` (a *scoped* dispatcher), not
    /// `set_global_default`: `tracing` has one global dispatcher slot per process, tests in
    /// this binary run concurrently, and a global install here would race every other test and
    /// silently win or lose depending on execution order. This also means we do not need
    /// `tracing::callsite::rebuild_interest_cache()` -- each `tracing::warn!` call site below is
    /// unique to this test function, so its interest is computed for the first time against the
    /// scoped subscriber installed by `with_default`, not against a stale global cache.
    #[test]
    fn pdf_engine_info_is_suppressed_while_its_warnings_survive() {
        use tracing_subscriber::layer::SubscriberExt as _;

        let filter = build_env_filter(None);
        let recorder = RecordingLayer::default();
        let targets = recorder.targets.clone();
        let subscriber = tracing_subscriber::registry().with(filter).with(recorder);

        tracing::subscriber::with_default(subscriber, || {
            // Suppressed: an INFO at the engine's target. `xberg_native_pdf=warn` caps that
            // target at WARN, so INFO is dropped -- while the root level is "info", so the
            // same event at any OTHER target would pass. That asymmetry is what makes this
            // sensitive to a stale prefix: if the directive stopped matching, this INFO would
            // fall through to the root filter and be captured.
            //
            // Deliberately NOT a WARN. `target=warn` PERMITS warnings; it quiets a noisy crate
            // down to warnings, it does not silence them. The previous version of this test
            // emitted a WARN and asserted it was suppressed, which the filter was never going
            // to do -- it had been failing on main.
            tracing::info!(target: "xberg_native_pdf::fonts", "chatty engine info");
            // Still passes at the same target: warnings are the level this directive keeps.
            tracing::warn!(target: "xberg_native_pdf::fonts", "engine warning worth seeing");
            // Positive control: an unrelated target at the same level must still pass, so this
            // test cannot pass by accidentally filtering everything out.
            tracing::warn!(target: "xberg::extract", "should still be observed");
        });

        let captured = targets.lock().expect("recording layer mutex poisoned");
        assert!(
            captured
                .iter()
                .filter(|target| *target == "xberg_native_pdf::fonts")
                .count()
                == 1,
            "exactly one event at the engine target must survive: the INFO suppressed by \
             `=warn`, the WARN kept. Two means the directive did not match (stale prefix); \
             zero means the filter is dropping warnings it should keep; \
             captured targets: {captured:?}"
        );
        assert!(
            captured.iter().any(|target| target == "xberg::extract"),
            "positive control failed: xberg::extract WARN event must still pass through \
             (if this also fails, the test filtered everything and proves nothing); \
             captured targets: {captured:?}"
        );
    }
}
