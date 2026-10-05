//! C ABI for the ISO-TP channel machine (`iotcom_isotp` native library).
//!
//! Contract (see `iotcom-ffi-support`): every function returns an `i32` status (`IOTCOM_OK` = 0,
//! negative = error, details via `iotcom_last_error`), never unwinds, and works on an opaque
//! `IsoTpChannel` handle released with `iotcom_isotp_free`. Calls on one handle must be serialised.

use std::slice;

use iotcom_core::{Instant, Machine};
use iotcom_ffi_support::{export_common, ffi_guard, FfiError, IOTCOM_ERR_BUFFER_TOO_SMALL, IOTCOM_OK};
use isotp::{IsoTpConfig, IsoTpEvent, IsoTpMachine};

export_common!();

/// Configuration passed to [`iotcom_isotp_new`]. Flags: bit 0 = pad frames, bit 1 = extended
/// addressing on transmit, bit 2 = extended addressing on receive.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct IsoTpCfg {
    /// Transmit data length (8 classic; 8–64 CAN FD).
    pub tx_dl: u8,
    /// Padding byte (used when flag bit 0 is set).
    pub padding: u8,
    /// Option flags.
    pub flags: u8,
    /// Extended address prepended on transmit.
    pub ext_addr_tx: u8,
    /// Extended address expected on receive.
    pub ext_addr_rx: u8,
    /// Block size we announce.
    pub block_size: u8,
    /// STmin we announce (raw ISO encoding).
    pub st_min: u8,
    /// Max FC.WAIT frames accepted.
    pub max_wait_frames: u16,
    /// N_Bs timeout in milliseconds.
    pub n_bs_ms: u32,
    /// N_Cr timeout in milliseconds.
    pub n_cr_ms: u32,
    /// Largest message accepted.
    pub max_rx_len: u32,
}

/// Event kinds returned by [`iotcom_isotp_poll_event`].
pub const IOTCOM_ISOTP_EVENT_RECEIVED: u8 = 1;
/// A first frame announced `value` bytes.
pub const IOTCOM_ISOTP_EVENT_RX_STARTED: u8 = 2;
/// The current message was fully sent.
pub const IOTCOM_ISOTP_EVENT_SENT: u8 = 3;
/// A transfer failed; `value` holds the `IsoTpError` code.
pub const IOTCOM_ISOTP_EVENT_ERROR: u8 = 4;

/// A tagged event. `data`/`data_len` stay valid until the next call on the same handle.
#[repr(C)]
#[derive(Clone, Copy, Debug)]
pub struct IsoTpEventC {
    /// Event kind.
    pub kind: u8,
    /// Error code or announced length.
    pub value: u32,
    /// Received message.
    pub data: *const u8,
    /// Received message length.
    pub data_len: usize,
}

/// Opaque channel handle.
pub struct IsoTpChannel {
    machine: IsoTpMachine,
    last_event_data: Vec<u8>,
}

fn channel<'a>(ptr: *mut IsoTpChannel) -> Result<&'a mut IsoTpChannel, FfiError> {
    // SAFETY: the .NET SafeHandle guarantees the pointer came from iotcom_isotp_new and is not yet
    // freed; calls on one handle are serialised by the caller.
    unsafe { ptr.as_mut() }.ok_or_else(|| FfiError::null("channel"))
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

/// Creates a channel. On success `*out` receives the handle.
///
/// # Safety
/// `cfg` must point to a valid config and `out` to writable storage.
#[no_mangle]
pub unsafe extern "C" fn iotcom_isotp_new(cfg: *const IsoTpCfg, out: *mut *mut IsoTpChannel) -> i32 {
    ffi_guard(|| {
        // SAFETY: checked for null; caller contract.
        let c = unsafe { cfg.as_ref() }.ok_or_else(|| FfiError::null("cfg"))?;
        if out.is_null() {
            return Err(FfiError::null("out"));
        }
        let machine = IsoTpMachine::new(IsoTpConfig {
            tx_dl: usize::from(c.tx_dl),
            padding: (c.flags & 1 != 0).then_some(c.padding),
            ext_addr_tx: (c.flags & 2 != 0).then_some(c.ext_addr_tx),
            ext_addr_rx: (c.flags & 4 != 0).then_some(c.ext_addr_rx),
            block_size: c.block_size,
            st_min: c.st_min,
            n_bs_us: u64::from(c.n_bs_ms.max(1)) * 1000,
            n_cr_us: u64::from(c.n_cr_ms.max(1)) * 1000,
            max_wait_frames: c.max_wait_frames,
            max_rx_len: c.max_rx_len.max(1) as usize,
        })?;
        let handle = Box::into_raw(Box::new(IsoTpChannel { machine, last_event_data: Vec::new() }));
        // SAFETY: `out` checked for null above.
        unsafe { *out = handle };
        Ok(IOTCOM_OK)
    })
}

/// Releases a channel. Null is ignored.
///
/// # Safety
/// `ptr` must come from [`iotcom_isotp_new`] and must not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn iotcom_isotp_free(ptr: *mut IsoTpChannel) {
    if !ptr.is_null() {
        let _ = std::panic::catch_unwind(|| {
            // SAFETY: ownership is transferred back exactly once (SafeHandle.ReleaseHandle).
            drop(unsafe { Box::from_raw(ptr) });
        });
    }
}

/// Starts sending a message. Returns `IOTCOM_ERR_BUSY` while another message is in progress.
///
/// # Safety
/// `data` must be valid for `len` bytes.
#[no_mangle]
pub unsafe extern "C" fn iotcom_isotp_send(ptr: *mut IsoTpChannel, now_us: u64, data: *const u8, len: usize) -> i32 {
    ffi_guard(|| {
        let c = channel(ptr)?;
        c.machine.send(Instant(now_us), bytes(data, len)?)?;
        Ok(IOTCOM_OK)
    })
}

/// Feeds the data bytes of one received CAN frame.
///
/// # Safety
/// `data` must be valid for `len` bytes.
#[no_mangle]
pub unsafe extern "C" fn iotcom_isotp_handle_frame(ptr: *mut IsoTpChannel, now_us: u64, data: *const u8, len: usize) -> i32 {
    ffi_guard(|| {
        let c = channel(ptr)?;
        c.machine.handle_input(Instant(now_us), bytes(data, len)?)?;
        Ok(IOTCOM_OK)
    })
}

/// Processes expired timers (and paces consecutive frames by STmin).
///
/// # Safety
/// `ptr` must be a live handle.
#[no_mangle]
pub unsafe extern "C" fn iotcom_isotp_handle_timeout(ptr: *mut IsoTpChannel, now_us: u64) -> i32 {
    ffi_guard(|| {
        channel(ptr)?.machine.handle_timeout(Instant(now_us));
        Ok(IOTCOM_OK)
    })
}

/// Aborts the current transfers.
///
/// # Safety
/// `ptr` must be a live handle.
#[no_mangle]
pub unsafe extern "C" fn iotcom_isotp_reset(ptr: *mut IsoTpChannel) -> i32 {
    ffi_guard(|| {
        channel(ptr)?.machine.reset();
        Ok(IOTCOM_OK)
    })
}

/// Copies the next CAN frame payload to transmit into `buf` (64 bytes is always enough).
/// Returns its length, or 0 when nothing is pending.
///
/// # Safety
/// `buf` must be valid for `cap` bytes.
#[no_mangle]
pub unsafe extern "C" fn iotcom_isotp_poll_frame(ptr: *mut IsoTpChannel, buf: *mut u8, cap: usize) -> i32 {
    ffi_guard(|| {
        let c = channel(ptr)?;
        let mut frame = Vec::with_capacity(64);
        let Some(t) = c.machine.poll_transmit(&mut frame) else { return Ok(0) };
        if t.len > cap || buf.is_null() {
            c.machine.requeue_transmit(frame);
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
pub unsafe extern "C" fn iotcom_isotp_poll_event(ptr: *mut IsoTpChannel, out: *mut IsoTpEventC) -> i32 {
    ffi_guard(|| {
        if out.is_null() {
            return Err(FfiError::null("out"));
        }
        let c = channel(ptr)?;
        let Some(ev) = c.machine.poll_event() else { return Ok(0) };
        let mut e = IsoTpEventC { kind: 0, value: 0, data: std::ptr::null(), data_len: 0 };
        match ev {
            IsoTpEvent::Received(d) => {
                c.last_event_data = d;
                e.kind = IOTCOM_ISOTP_EVENT_RECEIVED;
                e.data = c.last_event_data.as_ptr();
                e.data_len = c.last_event_data.len();
                e.value = u32::try_from(e.data_len).unwrap_or(u32::MAX);
            }
            IsoTpEvent::RxStarted(n) => {
                e.kind = IOTCOM_ISOTP_EVENT_RX_STARTED;
                e.value = u32::try_from(n).unwrap_or(u32::MAX);
            }
            IsoTpEvent::Sent => e.kind = IOTCOM_ISOTP_EVENT_SENT,
            IsoTpEvent::Error(err) => {
                e.kind = IOTCOM_ISOTP_EVENT_ERROR;
                e.value = err as u32;
            }
        }
        // SAFETY: checked for null.
        unsafe { *out = e };
        Ok(1)
    })
}

/// Writes the next deadline (µs) to `*out_deadline_us`. Returns 1 if there is one, 0 otherwise.
///
/// # Safety
/// `out_deadline_us` must be writable.
#[no_mangle]
pub unsafe extern "C" fn iotcom_isotp_poll_timeout(ptr: *mut IsoTpChannel, out_deadline_us: *mut u64) -> i32 {
    ffi_guard(|| {
        let c = channel(ptr)?;
        match c.machine.poll_timeout() {
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

#[cfg(test)]
mod tests {
    use super::*;
    use iotcom_ffi_support::{IOTCOM_ERR_BUSY, IOTCOM_ERR_INVALID_ARG, IOTCOM_ERR_NULL};

    fn cfg() -> IsoTpCfg {
        IsoTpCfg {
            tx_dl: 8,
            padding: 0xAA,
            flags: 1,
            ext_addr_tx: 0,
            ext_addr_rx: 0,
            block_size: 0,
            st_min: 0,
            max_wait_frames: 10,
            n_bs_ms: 1000,
            n_cr_ms: 1000,
            max_rx_len: 4095,
        }
    }

    #[test]
    fn abi_roundtrip() {
        unsafe {
            assert_eq!(iotcom_abi_version(), iotcom_ffi_support::ABI_VERSION);
            let mut h: *mut IsoTpChannel = std::ptr::null_mut();
            assert_eq!(iotcom_isotp_new(&cfg(), &mut h), IOTCOM_OK);
            let msg = [0x22u8; 12];
            assert_eq!(iotcom_isotp_send(h, 0, msg.as_ptr(), msg.len()), IOTCOM_OK);
            assert_eq!(iotcom_isotp_send(h, 0, msg.as_ptr(), msg.len()), IOTCOM_ERR_BUSY);
            let mut buf = [0u8; 64];
            assert_eq!(iotcom_isotp_poll_frame(h, buf.as_mut_ptr(), buf.len()), 8);
            assert_eq!(&buf[..2], &[0x10, 12]);
            assert_eq!(iotcom_isotp_poll_frame(h, buf.as_mut_ptr(), buf.len()), 0, "waits for flow control");
            let fc = [0x30u8, 0, 0];
            assert_eq!(iotcom_isotp_handle_frame(h, 1, fc.as_ptr(), fc.len()), IOTCOM_OK);
            assert_eq!(iotcom_isotp_poll_frame(h, buf.as_mut_ptr(), 4), IOTCOM_ERR_BUFFER_TOO_SMALL);
            assert_eq!(iotcom_isotp_poll_frame(h, buf.as_mut_ptr(), buf.len()), 8);
            assert_eq!(&buf[..8], &[0x21, 0x22, 0x22, 0x22, 0x22, 0x22, 0x22, 0xAA]);
            let mut ev = std::mem::zeroed::<IsoTpEventC>();
            assert_eq!(iotcom_isotp_poll_event(h, &mut ev), 1);
            assert_eq!(ev.kind, IOTCOM_ISOTP_EVENT_SENT);
            let sf = [0x03u8, 0x62, 0xF1, 0x90];
            assert_eq!(iotcom_isotp_handle_frame(h, 2, sf.as_ptr(), sf.len()), IOTCOM_OK);
            assert_eq!(iotcom_isotp_poll_event(h, &mut ev), 1);
            assert_eq!(std::slice::from_raw_parts(ev.data, ev.data_len), &[0x62, 0xF1, 0x90]);
            iotcom_isotp_free(h);
        }
    }

    #[test]
    fn invalid_configuration_and_nulls() {
        unsafe {
            let mut h: *mut IsoTpChannel = std::ptr::null_mut();
            assert_eq!(iotcom_isotp_new(&IsoTpCfg { tx_dl: 10, ..cfg() }, &mut h), IOTCOM_ERR_INVALID_ARG);
            assert_eq!(iotcom_isotp_handle_timeout(std::ptr::null_mut(), 0), IOTCOM_ERR_NULL);
        }
    }
}
