//! Process-wide policy registry: lazy default.
//!
//! Own integration binary (the `OnceLock` policy cell is per-process;
//! this test must NOT call `set_policy` first — it asserts the
//! behaviour-preserving lazy default).

use xberg_native_pdf::crypto::{AlgorithmId, AlgorithmUse, Decision, PolicyMode, active_policy, is_policy_set};

#[test]
fn lazy_default_policy_is_compat_and_byte_stable() {
    assert!(!is_policy_set(), "no policy should be set before first access");

    // First access lazily installs the behaviour-preserving Compat
    // policy (so the default `active()` path stays byte-for-byte
    // identical to the behaviour from before the registry). ~keep
    let p = active_policy();
    assert_eq!(p.mode(), PolicyMode::Compat);
    assert!(is_policy_set(), "access lazily installs the default");

    // Compat allows every primitive in both directions — the legacy
    // R≤4 read/write gates therefore behave exactly as before. ~keep
    for &a in &AlgorithmId::ALL {
        for u in [AlgorithmUse::Read, AlgorithmUse::Write] {
            assert_eq!(p.evaluate(a, u), Decision::Allow, "compat {a:?} {u:?}");
        }
    }
}
