//! Verification that the no-op profiling path is the one compiled in when `profiling` is off.
//!
//! These tests only build when the `profiling` feature is NOT enabled. Each asserts a value that
//! the real pprof-backed implementation could not produce, so a regression in the feature gating
//! (for example re-exporting the real `ProfileGuard` unconditionally) fails them.
//!
//! Scope: this is a behavioural check of the public API, not a binary inspection. Literal absence
//! of pprof symbols from the linked artifact is not asserted here.

#![cfg(not(feature = "profiling"))]

use benchmark_harness::profiling::ProfileGuard;
use std::time::Duration;

/// Sampling frequency in Hz requested from the profiler; the no-op path must ignore it entirely.
const REQUESTED_SAMPLING_FREQUENCY_HZ: i32 = 1000;

/// Work performed between starting and finishing the profiler, large enough that a real profiler
/// sampling at [`REQUESTED_SAMPLING_FREQUENCY_HZ`] would record a non-zero duration.
fn burn_measurable_cpu_time() {
    let total: u64 = (0..2_000_000_u64).map(|value| value.wrapping_mul(7)).sum();
    std::hint::black_box(total);
}

#[test]
fn should_report_zero_samples_when_profiling_feature_is_disabled() {
    let guard = ProfileGuard::new(REQUESTED_SAMPLING_FREQUENCY_HZ).expect("create no-op profile guard");
    burn_measurable_cpu_time();

    assert_eq!(
        guard.estimated_sample_count(),
        0,
        "no-op profiler must collect no samples; a non-zero count means the real pprof profiler \
         was compiled in despite the `profiling` feature being disabled"
    );
}

#[test]
fn should_report_zero_duration_when_profiling_feature_is_disabled() {
    let guard = ProfileGuard::new(REQUESTED_SAMPLING_FREQUENCY_HZ).expect("create no-op profile guard");
    burn_measurable_cpu_time();
    let result = guard.finish().expect("finish no-op profile guard");

    assert_eq!(result.sample_count, 0, "no-op profiling result must carry no samples");
    assert_eq!(
        result.duration,
        Duration::ZERO,
        "no-op profiling result must not measure elapsed time; the real implementation reports the \
         guard's actual lifetime, which the CPU burn above makes non-zero"
    );
}

#[test]
fn should_not_write_a_flamegraph_when_profiling_feature_is_disabled() {
    let directory = tempfile::tempdir().expect("create temporary directory");
    let output_path = directory.path().join("nested").join("profile.svg");

    let guard = ProfileGuard::new(REQUESTED_SAMPLING_FREQUENCY_HZ).expect("create no-op profile guard");
    burn_measurable_cpu_time();
    let result = guard.finish().expect("finish no-op profile guard");

    result
        .generate_flamegraph(&output_path)
        .expect("no-op flamegraph generation must succeed without doing work");

    assert!(
        !output_path.exists(),
        "no-op flamegraph generation must not write `{}`; the real implementation creates parent \
         directories and writes an SVG there",
        output_path.display()
    );
}
