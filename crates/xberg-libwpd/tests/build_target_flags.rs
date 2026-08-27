#[path = "../build_target_flags.rs"]
mod build_target_flags;

use build_target_flags::{MSVC_ONLY_FLAGS, msvc_only_flags};

#[test]
fn msvc_target_receives_msvc_only_flags() {
    assert_eq!(msvc_only_flags("windows", "msvc"), MSVC_ONLY_FLAGS);
}

#[test]
fn mingw_target_does_not_receive_msvc_only_flags() {
    assert!(msvc_only_flags("windows", "gnu").is_empty());
}
