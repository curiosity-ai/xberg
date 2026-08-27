pub const MSVC_ONLY_FLAGS: &[&str] = &["/FImsvc_compat.h", "/EHsc"];

pub fn msvc_only_flags(target_os: &str, target_env: &str) -> &'static [&'static str] {
    if target_os == "windows" && target_env == "msvc" {
        MSVC_ONLY_FLAGS
    } else {
        &[]
    }
}
