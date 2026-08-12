//! Statistical utilities for benchmark analysis
//!
//! This module provides shared statistical functions used across the benchmark harness.

/// Calculate percentile using R-7 linear interpolation method
///
/// The R-7 method is the default percentile calculation method in R and provides
/// linear interpolation between order statistics for improved accuracy over simpler
/// rounding-based methods.
///
/// # Arguments
/// * `sorted_values` - Sorted array of values (must be sorted for correct results)
/// * `p` - Percentile to calculate (0.0 - 1.0, where 0.5 = median, 0.95 = 95th percentile)
///
/// # Returns
/// The calculated percentile value, or 0.0 if the array is empty
///
/// # Panics
/// This function does not panic, but returns 0.0 for empty input arrays.
///
/// # Example
/// ```ignore
/// let values = vec![1.0, 2.0, 3.0, 4.0, 5.0];
/// let p50 = percentile_r7(&values, 0.50);  // Median
/// let p95 = percentile_r7(&values, 0.95);  // 95th percentile
/// ```
pub(crate) fn percentile_r7(sorted_values: &[f64], p: f64) -> f64 {
    if sorted_values.is_empty() {
        return 0.0;
    }
    let n = sorted_values.len();
    if n == 1 {
        return sorted_values[0];
    }
    let index = p * (n as f64 - 1.0);
    let lower = index.floor() as usize;
    let upper = index.ceil().min((n - 1) as f64) as usize;
    if lower == upper {
        sorted_values[lower]
    } else {
        let weight = index - lower as f64;
        sorted_values[lower] * (1.0 - weight) + sorted_values[upper] * weight
    }
}

/// Sanitize an f64 value, replacing NaN or infinity with 0.0
///
/// This is used to ensure JSON-serializable output from statistical calculations.
pub(crate) fn sanitize_f64(v: f64) -> f64 {
    if v.is_finite() { v } else { 0.0 }
}

/// Calculate mean, sample variance (Bessel-corrected), and standard deviation
///
/// Filters out NaN and infinite values before calculation.
/// Returns `(mean, variance, std_dev)`. For empty or single-element input,
/// variance and std_dev are 0.0.
///
/// # Arguments
/// * `values` - Slice of f64 values (NaN/Inf values are filtered out)
///
/// # Returns
/// Tuple of (mean, sample_variance, standard_deviation)
pub(crate) fn calculate_variance(values: &[f64]) -> (f64, f64, f64) {
    let filtered: Vec<f64> = values
        .iter()
        .copied()
        .filter(|v| !v.is_nan() && v.is_finite())
        .collect();
    if filtered.len() <= 1 {
        return (filtered.first().copied().unwrap_or(0.0), 0.0, 0.0);
    }
    let mean = filtered.iter().sum::<f64>() / filtered.len() as f64;
    let variance = filtered.iter().map(|v| (v - mean).powi(2)).sum::<f64>() / (filtered.len() - 1) as f64;
    let std_dev = variance.sqrt();
    (mean, variance, std_dev)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_percentile_r7_empty() {
        let values: Vec<f64> = vec![];
        assert_eq!(percentile_r7(&values, 0.5), 0.0);
    }

    #[test]
    fn test_percentile_r7_single_value() {
        let values = vec![42.0];
        assert_eq!(percentile_r7(&values, 0.5), 42.0);
        assert_eq!(percentile_r7(&values, 0.95), 42.0);
        assert_eq!(percentile_r7(&values, 0.0), 42.0);
        assert_eq!(percentile_r7(&values, 1.0), 42.0);
    }

    #[test]
    fn test_percentile_r7_two_values_all_percentiles() {
        let values = vec![10.0, 20.0];

        let p0 = percentile_r7(&values, 0.0);
        assert_eq!(p0, 10.0);

        let p50 = percentile_r7(&values, 0.5);
        assert_eq!(p50, 15.0);

        let p100 = percentile_r7(&values, 1.0);
        assert_eq!(p100, 20.0);
    }

    #[test]
    fn test_percentile_r7_five_values_known_values() {
        let values = vec![1.0, 2.0, 3.0, 4.0, 5.0];

        let p50 = percentile_r7(&values, 0.50);
        assert_eq!(p50, 3.0);

        let p95 = percentile_r7(&values, 0.95);
        assert!((p95 - 4.8).abs() < 0.0001);

        let p25 = percentile_r7(&values, 0.25);
        assert_eq!(p25, 2.0);

        let p75 = percentile_r7(&values, 0.75);
        assert_eq!(p75, 4.0);

        let p0 = percentile_r7(&values, 0.0);
        assert_eq!(p0, 1.0);

        let p100 = percentile_r7(&values, 1.0);
        assert_eq!(p100, 5.0);
    }

    #[test]
    fn test_percentile_r7_identical_values() {
        let values = vec![7.0, 7.0, 7.0, 7.0, 7.0];

        assert_eq!(percentile_r7(&values, 0.0), 7.0);
        assert_eq!(percentile_r7(&values, 0.25), 7.0);
        assert_eq!(percentile_r7(&values, 0.5), 7.0);
        assert_eq!(percentile_r7(&values, 0.75), 7.0);
        assert_eq!(percentile_r7(&values, 0.95), 7.0);
        assert_eq!(percentile_r7(&values, 1.0), 7.0);
    }

    #[test]
    fn test_percentile_r7_negative_values() {
        let values = vec![-5.0, -3.0, -1.0, 0.0, 2.0];

        let p50 = percentile_r7(&values, 0.50);
        assert_eq!(p50, -1.0);

        let p95 = percentile_r7(&values, 0.95);
        assert!(p95 > 0.0 && p95 <= 2.0);

        let p0 = percentile_r7(&values, 0.0);
        assert_eq!(p0, -5.0);

        let p100 = percentile_r7(&values, 1.0);
        assert_eq!(p100, 2.0);
    }

    #[test]
    fn test_percentile_r7_many_values() {
        let values: Vec<f64> = (1..=100).map(|i| i as f64).collect();

        let p50 = percentile_r7(&values, 0.50);
        assert!((p50 - 50.5).abs() < 0.01);

        let p95 = percentile_r7(&values, 0.95);
        assert!((p95 - 95.05).abs() < 0.01);

        let p25 = percentile_r7(&values, 0.25);
        assert!((p25 - 25.75).abs() < 0.01);

        let p75 = percentile_r7(&values, 0.75);
        assert!((p75 - 75.25).abs() < 0.01);
    }

    #[test]
    fn test_percentile_r7_edge_percentiles() {
        let values = vec![3.0, 1.0, 9.0, 2.0, 7.0];

        let p0 = percentile_r7(&values, 0.0);
        let p100 = percentile_r7(&values, 1.0);

        assert_eq!(p0, 3.0);
        assert_eq!(p100, 7.0);
    }

    #[test]
    fn test_percentile_r7_sorted_edge_percentiles() {
        let values = vec![1.0, 2.0, 3.0, 7.0, 9.0];

        let p0 = percentile_r7(&values, 0.0);
        assert_eq!(p0, 1.0);

        let p100 = percentile_r7(&values, 1.0);
        assert_eq!(p100, 9.0);
    }

    #[test]
    fn test_percentile_r7_unsorted_input_behavior() {
        let unsorted = vec![5.0, 1.0, 3.0, 2.0, 4.0];

        let p50_unsorted = percentile_r7(&unsorted, 0.50);
        assert_eq!(p50_unsorted, 3.0);

        let mut sorted = unsorted.clone();
        sorted.sort_by(|a, b| a.partial_cmp(b).unwrap());
        let p50_sorted = percentile_r7(&sorted, 0.50);
        assert_eq!(p50_sorted, 3.0);

        assert_eq!(sorted, vec![1.0, 2.0, 3.0, 4.0, 5.0]);
    }

    #[test]
    fn test_percentile_r7_three_values() {
        let values = vec![10.0, 20.0, 30.0];

        let p0 = percentile_r7(&values, 0.0);
        assert_eq!(p0, 10.0);

        let p50 = percentile_r7(&values, 0.50);
        assert_eq!(p50, 20.0);

        let p100 = percentile_r7(&values, 1.0);
        assert_eq!(p100, 30.0);

        let p25 = percentile_r7(&values, 0.25);
        assert_eq!(p25, 15.0);

        let p75 = percentile_r7(&values, 0.75);
        assert_eq!(p75, 25.0);
    }

    #[test]
    fn test_percentile_r7_floating_point_values() {
        let values = vec![1.5, 2.7, 3.2, 4.1, 5.9];

        let p50 = percentile_r7(&values, 0.50);
        assert_eq!(p50, 3.2);

        let p25 = percentile_r7(&values, 0.25);
        assert_eq!(p25, 2.7);

        let p75 = percentile_r7(&values, 0.75);
        assert_eq!(p75, 4.1);

        let p95 = percentile_r7(&values, 0.95);
        assert!((p95 - 5.54).abs() < 0.0001);
    }

    #[test]
    fn test_percentile_r7_high_percentiles() {
        let values = vec![1.0, 2.0, 3.0, 4.0, 5.0];

        let p99 = percentile_r7(&values, 0.99);
        assert!((p99 - 4.96).abs() < 0.0001);

        let p999 = percentile_r7(&values, 0.999);
        assert!((p999 - 4.996).abs() < 0.0001);
    }

    #[test]
    fn test_percentile_r7_low_percentiles() {
        let values = vec![1.0, 2.0, 3.0, 4.0, 5.0];

        let p1 = percentile_r7(&values, 0.01);
        assert!((p1 - 1.04).abs() < 0.0001);

        let p001 = percentile_r7(&values, 0.001);
        assert!((p001 - 1.004).abs() < 0.0001);
    }

    #[test]
    fn test_sanitize_f64_finite() {
        assert_eq!(sanitize_f64(42.0), 42.0);
        assert_eq!(sanitize_f64(-1.5), -1.5);
        assert_eq!(sanitize_f64(0.0), 0.0);
    }

    #[test]
    fn test_sanitize_f64_nan() {
        assert_eq!(sanitize_f64(f64::NAN), 0.0);
    }

    #[test]
    fn test_sanitize_f64_infinity() {
        assert_eq!(sanitize_f64(f64::INFINITY), 0.0);
        assert_eq!(sanitize_f64(f64::NEG_INFINITY), 0.0);
    }

    #[test]
    fn test_calculate_variance_empty() {
        let (mean, variance, std_dev) = calculate_variance(&[]);
        assert_eq!(mean, 0.0);
        assert_eq!(variance, 0.0);
        assert_eq!(std_dev, 0.0);
    }

    #[test]
    fn test_calculate_variance_single() {
        let (mean, variance, std_dev) = calculate_variance(&[5.0]);
        assert!((mean - 5.0).abs() < 0.001);
        assert_eq!(variance, 0.0);
        assert_eq!(std_dev, 0.0);
    }

    #[test]
    fn test_calculate_variance_bessel_correction() {
        let (mean, variance, std_dev) = calculate_variance(&[1.0, 2.0, 3.0]);
        assert!((mean - 2.0).abs() < 0.001);
        assert!((variance - 1.0).abs() < 0.001);
        assert!((std_dev - 1.0).abs() < 0.001);
    }

    #[test]
    fn test_calculate_variance_filters_nan_and_inf() {
        let values = [f64::NAN, 1.0, f64::INFINITY, 2.0, f64::NEG_INFINITY, 3.0];
        let (mean, variance, std_dev) = calculate_variance(&values);
        assert!((mean - 2.0).abs() < 0.001);
        assert!((variance - 1.0).abs() < 0.001);
        assert!((std_dev - 1.0).abs() < 0.001);
    }

    #[test]
    fn test_calculate_variance_all_nan() {
        let (mean, variance, std_dev) = calculate_variance(&[f64::NAN, f64::NAN]);
        assert_eq!(mean, 0.0);
        assert_eq!(variance, 0.0);
        assert_eq!(std_dev, 0.0);
    }

    #[test]
    fn test_calculate_variance_identical_values() {
        let (mean, variance, std_dev) = calculate_variance(&[5.0, 5.0, 5.0]);
        assert!((mean - 5.0).abs() < 0.001);
        assert!(variance.abs() < 0.001);
        assert!(std_dev.abs() < 0.001);
    }
}
