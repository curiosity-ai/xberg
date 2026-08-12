//! Server-boundary validation for [`ServerConfig`](crate::core::ServerConfig).
//!
//! These validators guard the network-facing fields of the API server configuration:
//! listen host, listen port, CORS allow-list entries and upload size limits. They are
//! invoked from `ServerConfig::validate`, which every configuration load path
//! (`from_file`, `from_toml_file`, `from_yaml_file`, `from_json_file`) and
//! `apply_env_overrides` runs before the configuration is handed to the server.
//!
//! Every check is an allow-list: a value is rejected unless it matches a known-good
//! shape, and each rejection names the operation, the offending input and a fix.

use crate::{Result, XbergError};

/// Lowest port a server may bind to. Port 0 asks the OS for an arbitrary free port,
/// which defeats the point of a configured listen address.
const MIN_PORT: u32 = 1;

/// Highest port number representable in the TCP/UDP 16-bit port field.
const MAX_PORT: u32 = 65535;

/// Maximum total length of a DNS hostname (RFC 1035 §2.3.4).
const MAX_HOSTNAME_LENGTH: usize = 253;

/// Maximum length of a single dot-separated DNS label (RFC 1035 §2.3.4).
const MAX_HOSTNAME_LABEL_LENGTH: usize = 63;

/// Smallest upload limit that still allows a request through.
const MIN_UPLOAD_SIZE_BYTES: usize = 1;

/// CORS value that allows every origin.
const CORS_WILDCARD: &str = "*";

/// URL scheme prefixes accepted for a CORS origin.
const ALLOWED_CORS_SCHEMES: &[&str] = &["http://", "https://"];

/// Validate a server listen port.
///
/// # Arguments
///
/// * `port` - The port number to validate
///
/// # Returns
///
/// `Ok(())` if the port is within `MIN_PORT..=MAX_PORT`, otherwise a
/// [`XbergError::Validation`] naming the rejected value.
pub(crate) fn validate_port(port: u32) -> Result<()> {
    if (MIN_PORT..=MAX_PORT).contains(&port) {
        Ok(())
    } else {
        Err(XbergError::Validation {
            message: format!(
                "Port must be {MIN_PORT}-{MAX_PORT}, got {port}. \
                 Set 'server.port' (or XBERG_PORT) to a free port such as 8000."
            ),
            source: None,
        })
    }
}

/// Validate a server listen host.
///
/// Accepts an IPv4 address, an IPv6 address, or a DNS hostname whose labels are ASCII
/// alphanumeric with interior hyphens. Anything else — including a dotted all-numeric
/// string that is not a valid IPv4 address, such as `256.256.256.256` — is rejected.
///
/// # Arguments
///
/// * `host` - The host address to validate
///
/// # Returns
///
/// `Ok(())` if the host is a valid address or hostname, otherwise a
/// [`XbergError::Validation`] naming the rejected value.
pub(crate) fn validate_host(host: &str) -> Result<()> {
    let host = host.trim();

    if host.parse::<std::net::Ipv4Addr>().is_ok() || host.parse::<std::net::Ipv6Addr>().is_ok() {
        return Ok(());
    }

    if is_valid_hostname(host) {
        return Ok(());
    }

    Err(XbergError::Validation {
        message: format!(
            "Invalid host '{host}': must be a valid IP address or hostname. \
             Set 'server.host' (or XBERG_HOST) to e.g. '127.0.0.1', '0.0.0.0' or 'localhost'."
        ),
        source: None,
    })
}

/// Return `true` when `host` is a syntactically valid DNS hostname.
///
/// A dotted string made only of digits is treated as a malformed IP address rather than
/// a hostname, so it is rejected here instead of being waved through as a name.
fn is_valid_hostname(host: &str) -> bool {
    if host.is_empty() || host.len() > MAX_HOSTNAME_LENGTH {
        return false;
    }

    let looks_like_ip_address = host
        .split('.')
        .all(|label| !label.is_empty() && label.bytes().all(|byte| byte.is_ascii_digit()));
    if looks_like_ip_address {
        return false;
    }

    host.split('.').all(is_valid_hostname_label)
}

/// Return `true` when a single dot-separated hostname label is well formed.
fn is_valid_hostname_label(label: &str) -> bool {
    !label.is_empty()
        && label.len() <= MAX_HOSTNAME_LABEL_LENGTH
        && !label.starts_with('-')
        && !label.ends_with('-')
        && label.bytes().all(|byte| byte.is_ascii_alphanumeric() || byte == b'-')
}

/// Validate a single CORS allowed-origin entry.
///
/// Accepts the wildcard `*` or an absolute `http://` / `https://` URL with a non-empty
/// authority. Scheme-relative, bare-hostname and non-HTTP origins are rejected: a browser
/// never sends them as an `Origin` header, so allow-listing them silently does nothing.
///
/// # Arguments
///
/// * `origin` - The CORS origin to validate
///
/// # Returns
///
/// `Ok(())` if the origin is the wildcard or a well-formed HTTP(S) URL, otherwise a
/// [`XbergError::Validation`] naming the rejected value.
pub(crate) fn validate_cors_origin(origin: &str) -> Result<()> {
    let origin = origin.trim();

    if origin == CORS_WILDCARD {
        return Ok(());
    }

    for scheme in ALLOWED_CORS_SCHEMES {
        if let Some(remainder) = origin.strip_prefix(scheme) {
            let authority = remainder.split('/').next().unwrap_or_default();
            if !authority.is_empty() && !authority.contains(char::is_whitespace) {
                return Ok(());
            }
            break;
        }
    }

    Err(XbergError::Validation {
        message: format!(
            "Invalid CORS origin '{origin}': must be a valid HTTP/HTTPS URL or '{CORS_WILDCARD}'. \
             Set 'server.cors_origins' (or XBERG_CORS_ORIGINS) to e.g. 'https://example.com'."
        ),
        source: None,
    })
}

/// Validate an upload size limit in bytes.
///
/// # Arguments
///
/// * `size` - The maximum upload size in bytes
///
/// # Returns
///
/// `Ok(())` if the limit is at least `MIN_UPLOAD_SIZE_BYTES`, otherwise a
/// [`XbergError::Validation`] naming the rejected value. A limit of `0` would reject
/// every request body, so it is treated as a configuration mistake.
pub(crate) fn validate_upload_size(size: usize) -> Result<()> {
    if size >= MIN_UPLOAD_SIZE_BYTES {
        Ok(())
    } else {
        Err(XbergError::Validation {
            message: format!(
                "Upload size must be greater than 0, got {size}. \
                 Set 'server.max_request_body_bytes' / 'server.max_multipart_field_bytes' \
                 to a positive byte count such as 104857600 (100 MB)."
            ),
            source: None,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Render a validation outcome as an exactly comparable string: the empty string for
    /// an accepted value, or the full `Display` text of the rejection.
    fn outcome(result: crate::Result<()>) -> String {
        match result {
            Ok(()) => String::new(),
            Err(error) => error.to_string(),
        }
    }

    const PORT_ZERO_REJECTION: &str = "Validation error: Port must be 1-65535, got 0. \
         Set 'server.port' (or XBERG_PORT) to a free port such as 8000.";

    const EMPTY_HOST_REJECTION: &str = "Validation error: Invalid host '': \
         must be a valid IP address or hostname. Set 'server.host' (or XBERG_HOST) to e.g. \
         '127.0.0.1', '0.0.0.0' or 'localhost'.";

    const UPLOAD_SIZE_ZERO_REJECTION: &str = "Validation error: Upload size must be greater than 0, got 0. \
         Set 'server.max_request_body_bytes' / 'server.max_multipart_field_bytes' \
         to a positive byte count such as 104857600 (100 MB).";

    fn cors_rejection(origin: &str) -> String {
        format!(
            "Validation error: Invalid CORS origin '{origin}': must be a valid HTTP/HTTPS URL or '*'. \
             Set 'server.cors_origins' (or XBERG_CORS_ORIGINS) to e.g. 'https://example.com'."
        )
    }

    #[test]
    fn should_accept_port_when_inside_valid_range() {
        for port in [1_u32, 80, 443, 8000, 65535] {
            assert_eq!(outcome(validate_port(port)), "", "port {port} should be accepted");
        }
    }

    #[test]
    fn should_reject_port_when_zero() {
        assert_eq!(outcome(validate_port(0)), PORT_ZERO_REJECTION);
    }

    #[test]
    fn should_reject_port_when_above_sixteen_bit_range() {
        assert_eq!(
            outcome(validate_port(65_536)),
            "Validation error: Port must be 1-65535, got 65536. \
             Set 'server.port' (or XBERG_PORT) to a free port such as 8000."
        );
    }

    #[test]
    fn should_accept_host_when_ipv4_address() {
        for host in ["127.0.0.1", "0.0.0.0", "192.168.1.1", "10.0.0.1", "255.255.255.255"] {
            assert_eq!(outcome(validate_host(host)), "", "host {host} should be accepted");
        }
    }

    #[test]
    fn should_accept_host_when_ipv6_address() {
        for host in ["::1", "::", "2001:db8::1", "fe80::1"] {
            assert_eq!(outcome(validate_host(host)), "", "host {host} should be accepted");
        }
    }

    #[test]
    fn should_accept_host_when_dns_hostname() {
        for host in ["localhost", "example.com", "sub.example.com", "api-server", "app123"] {
            assert_eq!(outcome(validate_host(host)), "", "host {host} should be accepted");
        }
    }

    #[test]
    fn should_reject_host_when_empty() {
        assert_eq!(outcome(validate_host("")), EMPTY_HOST_REJECTION);
    }

    #[test]
    fn should_reject_host_when_it_contains_whitespace() {
        assert_eq!(
            outcome(validate_host("not a valid host")),
            "Validation error: Invalid host 'not a valid host': must be a valid IP address or hostname. \
             Set 'server.host' (or XBERG_HOST) to e.g. '127.0.0.1', '0.0.0.0' or 'localhost'."
        );
    }

    #[test]
    fn should_reject_host_when_ipv4_octets_are_out_of_range() {
        assert_eq!(
            outcome(validate_host("256.256.256.256")),
            "Validation error: Invalid host '256.256.256.256': must be a valid IP address or hostname. \
             Set 'server.host' (or XBERG_HOST) to e.g. '127.0.0.1', '0.0.0.0' or 'localhost'."
        );
    }

    #[test]
    fn should_reject_host_when_a_label_is_empty() {
        assert_eq!(
            outcome(validate_host("example..com")),
            "Validation error: Invalid host 'example..com': must be a valid IP address or hostname. \
             Set 'server.host' (or XBERG_HOST) to e.g. '127.0.0.1', '0.0.0.0' or 'localhost'."
        );
    }

    #[test]
    fn should_accept_cors_origin_when_https_url() {
        for origin in [
            "https://example.com",
            "https://localhost:3000",
            "https://sub.example.com",
            "https://192.168.1.1",
            "https://example.com/path",
        ] {
            assert_eq!(outcome(validate_cors_origin(origin)), "", "origin {origin} should pass");
        }
    }

    #[test]
    fn should_accept_cors_origin_when_http_url() {
        for origin in ["http://example.com", "http://localhost:3000", "http://127.0.0.1:8000"] {
            assert_eq!(outcome(validate_cors_origin(origin)), "", "origin {origin} should pass");
        }
    }

    #[test]
    fn should_accept_cors_origin_when_wildcard() {
        assert_eq!(outcome(validate_cors_origin("*")), "");
    }

    #[test]
    fn should_reject_cors_origin_when_scheme_is_missing() {
        assert_eq!(outcome(validate_cors_origin("not-a-url")), cors_rejection("not-a-url"));
        assert_eq!(
            outcome(validate_cors_origin("example.com")),
            cors_rejection("example.com")
        );
    }

    #[test]
    fn should_reject_cors_origin_when_scheme_is_not_http() {
        assert_eq!(
            outcome(validate_cors_origin("ftp://example.com")),
            cors_rejection("ftp://example.com")
        );
    }

    #[test]
    fn should_reject_cors_origin_when_authority_is_empty() {
        assert_eq!(outcome(validate_cors_origin("http://")), cors_rejection("http://"));
        assert_eq!(
            outcome(validate_cors_origin("https:///path")),
            cors_rejection("https:///path")
        );
    }

    #[test]
    fn should_accept_upload_size_when_positive() {
        for size in [1_usize, 1024, 1_000_000, 1_000_000_000, usize::MAX] {
            assert_eq!(
                outcome(validate_upload_size(size)),
                "",
                "size {size} should be accepted"
            );
        }
    }

    #[test]
    fn should_reject_upload_size_when_zero() {
        assert_eq!(outcome(validate_upload_size(0)), UPLOAD_SIZE_ZERO_REJECTION);
    }
}
