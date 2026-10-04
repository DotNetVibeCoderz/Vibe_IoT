//! # iotcom-ffi-support
//!
//! The C ABI contract shared by every IoTCom.Net native library (see docs/en/native/ffi.md):
//!
//! * every exported function is `extern "C"` and wrapped in [`ffi_guard`], so a Rust panic never
//!   unwinds into .NET — it becomes [`IOTCOM_ERR_PANIC`];
//! * results are `i32` status codes; details are kept per thread and read with `iotcom_last_error`;
//! * objects are opaque heap handles created by `*_new` and released by exactly one `*_free`;
//! * buffers are owned by the caller;
//! * [`ABI_VERSION`] is checked by the .NET loader before any other call.
//!
//! Use [`export_common!`] once per cdylib to export `iotcom_abi_version` and `iotcom_last_error`.

use std::cell::RefCell;
use std::panic::{catch_unwind, AssertUnwindSafe};

/// Bump when any exported signature or `repr(C)` layout changes.
pub const ABI_VERSION: u32 = 1;

/// Success.
pub const IOTCOM_OK: i32 = 0;
/// A required pointer was null.
pub const IOTCOM_ERR_NULL: i32 = -1;
/// An argument is invalid.
pub const IOTCOM_ERR_INVALID_ARG: i32 = -2;
/// A panic was caught at the boundary.
pub const IOTCOM_ERR_PANIC: i32 = -3;
/// The caller's buffer is too small (required size is reported through the out parameter).
pub const IOTCOM_ERR_BUFFER_TOO_SMALL: i32 = -4;
/// The peer violated the protocol.
pub const IOTCOM_ERR_PROTOCOL: i32 = -5;
/// Back-pressure: try again later.
pub const IOTCOM_ERR_BUSY: i32 = -6;
/// Unexpected internal error.
pub const IOTCOM_ERR_INTERNAL: i32 = -99;

thread_local! {
    static LAST_ERROR: RefCell<String> = const { RefCell::new(String::new()) };
}

/// Error type carried through [`ffi_guard`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FfiError {
    /// Status code returned to the caller.
    pub code: i32,
    /// Message stored in the thread-local last error.
    pub message: String,
}

impl FfiError {
    /// Creates an error.
    pub fn new(code: i32, message: impl Into<String>) -> Self {
        Self { code, message: message.into() }
    }

    /// Null pointer error for `what`.
    pub fn null(what: &str) -> Self {
        Self::new(IOTCOM_ERR_NULL, format!("{what} must not be null"))
    }
}

impl From<iotcom_core::Error> for FfiError {
    fn from(e: iotcom_core::Error) -> Self {
        let code = match e {
            iotcom_core::Error::InvalidArgument(_) => IOTCOM_ERR_INVALID_ARG,
            iotcom_core::Error::Protocol(_) => IOTCOM_ERR_PROTOCOL,
            iotcom_core::Error::Busy => IOTCOM_ERR_BUSY,
            iotcom_core::Error::BufferTooSmall(_) => IOTCOM_ERR_BUFFER_TOO_SMALL,
        };
        Self::new(code, e.to_string())
    }
}

/// Stores `message` as this thread's last error.
pub fn set_last_error(message: impl Into<String>) {
    LAST_ERROR.with(|e| *e.borrow_mut() = message.into());
}

/// Runs `f`, converting errors and panics into status codes.
///
/// `f` returns `Ok(code)` where `code` is usually [`IOTCOM_OK`] (or a non-negative count).
pub fn ffi_guard<F>(f: F) -> i32
where
    F: FnOnce() -> Result<i32, FfiError>,
{
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(code)) => code,
        Ok(Err(e)) => {
            set_last_error(e.message);
            e.code
        }
        Err(panic) => {
            let msg = panic
                .downcast_ref::<&str>()
                .map(|s| (*s).to_string())
                .or_else(|| panic.downcast_ref::<String>().cloned())
                .unwrap_or_else(|| "unknown panic".to_string());
            set_last_error(format!("panic in native code: {msg}"));
            IOTCOM_ERR_PANIC
        }
    }
}

/// Copies the last error (UTF-8, not NUL-terminated) into `buf`.
///
/// Returns the full message length; if it is larger than `len` the message was truncated.
///
/// # Safety
/// `buf` must be valid for `len` writable bytes, or null when `len == 0`.
pub unsafe fn copy_last_error(buf: *mut u8, len: usize) -> i32 {
    LAST_ERROR.with(|e| {
        let msg = e.borrow();
        let bytes = msg.as_bytes();
        if !buf.is_null() && len > 0 {
            let n = bytes.len().min(len);
            // SAFETY: caller guarantees `buf` is valid for `len` bytes and n <= len.
            unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), buf, n) };
        }
        i32::try_from(bytes.len()).unwrap_or(i32::MAX)
    })
}

/// Exports `iotcom_abi_version` and `iotcom_last_error` from the calling cdylib.
#[macro_export]
macro_rules! export_common {
    () => {
        /// ABI version of this native library.
        #[no_mangle]
        pub extern "C" fn iotcom_abi_version() -> u32 {
            $crate::ABI_VERSION
        }

        /// Copies the calling thread's last error message into `buf`; returns its full length.
        ///
        /// # Safety
        /// `buf` must be valid for `len` bytes (or null with `len == 0`).
        #[no_mangle]
        pub unsafe extern "C" fn iotcom_last_error(buf: *mut u8, len: usize) -> i32 {
            // SAFETY: forwarded caller contract.
            unsafe { $crate::copy_last_error(buf, len) }
        }
    };
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn panics_become_status_codes() {
        let code = ffi_guard(|| panic!("boom"));
        assert_eq!(code, IOTCOM_ERR_PANIC);
        let mut buf = [0u8; 64];
        let n = unsafe { copy_last_error(buf.as_mut_ptr(), buf.len()) } as usize;
        assert!(std::str::from_utf8(&buf[..n]).unwrap().contains("boom"));
    }

    #[test]
    fn errors_are_reported() {
        assert_eq!(ffi_guard(|| Err(FfiError::null("cfg"))), IOTCOM_ERR_NULL);
        assert_eq!(ffi_guard(|| Ok(7)), 7);
        let e: FfiError = iotcom_core::Error::Busy.into();
        assert_eq!(e.code, IOTCOM_ERR_BUSY);
    }
}
