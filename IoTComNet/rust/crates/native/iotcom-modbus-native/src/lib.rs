//! C ABI for the Modbus master machine (`iotcom_modbus` native library).
//!
//! Contract (see `iotcom-ffi-support`): every function returns an `i32` status (`IOTCOM_OK` = 0,
//! negative = error, details via `iotcom_last_error`), never unwinds, and works on an opaque
//! `ModbusMaster` handle released with `iotcom_modbus_master_free`.
//!
//! The C header is generated with cbindgen (`rust/cbindgen.toml`) into `rust/include/iotcom_modbus.h`.

use std::slice;

use iotcom_core::{Instant, Machine};
use iotcom_ffi_support::{
    export_common, ffi_guard, FfiError, IOTCOM_ERR_BUFFER_TOO_SMALL, IOTCOM_ERR_INVALID_ARG, IOTCOM_OK,
};
use modbus::{Framing, MasterConfig, MasterEvent, MasterMachine};

export_common!();

/// Configuration passed to [`iotcom_modbus_master_new`].
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct ModbusMasterCfg {
    /// 0 = TCP, 1 = RTU, 2 = ASCII.
    pub framing: u8,
    /// Response timeout in milliseconds.
    pub timeout_ms: u32,
    /// Max in-flight requests (TCP).
    pub max_in_flight: u16,
    /// Max queued requests.
    pub max_queue: u16,
}

/// Event kinds returned by [`iotcom_modbus_master_poll_event`].
pub const IOTCOM_MODBUS_EVENT_RESPONSE: u8 = 1;
/// Timeout event kind.
pub const IOTCOM_MODBUS_EVENT_TIMEOUT: u8 = 2;

/// A tagged event. `data`/`data_len` point into memory owned by the master and stay valid until the
/// next call on the same handle.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct ModbusEvent {
    /// [`IOTCOM_MODBUS_EVENT_RESPONSE`] or [`IOTCOM_MODBUS_EVENT_TIMEOUT`].
    pub kind: u8,
    /// Responding unit id.
    pub unit_id: u8,
    /// Exception code when the response is an exception PDU, else 0.
    pub exception_code: u8,
    /// Request id.
    pub id: u32,
    /// Response PDU.
    pub data: *const u8,
    /// Response PDU length.
    pub data_len: usize,
}

/// Opaque master handle.
pub struct ModbusMaster {
    machine: MasterMachine,
    last_event_data: Vec<u8>,
}

fn master<'a>(ptr: *mut ModbusMaster) -> Result<&'a mut ModbusMaster, FfiError> {
    // SAFETY: the .NET SafeHandle guarantees the pointer came from iotcom_modbus_master_new and is
    // not yet freed; calls on one handle are serialised by the caller.
    unsafe { ptr.as_mut() }.ok_or_else(|| FfiError::null("master"))
}

fn bytes<'a>(data: *const u8, len: usize) -> Result<&'a [u8], FfiError> {
    if len == 0 {
        return Ok(&[]);
    }
    if data.is_null() {
        return Err(FfiError::null("data"));
    }
    // SAFETY: caller guarantees `data` is valid for `len` bytes for the duration of the call.
    Ok(unsafe { slice::from_raw_parts(data, len) })
}

/// Creates a master. On success `*out` receives the handle.
///
/// # Safety
/// `cfg` must point to a valid config and `out` to writable storage.
#[no_mangle]
pub unsafe extern "C" fn iotcom_modbus_master_new(cfg: *const ModbusMasterCfg, out: *mut *mut ModbusMaster) -> i32 {
    ffi_guard(|| {
        // SAFETY: checked for null; caller contract.
        let cfg = unsafe { cfg.as_ref() }.ok_or_else(|| FfiError::null("cfg"))?;
        if out.is_null() {
            return Err(FfiError::null("out"));
        }
        let framing = Framing::from_u8(cfg.framing).ok_or_else(|| FfiError::new(IOTCOM_ERR_INVALID_ARG, "unknown framing"))?;
        let machine = MasterMachine::new(MasterConfig {
            framing,
            timeout_us: u64::from(cfg.timeout_ms.max(1)) * 1000,
            max_in_flight: usize::from(cfg.max_in_flight.max(1)),
            max_queue: usize::from(cfg.max_queue.max(1)),
        });
        let handle = Box::into_raw(Box::new(ModbusMaster { machine, last_event_data: Vec::new() }));
        // SAFETY: `out` checked for null above.
        unsafe { *out = handle };
        Ok(IOTCOM_OK)
    })
}

/// Releases a master. Null is ignored.
///
/// # Safety
/// `ptr` must come from [`iotcom_modbus_master_new`] and must not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn iotcom_modbus_master_free(ptr: *mut ModbusMaster) {
    if !ptr.is_null() {
        let _ = std::panic::catch_unwind(|| {
            // SAFETY: ownership is transferred back exactly once (SafeHandle.ReleaseHandle).
            drop(unsafe { Box::from_raw(ptr) });
        });
    }
}

/// Submits a request PDU for `unit_id`. `*out_id` receives the request id.
///
/// # Safety
/// `pdu` must be valid for `pdu_len` bytes; `out_id` writable.
#[no_mangle]
pub unsafe extern "C" fn iotcom_modbus_master_submit(
    ptr: *mut ModbusMaster,
    now_us: u64,
    unit_id: u8,
    pdu: *const u8,
    pdu_len: usize,
    out_id: *mut u32,
) -> i32 {
    ffi_guard(|| {
        let m = master(ptr)?;
        let id = m.machine.submit(Instant(now_us), unit_id, bytes(pdu, pdu_len)?)?;
        if !out_id.is_null() {
            // SAFETY: checked for null.
            unsafe { *out_id = id };
        }
        Ok(IOTCOM_OK)
    })
}

/// Feeds received bytes.
///
/// # Safety
/// `data` must be valid for `len` bytes.
#[no_mangle]
pub unsafe extern "C" fn iotcom_modbus_master_handle_input(ptr: *mut ModbusMaster, now_us: u64, data: *const u8, len: usize) -> i32 {
    ffi_guard(|| {
        let m = master(ptr)?;
        m.machine.handle_input(Instant(now_us), bytes(data, len)?)?;
        Ok(IOTCOM_OK)
    })
}

/// Processes expired timers.
///
/// # Safety
/// `ptr` must be a live handle.
#[no_mangle]
pub unsafe extern "C" fn iotcom_modbus_master_handle_timeout(ptr: *mut ModbusMaster, now_us: u64) -> i32 {
    ffi_guard(|| {
        master(ptr)?.machine.handle_timeout(Instant(now_us));
        Ok(IOTCOM_OK)
    })
}

/// Copies the next frame to transmit into `buf`. Returns the frame length (0 = nothing to send).
/// If `cap` is too small, returns `IOTCOM_ERR_BUFFER_TOO_SMALL` and the frame stays queued
/// (`*out_required` receives the needed size).
///
/// # Safety
/// `buf` must be valid for `cap` bytes; `out_required` may be null.
#[no_mangle]
pub unsafe extern "C" fn iotcom_modbus_master_poll_transmit(ptr: *mut ModbusMaster, buf: *mut u8, cap: usize, out_required: *mut usize) -> i32 {
    ffi_guard(|| {
        let m = master(ptr)?;
        let mut frame = Vec::new();
        let Some(t) = m.machine.poll_transmit(&mut frame) else { return Ok(0) };
        if t.len > cap || buf.is_null() {
            m.machine.requeue_transmit(frame); // keep it queued; the caller retries with a bigger buffer
            if !out_required.is_null() {
                // SAFETY: checked for null.
                unsafe { *out_required = t.len };
            }
            return Err(FfiError::new(IOTCOM_ERR_BUFFER_TOO_SMALL, format!("frame needs {} bytes", t.len)));
        }
        // SAFETY: buf valid for cap >= t.len bytes.
        unsafe { std::ptr::copy_nonoverlapping(frame.as_ptr(), buf, t.len) };
        Ok(i32::try_from(t.len).unwrap_or(i32::MAX))
    })
}

/// Retrieves the next event. Returns 1 when `*out` was filled, 0 when no event is pending.
///
/// # Safety
/// `out` must be writable. `out.data` is valid until the next call on this handle.
#[no_mangle]
pub unsafe extern "C" fn iotcom_modbus_master_poll_event(ptr: *mut ModbusMaster, out: *mut ModbusEvent) -> i32 {
    ffi_guard(|| {
        if out.is_null() {
            return Err(FfiError::null("out"));
        }
        let m = master(ptr)?;
        let Some(ev) = m.machine.poll_event() else { return Ok(0) };
        let event = match ev {
            MasterEvent::Response { id, unit_id, pdu } => {
                let exception_code = modbus::pdu::exception_code(&pdu).unwrap_or(0);
                m.last_event_data = pdu;
                ModbusEvent {
                    kind: IOTCOM_MODBUS_EVENT_RESPONSE,
                    unit_id,
                    exception_code,
                    id,
                    data: m.last_event_data.as_ptr(),
                    data_len: m.last_event_data.len(),
                }
            }
            MasterEvent::Timeout { id } => ModbusEvent {
                kind: IOTCOM_MODBUS_EVENT_TIMEOUT,
                unit_id: 0,
                exception_code: 0,
                id,
                data: std::ptr::null(),
                data_len: 0,
            },
        };
        // SAFETY: checked for null.
        unsafe { *out = event };
        Ok(1)
    })
}

/// Writes the next deadline (µs) to `*out_deadline_us`. Returns 1 if there is one, 0 otherwise.
///
/// # Safety
/// `out_deadline_us` must be writable.
#[no_mangle]
pub unsafe extern "C" fn iotcom_modbus_master_poll_timeout(ptr: *mut ModbusMaster, out_deadline_us: *mut u64) -> i32 {
    ffi_guard(|| {
        let m = master(ptr)?;
        match m.machine.poll_timeout() {
            Some(t) => {
                if !out_deadline_us.is_null() {
                    // SAFETY: checked for null.
                    unsafe { *out_deadline_us = t.as_micros() };
                }
                Ok(1)
            }
            None => Ok(0),
        }
    })
}

/// Number of queued + in-flight requests.
///
/// # Safety
/// `ptr` must be a live handle.
#[no_mangle]
pub unsafe extern "C" fn iotcom_modbus_master_pending(ptr: *mut ModbusMaster) -> i32 {
    ffi_guard(|| Ok(i32::try_from(master(ptr)?.machine.pending()).unwrap_or(i32::MAX)))
}

#[cfg(test)]
mod tests {
    use super::*;
    use iotcom_ffi_support::IOTCOM_ERR_NULL;

    #[test]
    fn abi_roundtrip() {
        unsafe {
            assert_eq!(iotcom_abi_version(), iotcom_ffi_support::ABI_VERSION);
            let cfg = ModbusMasterCfg { framing: 0, timeout_ms: 100, max_in_flight: 4, max_queue: 16 };
            let mut h: *mut ModbusMaster = std::ptr::null_mut();
            assert_eq!(iotcom_modbus_master_new(&cfg, &mut h), IOTCOM_OK);
            let pdu = [3u8, 0, 0, 0, 1];
            let mut id = 0;
            assert_eq!(iotcom_modbus_master_submit(h, 0, 1, pdu.as_ptr(), pdu.len(), &mut id), IOTCOM_OK);
            let mut buf = [0u8; 1024];
            let n = iotcom_modbus_master_poll_transmit(h, buf.as_mut_ptr(), buf.len(), std::ptr::null_mut());
            assert_eq!(&buf[..n as usize], &[0, 1, 0, 0, 0, 6, 1, 3, 0, 0, 0, 1]);
            let resp = [0u8, 1, 0, 0, 0, 5, 1, 3, 2, 0, 42];
            assert_eq!(iotcom_modbus_master_handle_input(h, 10, resp.as_ptr(), resp.len()), IOTCOM_OK);
            let mut ev = std::mem::zeroed::<ModbusEvent>();
            assert_eq!(iotcom_modbus_master_poll_event(h, &mut ev), 1);
            assert_eq!((ev.kind, ev.id, ev.data_len), (IOTCOM_MODBUS_EVENT_RESPONSE, id, 4));
            assert_eq!(std::slice::from_raw_parts(ev.data, ev.data_len), &[3, 2, 0, 42]);
            iotcom_modbus_master_free(h);
        }
    }

    #[test]
    fn null_pointers_are_rejected() {
        unsafe {
            assert_eq!(iotcom_modbus_master_handle_timeout(std::ptr::null_mut(), 0), IOTCOM_ERR_NULL);
            assert_eq!(iotcom_modbus_master_new(std::ptr::null(), std::ptr::null_mut()), IOTCOM_ERR_NULL);
        }
    }
}
