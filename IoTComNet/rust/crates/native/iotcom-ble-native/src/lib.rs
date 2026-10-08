//! C ABI for a Bluetooth Low Energy central (`iotcom_ble` native library), built on btleplug
//! (WinRT on Windows, BlueZ on Linux, CoreBluetooth on macOS).
//!
//! Contract (see `iotcom-ffi-support`): every function returns an `i32` status (`IOTCOM_OK` = 0,
//! negative = error, details via `iotcom_last_error`) and never unwinds. A `BleCentral` handle owns a
//! Tokio runtime; calls block until the operation completes or `timeout_ms` passes. Asynchronous
//! happenings (advertisements, notifications, disconnections) are queued as one-line JSON events and
//! drained with [`iotcom_ble_poll_event`]. Strings cross the boundary as UTF-8 pointer + length.

use std::collections::VecDeque;
use std::fmt::Write as _;
use std::slice;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use btleplug::api::{
    Central, CentralEvent, CharPropFlags, Characteristic, Manager as _, Peripheral as _, ScanFilter, WriteType,
};
use btleplug::platform::{Adapter, Manager, Peripheral};
use futures::StreamExt;
use iotcom_ffi_support::{
    export_common, ffi_guard, FfiError, IOTCOM_ERR_BUFFER_TOO_SMALL, IOTCOM_ERR_INTERNAL, IOTCOM_ERR_INVALID_ARG, IOTCOM_OK,
};
use tokio::runtime::Runtime;
use uuid::Uuid;

export_common!();

/// Status: no Bluetooth adapter, or the platform refused access.
pub const IOTCOM_ERR_BLE_UNAVAILABLE: i32 = -20;
/// Status: the peripheral id is unknown (not seen in a scan).
pub const IOTCOM_ERR_BLE_UNKNOWN_DEVICE: i32 = -21;
/// Status: the characteristic is not on the connected peripheral.
pub const IOTCOM_ERR_BLE_UNKNOWN_CHARACTERISTIC: i32 = -22;
/// Status: the operation timed out.
pub const IOTCOM_ERR_BLE_TIMEOUT: i32 = -23;
/// Status: the BLE stack reported an error.
pub const IOTCOM_ERR_BLE_STACK: i32 = -24;

const MAX_QUEUE: usize = 4096;

/// Opaque central handle.
pub struct BleCentral {
    runtime: Runtime,
    adapter: Adapter,
    events: Arc<Mutex<VecDeque<String>>>,
}

fn central<'a>(ptr: *mut BleCentral) -> Result<&'a BleCentral, FfiError> {
    // SAFETY: the .NET SafeHandle guarantees the pointer came from iotcom_ble_new and is not freed.
    unsafe { ptr.as_ref() }.ok_or_else(|| FfiError::null("central"))
}

fn text<'a>(ptr: *const u8, len: usize, what: &str) -> Result<&'a str, FfiError> {
    if ptr.is_null() {
        return Err(FfiError::null(what));
    }
    // SAFETY: caller guarantees `ptr` is valid for `len` bytes.
    let bytes = unsafe { slice::from_raw_parts(ptr, len) };
    std::str::from_utf8(bytes).map_err(|_| FfiError::new(IOTCOM_ERR_INVALID_ARG, format!("{what} is not UTF-8")))
}

fn stack(e: btleplug::Error) -> FfiError {
    FfiError::new(IOTCOM_ERR_BLE_STACK, format!("BLE: {e}"))
}

fn uuid(s: &str) -> Result<Uuid, FfiError> {
    parse_uuid(s).ok_or_else(|| FfiError::new(IOTCOM_ERR_INVALID_ARG, format!("'{s}' is not a UUID (16-bit like 2a37, or 128-bit)")))
}

/// Parses a 128-bit UUID or a 16/32-bit Bluetooth SIG short form (expanded on the base UUID).
pub fn parse_uuid(s: &str) -> Option<Uuid> {
    let t = s.trim().trim_start_matches("0x");
    if t.len() <= 8 && !t.is_empty() {
        let short = u32::from_str_radix(t, 16).ok()?;
        return Some(Uuid::from_u128(((short as u128) << 96) | 0x0000_0000_0000_1000_8000_0080_5F9B_34FB));
    }
    Uuid::parse_str(t).ok()
}

/// Escapes a string for JSON.
pub fn json_string(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    out.push('"');
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => {
                let _ = write!(out, "\\u{:04x}", c as u32);
            }
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

/// Lower-case hex without separators.
pub fn hex(bytes: &[u8]) -> String {
    let mut s = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        let _ = write!(s, "{b:02x}");
    }
    s
}

fn push(events: &Mutex<VecDeque<String>>, event: String) {
    let mut q = events.lock().unwrap_or_else(|p| p.into_inner());
    if q.len() >= MAX_QUEUE {
        q.pop_front();
    }
    q.push_back(event);
}

async fn advertisement_json(adapter: &Adapter, id: &btleplug::platform::PeripheralId) -> Option<String> {
    let p = adapter.peripheral(id).await.ok()?;
    let props = p.properties().await.ok()??;
    let mut s = String::from("{\"type\":\"advertisement\",\"id\":");
    s.push_str(&json_string(&id.to_string()));
    let _ = write!(s, ",\"address\":{}", json_string(&props.address.to_string()));
    if let Some(name) = &props.local_name {
        let _ = write!(s, ",\"name\":{}", json_string(name));
    }
    if let Some(rssi) = props.rssi {
        let _ = write!(s, ",\"rssi\":{rssi}");
    }
    if let Some(tx) = props.tx_power_level {
        let _ = write!(s, ",\"tx_power\":{tx}");
    }
    s.push_str(",\"services\":[");
    for (i, u) in props.services.iter().enumerate() {
        if i > 0 {
            s.push(',');
        }
        s.push_str(&json_string(&u.to_string()));
    }
    s.push_str("],\"manufacturer\":{");
    for (i, (company, data)) in props.manufacturer_data.iter().enumerate() {
        if i > 0 {
            s.push(',');
        }
        let _ = write!(s, "\"{company}\":\"{}\"", hex(data));
    }
    s.push_str("},\"service_data\":{");
    for (i, (u, data)) in props.service_data.iter().enumerate() {
        if i > 0 {
            s.push(',');
        }
        let _ = write!(s, "{}:\"{}\"", json_string(&u.to_string()), hex(data));
    }
    s.push_str("}}");
    Some(s)
}

/// Creates a central on the first Bluetooth adapter and starts listening for adapter events.
///
/// # Safety
/// `out` must be valid for one pointer write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_ble_new(out: *mut *mut BleCentral) -> i32 {
    ffi_guard(|| {
        if out.is_null() {
            return Err(FfiError::null("out"));
        }
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .enable_all()
            .thread_name("iotcom-ble")
            .build()
            .map_err(|e| FfiError::new(IOTCOM_ERR_INTERNAL, format!("runtime: {e}")))?;
        let adapter = runtime.block_on(async {
            let manager = Manager::new().await.map_err(|e| FfiError::new(IOTCOM_ERR_BLE_UNAVAILABLE, format!("Bluetooth is not available: {e}")))?;
            let adapters = manager.adapters().await.map_err(|e| FfiError::new(IOTCOM_ERR_BLE_UNAVAILABLE, format!("Bluetooth is not available: {e}")))?;
            adapters.into_iter().next().ok_or_else(|| FfiError::new(IOTCOM_ERR_BLE_UNAVAILABLE, "no Bluetooth adapter found"))
        })?;
        let events = Arc::new(Mutex::new(VecDeque::new()));
        let (a, q) = (adapter.clone(), events.clone());
        runtime.spawn(async move {
            let Ok(mut stream) = a.events().await else { return };
            while let Some(event) = stream.next().await {
                match event {
                    CentralEvent::DeviceDiscovered(id)
                    | CentralEvent::DeviceUpdated(id)
                    | CentralEvent::ManufacturerDataAdvertisement { id, .. }
                    | CentralEvent::ServiceDataAdvertisement { id, .. }
                    | CentralEvent::ServicesAdvertisement { id, .. } => {
                        // Properties can be missing for a moment after discovery: report the id anyway.
                        let json = advertisement_json(&a, &id).await.unwrap_or_else(|| format!(
                            "{{\"type\":\"advertisement\",\"id\":{},\"services\":[],\"manufacturer\":{{}},\"service_data\":{{}}}}",
                            json_string(&id.to_string())));
                        push(&q, json);
                    }
                    CentralEvent::DeviceConnected(id) => push(&q, format!("{{\"type\":\"connected\",\"id\":{}}}", json_string(&id.to_string()))),
                    CentralEvent::DeviceDisconnected(id) => push(&q, format!("{{\"type\":\"disconnected\",\"id\":{}}}", json_string(&id.to_string()))),
                    _ => {}
                }
            }
        });
        let handle = Box::new(BleCentral { runtime, adapter, events });
        // SAFETY: checked non-null above.
        unsafe { *out = Box::into_raw(handle) };
        Ok(IOTCOM_OK)
    })
}

/// Releases a central (stops its runtime).
///
/// # Safety
/// `ptr` must come from [`iotcom_ble_new`] and must not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn iotcom_ble_free(ptr: *mut BleCentral) {
    if !ptr.is_null() {
        let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            // SAFETY: ownership returns to Rust exactly once.
            let central = unsafe { Box::from_raw(ptr) };
            central.runtime.shutdown_timeout(Duration::from_millis(500));
        }));
    }
}

/// Writes the adapter description (UTF-8) into `buf`; `written` receives the full length.
///
/// # Safety
/// `buf` valid for `len` bytes; `written` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_ble_adapter_info(ptr: *mut BleCentral, buf: *mut u8, len: usize, written: *mut usize) -> i32 {
    ffi_guard(|| {
        let c = central(ptr)?;
        let info = c.runtime.block_on(c.adapter.adapter_info()).map_err(stack)?;
        // SAFETY: forwarded caller contract.
        unsafe { copy_out(info.as_bytes(), buf, len, written) }
    })
}

/// Starts scanning. `services` is a comma-separated UUID filter (empty = all devices).
///
/// # Safety
/// `services` valid for `services_len` bytes (may be null when the length is 0).
#[no_mangle]
pub unsafe extern "C" fn iotcom_ble_start_scan(ptr: *mut BleCentral, services: *const u8, services_len: usize) -> i32 {
    ffi_guard(|| {
        let c = central(ptr)?;
        let filter = if services_len == 0 {
            ScanFilter::default()
        } else {
            let list = text(services, services_len, "services")?;
            ScanFilter { services: list.split(',').filter(|s| !s.trim().is_empty()).map(uuid).collect::<Result<_, _>>()? }
        };
        c.runtime.block_on(c.adapter.start_scan(filter)).map_err(stack)?;
        Ok(IOTCOM_OK)
    })
}

/// Stops scanning.
///
/// # Safety
/// `ptr` must be a live handle.
#[no_mangle]
pub unsafe extern "C" fn iotcom_ble_stop_scan(ptr: *mut BleCentral) -> i32 {
    ffi_guard(|| {
        let c = central(ptr)?;
        c.runtime.block_on(c.adapter.stop_scan()).map_err(stack)?;
        Ok(IOTCOM_OK)
    })
}

/// Copies the oldest queued event (one JSON object) into `buf`. Returns 1 when an event was copied,
/// 0 when the queue is empty, or `IOTCOM_ERR_BUFFER_TOO_SMALL` with the required size in `written`
/// (the event stays queued).
///
/// # Safety
/// `buf` valid for `len` bytes; `written` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_ble_poll_event(ptr: *mut BleCentral, buf: *mut u8, len: usize, written: *mut usize) -> i32 {
    ffi_guard(|| {
        let c = central(ptr)?;
        if written.is_null() {
            return Err(FfiError::null("written"));
        }
        let mut q = c.events.lock().unwrap_or_else(|p| p.into_inner());
        let Some(event) = q.front() else { return Ok(0) };
        let bytes = event.as_bytes();
        // SAFETY: checked non-null above.
        unsafe { *written = bytes.len() };
        if bytes.len() > len || buf.is_null() {
            return Err(FfiError::new(IOTCOM_ERR_BUFFER_TOO_SMALL, format!("event needs {} bytes", bytes.len())));
        }
        // SAFETY: buf valid for len >= bytes.len().
        unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), buf, bytes.len()) };
        q.pop_front();
        Ok(1)
    })
}

/// # Safety
/// `buf` valid for `len` bytes (or null with 0); `written` valid for one write.
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

async fn find(adapter: &Adapter, id: &str) -> Result<Peripheral, FfiError> {
    let all = adapter.peripherals().await.map_err(stack)?;
    all.into_iter()
        .find(|p| p.id().to_string().eq_ignore_ascii_case(id) || p.address().to_string().eq_ignore_ascii_case(id))
        .ok_or_else(|| FfiError::new(IOTCOM_ERR_BLE_UNKNOWN_DEVICE, format!("device {id} has not been seen in a scan")))
}

fn characteristic(p: &Peripheral, id: &str) -> Result<Characteristic, FfiError> {
    let u = uuid(id)?;
    p.characteristics()
        .into_iter()
        .find(|c| c.uuid == u)
        .ok_or_else(|| FfiError::new(IOTCOM_ERR_BLE_UNKNOWN_CHARACTERISTIC, format!("characteristic {u} not found (connect and discover services first)")))
}

fn timeout(ms: u32) -> Duration {
    Duration::from_millis(if ms == 0 { 10_000 } else { u64::from(ms) })
}

async fn within<T>(ms: u32, what: &str, f: impl std::future::Future<Output = Result<T, FfiError>>) -> Result<T, FfiError> {
    tokio::time::timeout(timeout(ms), f)
        .await
        .map_err(|_| FfiError::new(IOTCOM_ERR_BLE_TIMEOUT, format!("{what} timed out after {} ms", timeout(ms).as_millis())))?
}

/// Connects to a peripheral seen in a scan, discovers its services and starts forwarding its notifications.
///
/// # Safety
/// `id` valid for `id_len` bytes.
#[no_mangle]
pub unsafe extern "C" fn iotcom_ble_connect(ptr: *mut BleCentral, id: *const u8, id_len: usize, timeout_ms: u32) -> i32 {
    ffi_guard(|| {
        let c = central(ptr)?;
        let id = text(id, id_len, "id")?.to_string();
        let q = c.events.clone();
        c.runtime.block_on(within(timeout_ms, "connect", async {
            let p = find(&c.adapter, &id).await?;
            if !p.is_connected().await.map_err(stack)? {
                p.connect().await.map_err(stack)?;
            }
            p.discover_services().await.map_err(stack)?;
            let mut notifications = p.notifications().await.map_err(stack)?;
            let peripheral_id = id.clone();
            tokio::spawn(async move {
                while let Some(n) = notifications.next().await {
                    push(&q, format!(
                        "{{\"type\":\"notification\",\"id\":{},\"uuid\":{},\"value\":\"{}\"}}",
                        json_string(&peripheral_id), json_string(&n.uuid.to_string()), hex(&n.value)));
                }
            });
            Ok(())
        }))?;
        Ok(IOTCOM_OK)
    })
}

/// Disconnects a peripheral.
///
/// # Safety
/// `id` valid for `id_len` bytes.
#[no_mangle]
pub unsafe extern "C" fn iotcom_ble_disconnect(ptr: *mut BleCentral, id: *const u8, id_len: usize) -> i32 {
    ffi_guard(|| {
        let c = central(ptr)?;
        let id = text(id, id_len, "id")?;
        c.runtime.block_on(within(5000, "disconnect", async {
            let p = find(&c.adapter, id).await?;
            p.disconnect().await.map_err(stack)
        }))?;
        Ok(IOTCOM_OK)
    })
}

/// Writes the services of a connected peripheral as JSON:
/// `[{"uuid":"…","primary":true,"characteristics":[{"uuid":"…","properties":["read","notify"]}]}]`.
///
/// # Safety
/// `id` valid for `id_len` bytes; `buf` valid for `len` bytes; `written` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_ble_services(ptr: *mut BleCentral, id: *const u8, id_len: usize, buf: *mut u8, len: usize, written: *mut usize) -> i32 {
    ffi_guard(|| {
        let c = central(ptr)?;
        let id = text(id, id_len, "id")?;
        let p = c.runtime.block_on(find(&c.adapter, id))?;
        let mut s = String::from("[");
        for (i, service) in p.services().iter().enumerate() {
            if i > 0 {
                s.push(',');
            }
            let _ = write!(s, "{{\"uuid\":{},\"primary\":{},\"characteristics\":[", json_string(&service.uuid.to_string()), service.primary);
            for (j, ch) in service.characteristics.iter().enumerate() {
                if j > 0 {
                    s.push(',');
                }
                let _ = write!(s, "{{\"uuid\":{},\"properties\":[{}]}}", json_string(&ch.uuid.to_string()), properties(ch.properties));
            }
            s.push_str("]}");
        }
        s.push(']');
        // SAFETY: forwarded caller contract.
        unsafe { copy_out(s.as_bytes(), buf, len, written) }
    })
}

/// Property flags as a JSON list body.
pub fn properties(flags: CharPropFlags) -> String {
    let names = [
        (CharPropFlags::BROADCAST, "broadcast"),
        (CharPropFlags::READ, "read"),
        (CharPropFlags::WRITE_WITHOUT_RESPONSE, "write_without_response"),
        (CharPropFlags::WRITE, "write"),
        (CharPropFlags::NOTIFY, "notify"),
        (CharPropFlags::INDICATE, "indicate"),
        (CharPropFlags::AUTHENTICATED_SIGNED_WRITES, "signed_write"),
        (CharPropFlags::EXTENDED_PROPERTIES, "extended"),
    ];
    names.iter().filter(|(f, _)| flags.contains(*f)).map(|(_, n)| format!("\"{n}\"")).collect::<Vec<_>>().join(",")
}

/// Reads a characteristic of a connected peripheral into `buf`.
///
/// # Safety
/// `id`/`ch` valid for their lengths; `buf` valid for `len` bytes; `written` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_ble_read(
    ptr: *mut BleCentral, id: *const u8, id_len: usize, ch: *const u8, ch_len: usize, timeout_ms: u32, buf: *mut u8, len: usize, written: *mut usize,
) -> i32 {
    ffi_guard(|| {
        let c = central(ptr)?;
        let (id, ch) = (text(id, id_len, "id")?, text(ch, ch_len, "characteristic")?);
        let value = c.runtime.block_on(within(timeout_ms, "read", async {
            let p = find(&c.adapter, id).await?;
            let characteristic = characteristic(&p, ch)?;
            p.read(&characteristic).await.map_err(stack)
        }))?;
        // SAFETY: forwarded caller contract.
        unsafe { copy_out(&value, buf, len, written) }
    })
}

/// Writes a characteristic (`with_response` != 0 waits for the write response).
///
/// # Safety
/// `id`/`ch` valid for their lengths; `data` valid for `data_len` bytes.
#[no_mangle]
pub unsafe extern "C" fn iotcom_ble_write(
    ptr: *mut BleCentral, id: *const u8, id_len: usize, ch: *const u8, ch_len: usize, data: *const u8, data_len: usize, with_response: u8, timeout_ms: u32,
) -> i32 {
    ffi_guard(|| {
        let c = central(ptr)?;
        let (id, ch) = (text(id, id_len, "id")?, text(ch, ch_len, "characteristic")?);
        let value = if data_len == 0 {
            Vec::new()
        } else if data.is_null() {
            return Err(FfiError::null("data"));
        } else {
            // SAFETY: caller guarantees data is valid for data_len bytes.
            unsafe { slice::from_raw_parts(data, data_len) }.to_vec()
        };
        let kind = if with_response != 0 { WriteType::WithResponse } else { WriteType::WithoutResponse };
        c.runtime.block_on(within(timeout_ms, "write", async {
            let p = find(&c.adapter, id).await?;
            let characteristic = characteristic(&p, ch)?;
            p.write(&characteristic, &value, kind).await.map_err(stack)
        }))?;
        Ok(IOTCOM_OK)
    })
}

/// Enables (`enable` != 0) or disables notifications or indications of a characteristic.
///
/// # Safety
/// `id`/`ch` valid for their lengths.
#[no_mangle]
pub unsafe extern "C" fn iotcom_ble_subscribe(ptr: *mut BleCentral, id: *const u8, id_len: usize, ch: *const u8, ch_len: usize, enable: u8) -> i32 {
    ffi_guard(|| {
        let c = central(ptr)?;
        let (id, ch) = (text(id, id_len, "id")?, text(ch, ch_len, "characteristic")?);
        c.runtime.block_on(within(10_000, "subscribe", async {
            let p = find(&c.adapter, id).await?;
            let characteristic = characteristic(&p, ch)?;
            if enable != 0 {
                p.subscribe(&characteristic).await.map_err(stack)
            } else {
                p.unsubscribe(&characteristic).await.map_err(stack)
            }
        }))?;
        Ok(IOTCOM_OK)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn short_uuids_expand_on_the_bluetooth_base() {
        assert_eq!(parse_uuid("2a37").unwrap().to_string(), "00002a37-0000-1000-8000-00805f9b34fb");
        assert_eq!(parse_uuid("0x180D").unwrap().to_string(), "0000180d-0000-1000-8000-00805f9b34fb");
        assert_eq!(parse_uuid("6e400001-b5a3-f393-e0a9-e50e24dcca9e").unwrap().to_string(), "6e400001-b5a3-f393-e0a9-e50e24dcca9e");
        assert!(parse_uuid("zz").is_none());
        assert!(parse_uuid("").is_none());
    }

    #[test]
    fn json_and_hex_helpers() {
        assert_eq!(json_string("a\"b\\c\n\u{1}"), "\"a\\\"b\\\\c\\n\\u0001\"");
        assert_eq!(hex(&[0x00, 0xab, 0x10]), "00ab10");
        assert_eq!(properties(CharPropFlags::READ | CharPropFlags::NOTIFY), "\"read\",\"notify\"");
    }

    #[test]
    fn null_handles_are_reported_not_dereferenced() {
        let mut written = 0usize;
        let code = unsafe { iotcom_ble_poll_event(std::ptr::null_mut(), std::ptr::null_mut(), 0, &mut written) };
        assert_eq!(code, iotcom_ffi_support::IOTCOM_ERR_NULL);
        assert_eq!(unsafe { iotcom_ble_new(std::ptr::null_mut()) }, iotcom_ffi_support::IOTCOM_ERR_NULL);
    }
}
