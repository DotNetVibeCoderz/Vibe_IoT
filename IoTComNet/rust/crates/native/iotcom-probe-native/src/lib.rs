//! C ABI over probe-rs — the `iotcom_probe` native library behind `IoTCom.Net.Debug`: list debug probes, attach to a
//! target, read and write memory, halt/run/reset, read the core state, flash (erase, program, verify) and erase all.
//!
//! Contract (see `iotcom-ffi-support`): every function returns an `i32` status (`IOTCOM_OK` = 0 or a non-negative count,
//! negative = error, details via `iotcom_last_error`) and never unwinds. Sessions are opaque handles released with exactly
//! one [`iotcom_probe_free`]; calls on one handle must be serialised. Probe lists are returned as JSON.

use std::fmt::Write as _;
use std::slice;
use std::time::Duration;

use iotcom_ffi_support::{export_common, ffi_guard, FfiError, IOTCOM_ERR_BUFFER_TOO_SMALL, IOTCOM_ERR_INVALID_ARG, IOTCOM_OK};
use probe_rs::flashing::{DownloadOptions, FlashLoader};
use probe_rs::probe::list::Lister;
use probe_rs::probe::{DebugProbeSelector, WireProtocol};
use probe_rs::{CoreStatus, MemoryInterface, Permissions, Session};

export_common!();

/// Status: the probe failed (USB, permissions, wiring, protocol).
pub const IOTCOM_ERR_PROBE: i32 = -40;
/// Status: no probe matches.
pub const IOTCOM_ERR_PROBE_NOT_FOUND: i32 = -41;
/// Status: the target refused (unknown chip, no answer, memory fault).
pub const IOTCOM_ERR_TARGET: i32 = -42;
/// Status: flashing failed.
pub const IOTCOM_ERR_FLASH: i32 = -43;

const HALT_TIMEOUT: Duration = Duration::from_millis(500);

/// An attached target and the flash data queued for the next commit.
pub struct ProbeSession {
    session: Session,
    pending: Vec<(u64, Vec<u8>)>,
}

/// JSON string escaping.
pub fn json_string(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    out.push('"');
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            c if (c as u32) < 0x20 => {
                let _ = write!(out, "\\u{:04x}", c as u32);
            }
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

fn text<'a>(ptr: *const u8, len: usize, what: &str) -> Result<&'a str, FfiError> {
    if len == 0 {
        return Ok("");
    }
    if ptr.is_null() {
        return Err(FfiError::null(what));
    }
    // SAFETY: caller guarantees `ptr` is valid for `len` bytes.
    std::str::from_utf8(unsafe { slice::from_raw_parts(ptr, len) }).map_err(|_| FfiError::new(IOTCOM_ERR_INVALID_ARG, format!("{what} is not UTF-8")))
}

fn input<'a>(ptr: *const u8, len: usize) -> Result<&'a [u8], FfiError> {
    if len == 0 {
        return Ok(&[]);
    }
    if ptr.is_null() {
        return Err(FfiError::null("data"));
    }
    // SAFETY: caller guarantees `ptr` is valid for `len` bytes.
    Ok(unsafe { slice::from_raw_parts(ptr, len) })
}

/// # Safety
/// `buf` valid for `len` bytes (or null with nothing to copy); `written` valid for one write.
unsafe fn copy_out(bytes: &[u8], buf: *mut u8, len: usize, written: *mut usize) -> Result<i32, FfiError> {
    if written.is_null() {
        return Err(FfiError::null("written"));
    }
    // SAFETY: checked non-null.
    unsafe { *written = bytes.len() };
    if bytes.len() > len || (buf.is_null() && !bytes.is_empty()) {
        return Err(FfiError::new(IOTCOM_ERR_BUFFER_TOO_SMALL, format!("needs {} bytes", bytes.len())));
    }
    if !bytes.is_empty() {
        // SAFETY: buf valid for len >= bytes.len().
        unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), buf, bytes.len()) };
    }
    Ok(IOTCOM_OK)
}

fn session<'a>(h: *mut ProbeSession) -> Result<&'a mut ProbeSession, FfiError> {
    if h.is_null() {
        return Err(FfiError::null("handle"));
    }
    // SAFETY: the handle comes from `iotcom_probe_open` and calls are serialised by the caller.
    Ok(unsafe { &mut *h })
}

fn target_err(e: impl std::fmt::Display) -> FfiError {
    FfiError::new(IOTCOM_ERR_TARGET, e.to_string())
}

/// Writes the attached debug probes as a JSON array: `selector`, `name`, `kind`, `serial`.
///
/// # Safety
/// `buf` valid for `len` bytes; `written` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_probe_list(buf: *mut u8, len: usize, written: *mut usize) -> i32 {
    ffi_guard(|| {
        let mut json = String::from("[");
        for (i, p) in Lister::new().list_all().iter().enumerate() {
            if i > 0 {
                json.push(',');
            }
            let selector = match &p.serial_number {
                Some(s) => format!("{:04x}:{:04x}:{s}", p.vendor_id, p.product_id),
                None => format!("{:04x}:{:04x}", p.vendor_id, p.product_id),
            };
            let _ = write!(
                json,
                "{{\"selector\":{},\"name\":{},\"kind\":{},\"serial\":{}}}",
                json_string(&selector),
                json_string(&p.identifier),
                json_string(&format!("{:?}", p.probe_type())),
                p.serial_number.as_deref().map_or_else(|| "null".to_string(), json_string)
            );
        }
        json.push(']');
        // SAFETY: forwarded caller contract.
        unsafe { copy_out(json.as_bytes(), buf, len, written) }
    })
}

/// Opens a probe (`selector` = `vid:pid[:serial]`, empty = the only probe), selects SWD (0) or JTAG (1) at `speed_khz`
/// and attaches to `target` (a probe-rs target name).
///
/// # Safety
/// Strings valid for their lengths; `out` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_probe_open(
    selector: *const u8,
    selector_len: usize,
    target: *const u8,
    target_len: usize,
    protocol: i32,
    speed_khz: u32,
    out: *mut *mut ProbeSession,
) -> i32 {
    ffi_guard(|| {
        if out.is_null() {
            return Err(FfiError::null("out"));
        }
        let selector = text(selector, selector_len, "selector")?;
        let target = text(target, target_len, "target")?;
        if target.is_empty() {
            return Err(FfiError::new(IOTCOM_ERR_INVALID_ARG, "target must name a chip (probe-rs target name)"));
        }
        let lister = Lister::new();
        let info = if selector.is_empty() {
            let all = lister.list_all();
            match all.len() {
                0 => return Err(FfiError::new(IOTCOM_ERR_PROBE_NOT_FOUND, "no debug probe found (check the USB cable, drivers and udev rules)")),
                1 => all.into_iter().next().ok_or_else(|| FfiError::new(IOTCOM_ERR_PROBE_NOT_FOUND, "no debug probe"))?,
                n => return Err(FfiError::new(IOTCOM_ERR_PROBE_NOT_FOUND, format!("{n} probes found: choose one with a selector (vid:pid:serial)"))),
            }
        } else {
            let wanted: DebugProbeSelector = selector.parse().map_err(|e| FfiError::new(IOTCOM_ERR_INVALID_ARG, format!("selector '{selector}': {e}")))?;
            lister
                .list_all()
                .into_iter()
                .find(|p| p.vendor_id == wanted.vendor_id && p.product_id == wanted.product_id && (wanted.serial_number.is_none() || p.serial_number == wanted.serial_number))
                .ok_or_else(|| FfiError::new(IOTCOM_ERR_PROBE_NOT_FOUND, format!("no probe matches '{selector}'")))?
        };
        let mut probe = info.open().map_err(|e| FfiError::new(IOTCOM_ERR_PROBE, format!("opening {}: {e}", info.identifier)))?;
        let wire = if protocol == 1 { WireProtocol::Jtag } else { WireProtocol::Swd };
        probe.select_protocol(wire).map_err(|e| FfiError::new(IOTCOM_ERR_PROBE, format!("{wire:?}: {e}")))?;
        if speed_khz > 0 {
            probe.set_speed(speed_khz).map_err(|e| FfiError::new(IOTCOM_ERR_PROBE, format!("speed {speed_khz} kHz: {e}")))?;
        }
        let session = probe.attach(target, Permissions::default()).map_err(|e| FfiError::new(IOTCOM_ERR_TARGET, format!("attaching to {target}: {e}")))?;
        // SAFETY: checked non-null.
        unsafe { *out = Box::into_raw(Box::new(ProbeSession { session, pending: Vec::new() })) };
        Ok(IOTCOM_OK)
    })
}

/// Releases a session (detaches; the target keeps running).
///
/// # Safety
/// `ptr` from [`iotcom_probe_open`], not used afterwards.
#[no_mangle]
pub unsafe extern "C" fn iotcom_probe_free(ptr: *mut ProbeSession) {
    if !ptr.is_null() {
        let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            // SAFETY: ownership returns to Rust exactly once.
            drop(unsafe { Box::from_raw(ptr) });
        }));
    }
}

/// Reads `len` bytes at `address` into `buf`.
///
/// # Safety
/// `handle` live; `buf` valid for `len` bytes.
#[no_mangle]
pub unsafe extern "C" fn iotcom_probe_read(handle: *mut ProbeSession, address: u64, buf: *mut u8, len: usize) -> i32 {
    ffi_guard(|| {
        let s = session(handle)?;
        if len == 0 {
            return Ok(IOTCOM_OK);
        }
        if buf.is_null() {
            return Err(FfiError::null("buf"));
        }
        // SAFETY: caller guarantees `buf` is valid for `len` bytes.
        let out = unsafe { slice::from_raw_parts_mut(buf, len) };
        s.session.core(0).map_err(target_err)?.read(address, out).map_err(|e| FfiError::new(IOTCOM_ERR_TARGET, format!("read 0x{address:08X}: {e}")))?;
        Ok(IOTCOM_OK)
    })
}

/// Writes `len` bytes at `address`.
///
/// # Safety
/// `handle` live; `data` valid for `len` bytes.
#[no_mangle]
pub unsafe extern "C" fn iotcom_probe_write(handle: *mut ProbeSession, address: u64, data: *const u8, len: usize) -> i32 {
    ffi_guard(|| {
        let s = session(handle)?;
        let data = input(data, len)?;
        s.session.core(0).map_err(target_err)?.write(address, data).map_err(|e| FfiError::new(IOTCOM_ERR_TARGET, format!("write 0x{address:08X}: {e}")))?;
        Ok(IOTCOM_OK)
    })
}

/// Halts core 0.
///
/// # Safety
/// `handle` live.
#[no_mangle]
pub unsafe extern "C" fn iotcom_probe_halt(handle: *mut ProbeSession) -> i32 {
    ffi_guard(|| {
        session(handle)?.session.core(0).map_err(target_err)?.halt(HALT_TIMEOUT).map_err(target_err)?;
        Ok(IOTCOM_OK)
    })
}

/// Resumes core 0.
///
/// # Safety
/// `handle` live.
#[no_mangle]
pub unsafe extern "C" fn iotcom_probe_run(handle: *mut ProbeSession) -> i32 {
    ffi_guard(|| {
        session(handle)?.session.core(0).map_err(target_err)?.run().map_err(target_err)?;
        Ok(IOTCOM_OK)
    })
}

/// Resets the target (and halts at the reset vector when `halt` is non-zero).
///
/// # Safety
/// `handle` live.
#[no_mangle]
pub unsafe extern "C" fn iotcom_probe_reset(handle: *mut ProbeSession, halt: u8) -> i32 {
    ffi_guard(|| {
        let s = session(handle)?;
        let mut core = s.session.core(0).map_err(target_err)?;
        if halt != 0 {
            core.reset_and_halt(HALT_TIMEOUT).map_err(target_err)?;
        } else {
            core.reset().map_err(target_err)?;
        }
        Ok(IOTCOM_OK)
    })
}

/// Core state (0 unknown, 1 running, 2 halted, 3 sleeping, 4 locked up) and the program counter (0 unless halted).
///
/// # Safety
/// `handle` live; `state` and `pc` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_probe_core(handle: *mut ProbeSession, state: *mut i32, pc: *mut u32) -> i32 {
    ffi_guard(|| {
        if state.is_null() || pc.is_null() {
            return Err(FfiError::null("state/pc"));
        }
        let s = session(handle)?;
        let mut core = s.session.core(0).map_err(target_err)?;
        let status = core.status().map_err(target_err)?;
        let (code, counter) = match status {
            CoreStatus::Running => (1, 0),
            CoreStatus::Halted(_) => {
                let reg = core.program_counter();
                let value: u32 = core.read_core_reg(reg).map_err(target_err)?;
                (2, value)
            }
            CoreStatus::Sleeping => (3, 0),
            CoreStatus::LockedUp => (4, 0),
            _ => (0, 0),
        };
        // SAFETY: checked non-null.
        unsafe {
            *state = code;
            *pc = counter;
        }
        Ok(IOTCOM_OK)
    })
}

/// Queues `len` bytes at `address` for the next [`iotcom_probe_flash_commit`].
///
/// # Safety
/// `handle` live; `data` valid for `len` bytes.
#[no_mangle]
pub unsafe extern "C" fn iotcom_probe_flash_add(handle: *mut ProbeSession, address: u64, data: *const u8, len: usize) -> i32 {
    ffi_guard(|| {
        let s = session(handle)?;
        s.pending.push((address, input(data, len)?.to_vec()));
        Ok(IOTCOM_OK)
    })
}

/// Erases the sectors the queued data needs, programs it and (when `verify` is non-zero) verifies it.
///
/// # Safety
/// `handle` live.
#[no_mangle]
pub unsafe extern "C" fn iotcom_probe_flash_commit(handle: *mut ProbeSession, verify: u8) -> i32 {
    ffi_guard(|| {
        let s = session(handle)?;
        let pending = std::mem::take(&mut s.pending);
        if pending.is_empty() {
            return Err(FfiError::new(IOTCOM_ERR_INVALID_ARG, "nothing queued to flash"));
        }
        let mut loader: FlashLoader = s.session.target().flash_loader();
        for (address, data) in &pending {
            loader.add_data(*address, data).map_err(|e| FfiError::new(IOTCOM_ERR_FLASH, format!("0x{address:08X}: {e}")))?;
        }
        let mut options = DownloadOptions::default();
        options.verify = verify != 0;
        loader.commit(&mut s.session, options).map_err(|e| FfiError::new(IOTCOM_ERR_FLASH, e.to_string()))?;
        Ok(IOTCOM_OK)
    })
}

/// Erases the whole flash.
///
/// # Safety
/// `handle` live.
#[no_mangle]
pub unsafe extern "C" fn iotcom_probe_erase_all(handle: *mut ProbeSession) -> i32 {
    ffi_guard(|| {
        let s = session(handle)?;
        probe_rs::flashing::erase_all(&mut s.session, &mut probe_rs::flashing::FlashProgress::empty(), false).map_err(|e| FfiError::new(IOTCOM_ERR_FLASH, e.to_string()))?;
        Ok(IOTCOM_OK)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn json_escapes_control_characters() {
        assert_eq!(json_string("a\"b\\c\n"), "\"a\\\"b\\\\c\\u000a\"");
    }

    #[test]
    fn listing_never_fails_without_probes() {
        let mut written = 0usize;
        let mut buf = vec![0u8; 64 * 1024];
        // SAFETY: valid buffer and length.
        let status = unsafe { iotcom_probe_list(buf.as_mut_ptr(), buf.len(), &mut written) };
        assert_eq!(status, IOTCOM_OK);
        assert!(std::str::from_utf8(&buf[..written]).unwrap().starts_with('['));
    }

    #[test]
    fn open_requires_a_target() {
        let mut out = std::ptr::null_mut();
        // SAFETY: empty strings with zero lengths; `out` valid.
        let status = unsafe { iotcom_probe_open(std::ptr::null(), 0, std::ptr::null(), 0, 0, 4000, &mut out) };
        assert_eq!(status, IOTCOM_ERR_INVALID_ARG);
    }
}
