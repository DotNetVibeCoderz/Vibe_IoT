//! C ABI for raw USB (nusb: control, bulk and interrupt transfers) and HID (hidapi) — the
//! `iotcom_usb` native library behind `IoTCom.Net.Transport.Usb`.
//!
//! Contract (see `iotcom-ffi-support`): every function returns an `i32` status (`IOTCOM_OK` = 0 or a
//! non-negative count, negative = error, details via `iotcom_last_error`) and never unwinds. Handles
//! are opaque and released with exactly one `*_free`; calls on one handle must be serialised. Device
//! and HID lists are returned as JSON. Transfers block for at most `timeout_ms`;
//! [`IOTCOM_ERR_USB_TIMEOUT`] means nothing arrived in time and is not fatal.

use std::collections::HashMap;
use std::ffi::CString;
use std::fmt::Write as _;
use std::slice;
use std::sync::Mutex;
use std::time::Duration;

use iotcom_ffi_support::{export_common, ffi_guard, FfiError, IOTCOM_ERR_BUFFER_TOO_SMALL, IOTCOM_ERR_INVALID_ARG, IOTCOM_OK};
use nusb::transfer::{Bulk, ControlIn, ControlOut, ControlType, In, Interrupt, Out, Recipient, TransferError};
use nusb::{Device, Endpoint, Interface, MaybeFuture};

export_common!();

/// Status: the USB stack refused the operation (driver, permission, busy interface…).
pub const IOTCOM_ERR_USB: i32 = -30;
/// Status: the transfer timed out (no data); the handle stays usable.
pub const IOTCOM_ERR_USB_TIMEOUT: i32 = -31;
/// Status: the endpoint stalled (the device rejected the request).
pub const IOTCOM_ERR_USB_STALL: i32 = -32;
/// Status: the device is gone.
pub const IOTCOM_ERR_USB_DISCONNECTED: i32 = -33;
/// Status: no device matches the id.
pub const IOTCOM_ERR_USB_NOT_FOUND: i32 = -34;

/// Lower-case-free JSON string escaping.
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

fn opt(s: Option<&str>) -> String {
    s.map_or_else(|| "null".to_string(), json_string)
}

fn text<'a>(ptr: *const u8, len: usize, what: &str) -> Result<&'a str, FfiError> {
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

fn usb(e: nusb::Error) -> FfiError {
    FfiError::new(IOTCOM_ERR_USB, format!("USB: {e}"))
}

fn transfer(e: TransferError) -> FfiError {
    match e {
        TransferError::Cancelled => FfiError::new(IOTCOM_ERR_USB_TIMEOUT, "USB transfer timed out"),
        TransferError::Stall => FfiError::new(IOTCOM_ERR_USB_STALL, "USB endpoint stalled"),
        TransferError::Disconnected => FfiError::new(IOTCOM_ERR_USB_DISCONNECTED, "USB device disconnected"),
        other => FfiError::new(IOTCOM_ERR_USB, format!("USB transfer failed: {other}")),
    }
}

/// Parses `VVVV:PPPP[:serial]` (hex vendor and product ids).
pub fn parse_id(id: &str) -> Option<(u16, u16, Option<&str>)> {
    let mut parts = id.splitn(3, ':');
    let vid = u16::from_str_radix(parts.next()?.trim(), 16).ok()?;
    let pid = u16::from_str_radix(parts.next()?.trim(), 16).ok()?;
    Some((vid, pid, parts.next().filter(|s| !s.is_empty())))
}

/// Splits `bmRequestType` into the nusb control type and recipient.
pub fn request_type(bm: u8) -> Result<(ControlType, Recipient), FfiError> {
    let kind = match (bm >> 5) & 0x03 {
        0 => ControlType::Standard,
        1 => ControlType::Class,
        2 => ControlType::Vendor,
        _ => return Err(FfiError::new(IOTCOM_ERR_INVALID_ARG, "bmRequestType type 3 is reserved")),
    };
    let recipient = match bm & 0x1F {
        0 => Recipient::Device,
        1 => Recipient::Interface,
        2 => Recipient::Endpoint,
        3 => Recipient::Other,
        _ => return Err(FfiError::new(IOTCOM_ERR_INVALID_ARG, "bmRequestType recipient is reserved")),
    };
    Ok((kind, recipient))
}

// ---- enumeration ---------------------------------------------------------------------------------

/// Writes the attached USB devices as a JSON array into `buf`.
///
/// # Safety
/// `buf` valid for `len` bytes; `written` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_usb_list(buf: *mut u8, len: usize, written: *mut usize) -> i32 {
    ffi_guard(|| {
        let devices = nusb::list_devices().wait().map_err(usb)?;
        let mut s = String::from("[");
        for (i, d) in devices.enumerate() {
            if i > 0 {
                s.push(',');
            }
            let _ = write!(
                s,
                "{{\"id\":\"{:04x}:{:04x}{}\",\"vid\":{},\"pid\":{},\"manufacturer\":{},\"product\":{},\"serial\":{},\"class\":{},\"bus\":{},\"address\":{},\"interfaces\":[",
                d.vendor_id(), d.product_id(), d.serial_number().map(|s| format!(":{s}")).unwrap_or_default(),
                d.vendor_id(), d.product_id(), opt(d.manufacturer_string()), opt(d.product_string()), opt(d.serial_number()),
                d.class(), json_string(d.bus_id()), d.device_address());
            for (j, f) in d.interfaces().enumerate() {
                if j > 0 {
                    s.push(',');
                }
                let _ = write!(s, "{{\"number\":{},\"class\":{},\"subclass\":{},\"protocol\":{},\"name\":{}}}",
                    f.interface_number(), f.class(), f.subclass(), f.protocol(), opt(f.interface_string()));
            }
            s.push_str("]}");
        }
        s.push(']');
        // SAFETY: forwarded caller contract.
        unsafe { copy_out(s.as_bytes(), buf, len, written) }
    })
}

// ---- raw USB device ------------------------------------------------------------------------------

enum Ep {
    BulkIn(Endpoint<Bulk, In>),
    BulkOut(Endpoint<Bulk, Out>),
    IntIn(Endpoint<Interrupt, In>),
    IntOut(Endpoint<Interrupt, Out>),
}

/// Opaque USB device handle.
pub struct UsbHandle {
    device: Device,
    interfaces: HashMap<u8, Interface>,
    endpoints: HashMap<u8, Ep>,
}

fn handle<'a>(ptr: *mut UsbHandle) -> Result<&'a mut UsbHandle, FfiError> {
    // SAFETY: the .NET SafeHandle guarantees the pointer came from iotcom_usb_open and is live.
    unsafe { ptr.as_mut() }.ok_or_else(|| FfiError::null("device"))
}

impl UsbHandle {
    fn interface(&self, number: u8) -> Result<&Interface, FfiError> {
        self.interfaces
            .get(&number)
            .or_else(|| if number == 0xFF { self.interfaces.values().next() } else { None })
            .ok_or_else(|| FfiError::new(IOTCOM_ERR_INVALID_ARG, format!("interface {number} is not claimed (call iotcom_usb_claim)")))
    }

    fn owner(&self, endpoint: u8) -> Result<&Interface, FfiError> {
        self.interfaces
            .values()
            .find(|i| i.descriptor().is_some_and(|d| d.endpoints().any(|e| e.address() == endpoint)))
            .or_else(|| self.interfaces.values().next())
            .ok_or_else(|| FfiError::new(IOTCOM_ERR_INVALID_ARG, "claim the interface that owns the endpoint first"))
    }
}

/// Opens the first device matching `VVVV:PPPP[:serial]`.
///
/// # Safety
/// `id` valid for `id_len` bytes; `out` valid for one pointer write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_usb_open(id: *const u8, id_len: usize, out: *mut *mut UsbHandle) -> i32 {
    ffi_guard(|| {
        if out.is_null() {
            return Err(FfiError::null("out"));
        }
        let id = text(id, id_len, "id")?;
        let (vid, pid, serial) = parse_id(id).ok_or_else(|| FfiError::new(IOTCOM_ERR_INVALID_ARG, format!("'{id}' is not VVVV:PPPP[:serial]")))?;
        let info = nusb::list_devices()
            .wait()
            .map_err(usb)?
            .find(|d| d.vendor_id() == vid && d.product_id() == pid && serial.map_or(true, |s| d.serial_number() == Some(s)))
            .ok_or_else(|| FfiError::new(IOTCOM_ERR_USB_NOT_FOUND, format!("no USB device {id}")))?;
        let device = info.open().wait().map_err(usb)?;
        let h = Box::new(UsbHandle { device, interfaces: HashMap::new(), endpoints: HashMap::new() });
        // SAFETY: checked non-null.
        unsafe { *out = Box::into_raw(h) };
        Ok(IOTCOM_OK)
    })
}

/// Releases a device handle (and its claimed interfaces).
///
/// # Safety
/// `ptr` from [`iotcom_usb_open`], not used afterwards.
#[no_mangle]
pub unsafe extern "C" fn iotcom_usb_free(ptr: *mut UsbHandle) {
    if !ptr.is_null() {
        let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            // SAFETY: ownership returns to Rust exactly once.
            drop(unsafe { Box::from_raw(ptr) });
        }));
    }
}

/// Claims an interface (detaching a kernel driver on Linux when `detach` != 0).
///
/// # Safety
/// `ptr` must be live.
#[no_mangle]
pub unsafe extern "C" fn iotcom_usb_claim(ptr: *mut UsbHandle, interface: u8, detach: u8) -> i32 {
    ffi_guard(|| {
        let h = handle(ptr)?;
        if h.interfaces.contains_key(&interface) {
            return Ok(IOTCOM_OK);
        }
        let claimed = if detach != 0 { h.device.detach_and_claim_interface(interface).wait() } else { h.device.claim_interface(interface).wait() }.map_err(usb)?;
        h.interfaces.insert(interface, claimed);
        Ok(IOTCOM_OK)
    })
}

/// Control IN transfer on the default endpoint, through claimed `interface` (0xFF = any claimed).
///
/// # Safety
/// `buf` valid for `len` bytes; `written` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_usb_control_in(
    ptr: *mut UsbHandle, interface: u8, bm_request_type: u8, request: u8, value: u16, index: u16, buf: *mut u8, len: usize, timeout_ms: u32, written: *mut usize,
) -> i32 {
    ffi_guard(|| {
        let h = handle(ptr)?;
        let (control_type, recipient) = request_type(bm_request_type)?;
        let length = u16::try_from(len).unwrap_or(u16::MAX);
        let data = h
            .interface(interface)?
            .control_in(ControlIn { control_type, recipient, request, value, index, length }, Duration::from_millis(u64::from(timeout_ms.max(1))))
            .wait()
            .map_err(transfer)?;
        // SAFETY: forwarded caller contract.
        unsafe { copy_out(&data, buf, len, written) }
    })
}

/// Control OUT transfer on the default endpoint.
///
/// # Safety
/// `data` valid for `data_len` bytes.
#[no_mangle]
pub unsafe extern "C" fn iotcom_usb_control_out(
    ptr: *mut UsbHandle, interface: u8, bm_request_type: u8, request: u8, value: u16, index: u16, data: *const u8, data_len: usize, timeout_ms: u32,
) -> i32 {
    ffi_guard(|| {
        let h = handle(ptr)?;
        let (control_type, recipient) = request_type(bm_request_type)?;
        let data = input(data, data_len)?;
        h.interface(interface)?
            .control_out(ControlOut { control_type, recipient, request, value, index, data }, Duration::from_millis(u64::from(timeout_ms.max(1))))
            .wait()
            .map_err(transfer)?;
        Ok(IOTCOM_OK)
    })
}

/// Writes to an OUT endpoint (`interrupt` != 0 for interrupt endpoints). Returns the bytes sent.
///
/// # Safety
/// `data` valid for `data_len` bytes.
#[no_mangle]
pub unsafe extern "C" fn iotcom_usb_write(ptr: *mut UsbHandle, endpoint: u8, interrupt: u8, data: *const u8, data_len: usize, timeout_ms: u32) -> i32 {
    ffi_guard(|| {
        if endpoint & 0x80 != 0 {
            return Err(FfiError::new(IOTCOM_ERR_INVALID_ARG, format!("0x{endpoint:02x} is an IN endpoint")));
        }
        let h = handle(ptr)?;
        let payload = input(data, data_len)?.to_vec();
        if !h.endpoints.contains_key(&endpoint) {
            let iface = h.owner(endpoint)?;
            let ep = if interrupt != 0 { Ep::IntOut(iface.endpoint::<Interrupt, Out>(endpoint).map_err(usb)?) } else { Ep::BulkOut(iface.endpoint::<Bulk, Out>(endpoint).map_err(usb)?) };
            h.endpoints.insert(endpoint, ep);
        }
        let timeout = Duration::from_millis(u64::from(timeout_ms.max(1)));
        let completion = match h.endpoints.get_mut(&endpoint) {
            Some(Ep::BulkOut(e)) => e.transfer_blocking(payload.into(), timeout),
            Some(Ep::IntOut(e)) => e.transfer_blocking(payload.into(), timeout),
            _ => return Err(FfiError::new(IOTCOM_ERR_INVALID_ARG, "endpoint type mismatch")),
        };
        completion.status.map_err(transfer)?;
        Ok(i32::try_from(completion.actual_len).unwrap_or(i32::MAX))
    })
}

/// Reads one transfer from an IN endpoint into `buf` (rounded up to the max packet size internally).
///
/// # Safety
/// `buf` valid for `len` bytes; `written` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_usb_read(ptr: *mut UsbHandle, endpoint: u8, interrupt: u8, buf: *mut u8, len: usize, timeout_ms: u32, written: *mut usize) -> i32 {
    ffi_guard(|| {
        if endpoint & 0x80 == 0 {
            return Err(FfiError::new(IOTCOM_ERR_INVALID_ARG, format!("0x{endpoint:02x} is an OUT endpoint")));
        }
        let h = handle(ptr)?;
        if !h.endpoints.contains_key(&endpoint) {
            let iface = h.owner(endpoint)?;
            let ep = if interrupt != 0 { Ep::IntIn(iface.endpoint::<Interrupt, In>(endpoint).map_err(usb)?) } else { Ep::BulkIn(iface.endpoint::<Bulk, In>(endpoint).map_err(usb)?) };
            h.endpoints.insert(endpoint, ep);
        }
        let timeout = Duration::from_millis(u64::from(timeout_ms.max(1)));
        let completion = match h.endpoints.get_mut(&endpoint) {
            Some(Ep::BulkIn(e)) => {
                let mps = e.max_packet_size().max(1);
                let buffer = e.allocate(len.max(1).div_ceil(mps) * mps);
                e.transfer_blocking(buffer, timeout)
            }
            Some(Ep::IntIn(e)) => {
                let mps = e.max_packet_size().max(1);
                let buffer = e.allocate(len.max(1).div_ceil(mps) * mps);
                e.transfer_blocking(buffer, timeout)
            }
            _ => return Err(FfiError::new(IOTCOM_ERR_INVALID_ARG, "endpoint type mismatch")),
        };
        completion.status.map_err(transfer)?;
        let n = completion.actual_len.min(completion.buffer.len());
        // SAFETY: forwarded caller contract.
        unsafe { copy_out(&completion.buffer[..n], buf, len, written) }
    })
}

// ---- HID -----------------------------------------------------------------------------------------

static HID: Mutex<Option<hidapi::HidApi>> = Mutex::new(None);

fn hid_error(e: hidapi::HidError) -> FfiError {
    FfiError::new(IOTCOM_ERR_USB, format!("HID: {e}"))
}

fn with_hid<T>(f: impl FnOnce(&mut hidapi::HidApi) -> Result<T, FfiError>) -> Result<T, FfiError> {
    let mut guard = HID.lock().unwrap_or_else(|p| p.into_inner());
    if guard.is_none() {
        *guard = Some(hidapi::HidApi::new().map_err(hid_error)?);
    }
    f(guard.as_mut().expect("initialised above"))
}

/// Writes the HID devices as a JSON array into `buf`.
///
/// # Safety
/// `buf` valid for `len` bytes; `written` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_hid_list(buf: *mut u8, len: usize, written: *mut usize) -> i32 {
    ffi_guard(|| {
        let s = with_hid(|api| {
            api.refresh_devices().map_err(hid_error)?;
            let mut s = String::from("[");
            for (i, d) in api.device_list().enumerate() {
                if i > 0 {
                    s.push(',');
                }
                let _ = write!(
                    s,
                    "{{\"path\":{},\"vid\":{},\"pid\":{},\"manufacturer\":{},\"product\":{},\"serial\":{},\"usage_page\":{},\"usage\":{},\"interface\":{}}}",
                    json_string(&d.path().to_string_lossy()), d.vendor_id(), d.product_id(), opt(d.manufacturer_string()), opt(d.product_string()),
                    opt(d.serial_number()), d.usage_page(), d.usage(), d.interface_number());
            }
            s.push(']');
            Ok(s)
        })?;
        // SAFETY: forwarded caller contract.
        unsafe { copy_out(s.as_bytes(), buf, len, written) }
    })
}

/// Opaque HID handle.
pub struct HidHandle {
    device: hidapi::HidDevice,
}

fn hid<'a>(ptr: *mut HidHandle) -> Result<&'a HidHandle, FfiError> {
    // SAFETY: the .NET SafeHandle guarantees the pointer came from iotcom_hid_open and is live.
    unsafe { ptr.as_ref() }.ok_or_else(|| FfiError::null("hid"))
}

/// Opens a HID device by the platform path from [`iotcom_hid_list`].
///
/// # Safety
/// `path` valid for `path_len` bytes; `out` valid for one pointer write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_hid_open(path: *const u8, path_len: usize, out: *mut *mut HidHandle) -> i32 {
    ffi_guard(|| {
        if out.is_null() {
            return Err(FfiError::null("out"));
        }
        let path = CString::new(text(path, path_len, "path")?).map_err(|_| FfiError::new(IOTCOM_ERR_INVALID_ARG, "path contains NUL"))?;
        let device = with_hid(|api| api.open_path(&path).map_err(hid_error))?;
        // SAFETY: checked non-null.
        unsafe { *out = Box::into_raw(Box::new(HidHandle { device })) };
        Ok(IOTCOM_OK)
    })
}

/// Releases a HID handle.
///
/// # Safety
/// `ptr` from [`iotcom_hid_open`], not used afterwards.
#[no_mangle]
pub unsafe extern "C" fn iotcom_hid_free(ptr: *mut HidHandle) {
    if !ptr.is_null() {
        let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            // SAFETY: ownership returns to Rust exactly once.
            drop(unsafe { Box::from_raw(ptr) });
        }));
    }
}

/// Writes an output report (first byte = report id, 0 when the device uses none). Returns bytes written.
///
/// # Safety
/// `data` valid for `data_len` bytes.
#[no_mangle]
pub unsafe extern "C" fn iotcom_hid_write(ptr: *mut HidHandle, data: *const u8, data_len: usize) -> i32 {
    ffi_guard(|| {
        let n = hid(ptr)?.device.write(input(data, data_len)?).map_err(hid_error)?;
        Ok(i32::try_from(n).unwrap_or(i32::MAX))
    })
}

/// Reads one input report; `written` = 0 when nothing arrived within `timeout_ms` (-1 = wait forever).
///
/// # Safety
/// `buf` valid for `len` bytes; `written` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_hid_read(ptr: *mut HidHandle, buf: *mut u8, len: usize, timeout_ms: i32, written: *mut usize) -> i32 {
    ffi_guard(|| {
        if buf.is_null() || written.is_null() {
            return Err(FfiError::null("buf"));
        }
        // SAFETY: caller guarantees buf valid for len bytes.
        let out = unsafe { slice::from_raw_parts_mut(buf, len) };
        let n = hid(ptr)?.device.read_timeout(out, timeout_ms).map_err(hid_error)?;
        // SAFETY: checked non-null.
        unsafe { *written = n };
        Ok(IOTCOM_OK)
    })
}

/// Sends a feature report (first byte = report id).
///
/// # Safety
/// `data` valid for `data_len` bytes.
#[no_mangle]
pub unsafe extern "C" fn iotcom_hid_send_feature(ptr: *mut HidHandle, data: *const u8, data_len: usize) -> i32 {
    ffi_guard(|| {
        hid(ptr)?.device.send_feature_report(input(data, data_len)?).map_err(hid_error)?;
        Ok(IOTCOM_OK)
    })
}

/// Reads a feature report; `buf[0]` must hold the report id on entry.
///
/// # Safety
/// `buf` valid for `len` bytes; `written` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_hid_get_feature(ptr: *mut HidHandle, buf: *mut u8, len: usize, written: *mut usize) -> i32 {
    ffi_guard(|| {
        if buf.is_null() || written.is_null() {
            return Err(FfiError::null("buf"));
        }
        // SAFETY: caller guarantees buf valid for len bytes.
        let out = unsafe { slice::from_raw_parts_mut(buf, len) };
        let n = hid(ptr)?.device.get_feature_report(out).map_err(hid_error)?;
        // SAFETY: checked non-null.
        unsafe { *written = n };
        Ok(IOTCOM_OK)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ids_parse() {
        assert_eq!(parse_id("1d50:606f"), Some((0x1d50, 0x606f, None)));
        assert_eq!(parse_id("0483:5740:ABC123"), Some((0x0483, 0x5740, Some("ABC123"))));
        assert_eq!(parse_id("zz:1"), None);
        assert_eq!(parse_id("1234"), None);
    }

    #[test]
    fn request_types_split() {
        let (t, r) = request_type(0xC1).unwrap(); // device-to-host, vendor, interface
        assert!(matches!((t, r), (ControlType::Vendor, Recipient::Interface)));
        let (t, r) = request_type(0x21).unwrap(); // class, interface (CDC SET_LINE_CODING)
        assert!(matches!((t, r), (ControlType::Class, Recipient::Interface)));
        assert!(request_type(0x60).is_err());
    }

    #[test]
    fn json_escapes_and_null_handles() {
        assert_eq!(json_string("a\"b\\"), "\"a\\\"b\\\\\"");
        assert_eq!(opt(None), "null");
        assert_eq!(unsafe { iotcom_usb_claim(std::ptr::null_mut(), 0, 0) }, iotcom_ffi_support::IOTCOM_ERR_NULL);
        let mut written = 0;
        assert_eq!(unsafe { iotcom_hid_read(std::ptr::null_mut(), std::ptr::null_mut(), 0, 0, &mut written) }, iotcom_ffi_support::IOTCOM_ERR_NULL);
    }
}
