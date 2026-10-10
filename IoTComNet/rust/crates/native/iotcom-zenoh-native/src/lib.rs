//! C ABI for an Eclipse Zenoh session (`iotcom_zenoh` native library): put / delete, subscribers,
//! queryables and `get`, built on the `zenoh` crate (TCP and UDP transports only).
//!
//! Contract (see `iotcom-ffi-support`): every function returns an `i32` status (`IOTCOM_OK` = 0,
//! negative = error, details via `iotcom_last_error`) and never unwinds. A `ZenohSession` handle owns a
//! Tokio runtime and the zenoh session; calls block until the operation completes. Asynchronous
//! happenings (samples, incoming queries, `get` replies) are queued as one-line JSON events and drained
//! with [`iotcom_zenoh_poll_event`]; payloads travel as base64. Strings cross the boundary as UTF-8
//! pointer + length, binary payloads as pointer + length.
//!
//! Events:
//! * `{"type":"sample","sub":1,"key":"a/b","kind":"put"|"delete","payload":"<b64>","encoding":"..."}`
//! * `{"type":"query","queryable":2,"query":7,"key":"a/b","parameters":"x=1","payload":"<b64>"}`
//! * `{"type":"reply","get":3,"key":"a/b","kind":"put"|"delete","payload":"<b64>","encoding":"..."}`
//! * `{"type":"reply_error","get":3,"payload":"<b64>"}`
//! * `{"type":"get_done","get":3}`

use std::collections::{HashMap, VecDeque};
use std::fmt::Write as _;
use std::slice;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use iotcom_ffi_support::{
    export_common, ffi_guard, FfiError, IOTCOM_ERR_BUFFER_TOO_SMALL, IOTCOM_ERR_INTERNAL, IOTCOM_ERR_INVALID_ARG, IOTCOM_OK,
};
use tokio::runtime::Runtime;
use zenoh::bytes::Encoding;
use zenoh::query::{Query, Queryable};
use zenoh::sample::{Sample, SampleKind};
use zenoh::pubsub::Subscriber;
use zenoh::{Config, Session, Wait};

export_common!();

/// Status: the session could not be opened (bad configuration or endpoints).
pub const IOTCOM_ERR_ZENOH_OPEN: i32 = -20;
/// Status: zenoh reported an error.
pub const IOTCOM_ERR_ZENOH: i32 = -21;
/// Status: the subscriber, queryable or query id is unknown (already undeclared or finished).
pub const IOTCOM_ERR_ZENOH_UNKNOWN_ID: i32 = -22;
/// Status: the session was closed.
pub const IOTCOM_ERR_ZENOH_CLOSED: i32 = -23;

const MAX_QUEUE: usize = 4096;

/// Opaque session handle.
pub struct ZenohSession {
    runtime: Runtime,
    session: Mutex<Option<Session>>,
    events: Arc<Mutex<VecDeque<String>>>,
    subscribers: Mutex<HashMap<u64, Subscriber<()>>>,
    queryables: Mutex<HashMap<u64, Queryable<()>>>,
    queries: Arc<Mutex<HashMap<u64, Query>>>,
    next_id: Arc<AtomicU64>,
}

fn handle<'a>(ptr: *mut ZenohSession) -> Result<&'a ZenohSession, FfiError> {
    // SAFETY: the .NET SafeHandle guarantees the pointer came from iotcom_zenoh_open and is not freed.
    unsafe { ptr.as_ref() }.ok_or_else(|| FfiError::null("session"))
}

fn text<'a>(ptr: *const u8, len: usize, what: &str) -> Result<&'a str, FfiError> {
    if len == 0 {
        return Ok("");
    }
    if ptr.is_null() {
        return Err(FfiError::null(what));
    }
    // SAFETY: caller guarantees `ptr` is valid for `len` bytes.
    let bytes = unsafe { slice::from_raw_parts(ptr, len) };
    std::str::from_utf8(bytes).map_err(|_| FfiError::new(IOTCOM_ERR_INVALID_ARG, format!("{what} is not UTF-8")))
}

fn bytes(ptr: *const u8, len: usize, what: &str) -> Result<Vec<u8>, FfiError> {
    if len == 0 {
        return Ok(Vec::new());
    }
    if ptr.is_null() {
        return Err(FfiError::null(what));
    }
    // SAFETY: caller guarantees `ptr` is valid for `len` bytes.
    Ok(unsafe { slice::from_raw_parts(ptr, len) }.to_vec())
}

fn zerr(e: impl std::fmt::Display) -> FfiError {
    FfiError::new(IOTCOM_ERR_ZENOH, format!("zenoh: {e}"))
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

/// Standard base64 (with padding).
pub fn base64(data: &[u8]) -> String {
    const ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity(data.len().div_ceil(3) * 4);
    for chunk in data.chunks(3) {
        let n = (u32::from(chunk[0]) << 16) | (u32::from(*chunk.get(1).unwrap_or(&0)) << 8) | u32::from(*chunk.get(2).unwrap_or(&0));
        out.push(ALPHABET[(n >> 18) as usize & 63] as char);
        out.push(ALPHABET[(n >> 12) as usize & 63] as char);
        out.push(if chunk.len() > 1 { ALPHABET[(n >> 6) as usize & 63] as char } else { '=' });
        out.push(if chunk.len() > 2 { ALPHABET[n as usize & 63] as char } else { '=' });
    }
    out
}

fn push(events: &Mutex<VecDeque<String>>, event: String) {
    let mut q = events.lock().unwrap_or_else(|p| p.into_inner());
    if q.len() >= MAX_QUEUE {
        q.pop_front();
    }
    q.push_back(event);
}

fn lock<T>(m: &Mutex<T>) -> std::sync::MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|p| p.into_inner())
}

fn kind_name(kind: SampleKind) -> &'static str {
    match kind {
        SampleKind::Put => "put",
        SampleKind::Delete => "delete",
    }
}

fn sample_fields(sample: &Sample) -> String {
    format!(
        "\"key\":{},\"kind\":\"{}\",\"payload\":\"{}\",\"encoding\":{}",
        json_string(sample.key_expr().as_str()),
        kind_name(sample.kind()),
        base64(&sample.payload().to_bytes()),
        json_string(&sample.encoding().to_string())
    )
}

fn endpoint_list(list: &str) -> String {
    let items: Vec<String> = list.split(',').map(str::trim).filter(|s| !s.is_empty()).map(json_string).collect();
    format!("[{}]", items.join(","))
}

fn build_config(mode: &str, connect: &str, listen: &str, multicast: bool) -> Result<Config, FfiError> {
    let mode = if mode.is_empty() { "peer" } else { mode };
    if mode != "peer" && mode != "client" {
        return Err(FfiError::new(IOTCOM_ERR_INVALID_ARG, format!("mode must be 'peer' or 'client', not '{mode}'")));
    }
    let mut config = Config::default();
    let mut set = |key: &str, value: String| {
        config.insert_json5(key, &value).map_err(|e| FfiError::new(IOTCOM_ERR_ZENOH_OPEN, format!("config {key}: {e}")))
    };
    set("mode", json_string(mode))?;
    set("scouting/multicast/enabled", multicast.to_string())?;
    if !connect.trim().is_empty() {
        set("connect/endpoints", endpoint_list(connect))?;
    }
    if !listen.trim().is_empty() {
        set("listen/endpoints", endpoint_list(listen))?;
    }
    Ok(config)
}

fn session_of(z: &ZenohSession) -> Result<Session, FfiError> {
    lock(&z.session).clone().ok_or_else(|| FfiError::new(IOTCOM_ERR_ZENOH_CLOSED, "the session is closed"))
}

/// Opens a session. `mode` is `peer` or `client` (empty = peer); `connect` and `listen` are
/// comma-separated endpoint lists such as `tcp/127.0.0.1:7447`; `multicast` != 0 enables multicast scouting.
///
/// # Safety
/// Each string valid for its length (null allowed when the length is 0); `out` valid for one pointer write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_zenoh_open(
    mode: *const u8, mode_len: usize, connect: *const u8, connect_len: usize, listen: *const u8, listen_len: usize, multicast: u8,
    out: *mut *mut ZenohSession,
) -> i32 {
    ffi_guard(|| {
        if out.is_null() {
            return Err(FfiError::null("out"));
        }
        let config = build_config(text(mode, mode_len, "mode")?, text(connect, connect_len, "connect")?, text(listen, listen_len, "listen")?, multicast != 0)?;
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .enable_all()
            .thread_name("iotcom-zenoh")
            .build()
            .map_err(|e| FfiError::new(IOTCOM_ERR_INTERNAL, format!("runtime: {e}")))?;
        let session = runtime
            .block_on(async { zenoh::open(config).await })
            .map_err(|e| FfiError::new(IOTCOM_ERR_ZENOH_OPEN, format!("cannot open the zenoh session: {e}")))?;
        let z = Box::new(ZenohSession {
            runtime,
            session: Mutex::new(Some(session)),
            events: Arc::new(Mutex::new(VecDeque::new())),
            subscribers: Mutex::new(HashMap::new()),
            queryables: Mutex::new(HashMap::new()),
            queries: Arc::new(Mutex::new(HashMap::new())),
            next_id: Arc::new(AtomicU64::new(1)),
        });
        // SAFETY: checked non-null above.
        unsafe { *out = Box::into_raw(z) };
        Ok(IOTCOM_OK)
    })
}

fn close_inner(z: &ZenohSession) {
    lock(&z.subscribers).clear();
    lock(&z.queryables).clear();
    lock(&z.queries).clear();
    let session = lock(&z.session).take();
    if let Some(session) = session {
        let _ = z.runtime.block_on(async { tokio::time::timeout(Duration::from_secs(5), session.close()).await });
    }
}

/// Closes the session (idempotent); the handle must still be freed with [`iotcom_zenoh_free`].
///
/// # Safety
/// `ptr` must be a live handle.
#[no_mangle]
pub unsafe extern "C" fn iotcom_zenoh_close(ptr: *mut ZenohSession) -> i32 {
    ffi_guard(|| {
        close_inner(handle(ptr)?);
        Ok(IOTCOM_OK)
    })
}

/// Closes and releases a session.
///
/// # Safety
/// `ptr` must come from [`iotcom_zenoh_open`] and must not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn iotcom_zenoh_free(ptr: *mut ZenohSession) {
    if !ptr.is_null() {
        let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            // SAFETY: ownership returns to Rust exactly once.
            let z = unsafe { Box::from_raw(ptr) };
            close_inner(&z);
            z.runtime.shutdown_timeout(Duration::from_millis(500));
        }));
    }
}

/// Writes session info as JSON (`{"zid":"…","peers":["…"],"routers":["…"]}`) into `buf`; `written` receives the full length.
///
/// # Safety
/// `buf` valid for `len` bytes; `written` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_zenoh_info(ptr: *mut ZenohSession, buf: *mut u8, len: usize, written: *mut usize) -> i32 {
    ffi_guard(|| {
        let z = handle(ptr)?;
        let session = session_of(z)?;
        let json = z.runtime.block_on(async {
            let info = session.info();
            let zid = info.zid().await;
            let peers: Vec<String> = info.peers_zid().await.map(|p| json_string(&p.to_string())).collect();
            let routers: Vec<String> = info.routers_zid().await.map(|p| json_string(&p.to_string())).collect();
            format!("{{\"zid\":{},\"peers\":[{}],\"routers\":[{}]}}", json_string(&zid.to_string()), peers.join(","), routers.join(","))
        });
        // SAFETY: forwarded caller contract.
        unsafe { copy_out(json.as_bytes(), buf, len, written) }
    })
}

/// Publishes a value on a key expression. `encoding` may be empty (= zenoh default).
///
/// # Safety
/// Each pointer valid for its length (null allowed when the length is 0).
#[no_mangle]
pub unsafe extern "C" fn iotcom_zenoh_put(
    ptr: *mut ZenohSession, key: *const u8, key_len: usize, data: *const u8, data_len: usize, encoding: *const u8, encoding_len: usize,
) -> i32 {
    ffi_guard(|| {
        let z = handle(ptr)?;
        let session = session_of(z)?;
        let key = text(key, key_len, "key")?.to_string();
        let payload = bytes(data, data_len, "data")?;
        let encoding = text(encoding, encoding_len, "encoding")?.to_string();
        z.runtime.block_on(async {
            let mut put = session.put(key, payload);
            if !encoding.is_empty() {
                put = put.encoding(Encoding::from(encoding));
            }
            put.await
        })
        .map_err(zerr)?;
        Ok(IOTCOM_OK)
    })
}

/// Deletes the value(s) of a key expression.
///
/// # Safety
/// `key` valid for `key_len` bytes.
#[no_mangle]
pub unsafe extern "C" fn iotcom_zenoh_delete(ptr: *mut ZenohSession, key: *const u8, key_len: usize) -> i32 {
    ffi_guard(|| {
        let z = handle(ptr)?;
        let session = session_of(z)?;
        let key = text(key, key_len, "key")?.to_string();
        session.delete(key).wait().map_err(zerr)?;
        Ok(IOTCOM_OK)
    })
}

/// Declares a subscriber; samples arrive as `sample` events carrying the id written to `out_id`.
///
/// # Safety
/// `key` valid for `key_len` bytes; `out_id` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_zenoh_declare_subscriber(ptr: *mut ZenohSession, key: *const u8, key_len: usize, out_id: *mut u64) -> i32 {
    ffi_guard(|| {
        let z = handle(ptr)?;
        if out_id.is_null() {
            return Err(FfiError::null("out_id"));
        }
        let session = session_of(z)?;
        let key = text(key, key_len, "key")?.to_string();
        let id = z.next_id.fetch_add(1, Ordering::Relaxed);
        let q = z.events.clone();
        let sub = session
            .declare_subscriber(key)
            .callback(move |sample| {
                push(&q, format!("{{\"type\":\"sample\",\"sub\":{id},{}}}", sample_fields(&sample)));
            })
            .wait()
            .map_err(zerr)?;
        lock(&z.subscribers).insert(id, sub);
        // SAFETY: checked non-null above.
        unsafe { *out_id = id };
        Ok(IOTCOM_OK)
    })
}

/// Undeclares a subscriber.
///
/// # Safety
/// `ptr` must be a live handle.
#[no_mangle]
pub unsafe extern "C" fn iotcom_zenoh_undeclare_subscriber(ptr: *mut ZenohSession, id: u64) -> i32 {
    ffi_guard(|| {
        let z = handle(ptr)?;
        let sub = lock(&z.subscribers).remove(&id).ok_or_else(|| FfiError::new(IOTCOM_ERR_ZENOH_UNKNOWN_ID, format!("subscriber {id} is not declared")))?;
        sub.undeclare().wait().map_err(zerr)?;
        Ok(IOTCOM_OK)
    })
}

/// Declares a queryable; incoming queries arrive as `query` events and must be answered with
/// [`iotcom_zenoh_reply`] and closed with [`iotcom_zenoh_finish_query`].
///
/// # Safety
/// `key` valid for `key_len` bytes; `out_id` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_zenoh_declare_queryable(ptr: *mut ZenohSession, key: *const u8, key_len: usize, out_id: *mut u64) -> i32 {
    ffi_guard(|| {
        let z = handle(ptr)?;
        if out_id.is_null() {
            return Err(FfiError::null("out_id"));
        }
        let session = session_of(z)?;
        let key = text(key, key_len, "key")?.to_string();
        let id = z.next_id.fetch_add(1, Ordering::Relaxed);
        let (q, queries, ids) = (z.events.clone(), z.queries.clone(), z.next_id.clone());
        let queryable = session
            .declare_queryable(key)
            .callback(move |query| {
                let query_id = ids.fetch_add(1, Ordering::Relaxed);
                let payload = query.payload().map(|p| base64(&p.to_bytes())).unwrap_or_default();
                let event = format!(
                    "{{\"type\":\"query\",\"queryable\":{id},\"query\":{query_id},\"key\":{},\"parameters\":{},\"payload\":\"{payload}\"}}",
                    json_string(query.key_expr().as_str()),
                    json_string(query.parameters().as_str())
                );
                lock(&queries).insert(query_id, query);
                push(&q, event);
            })
            .wait()
            .map_err(zerr)?;
        lock(&z.queryables).insert(id, queryable);
        // SAFETY: checked non-null above.
        unsafe { *out_id = id };
        Ok(IOTCOM_OK)
    })
}

/// Undeclares a queryable.
///
/// # Safety
/// `ptr` must be a live handle.
#[no_mangle]
pub unsafe extern "C" fn iotcom_zenoh_undeclare_queryable(ptr: *mut ZenohSession, id: u64) -> i32 {
    ffi_guard(|| {
        let z = handle(ptr)?;
        let q = lock(&z.queryables).remove(&id).ok_or_else(|| FfiError::new(IOTCOM_ERR_ZENOH_UNKNOWN_ID, format!("queryable {id} is not declared")))?;
        q.undeclare().wait().map_err(zerr)?;
        Ok(IOTCOM_OK)
    })
}

fn query_of(z: &ZenohSession, id: u64) -> Result<Query, FfiError> {
    lock(&z.queries).get(&id).cloned().ok_or_else(|| FfiError::new(IOTCOM_ERR_ZENOH_UNKNOWN_ID, format!("query {id} is unknown or already finished")))
}

/// Sends one reply to a query (the key must intersect the query's key expression). May be called
/// several times per query. `encoding` may be empty.
///
/// # Safety
/// Each pointer valid for its length (null allowed when the length is 0).
#[no_mangle]
pub unsafe extern "C" fn iotcom_zenoh_reply(
    ptr: *mut ZenohSession, query: u64, key: *const u8, key_len: usize, data: *const u8, data_len: usize, encoding: *const u8, encoding_len: usize,
) -> i32 {
    ffi_guard(|| {
        let z = handle(ptr)?;
        let q = query_of(z, query)?;
        let key = text(key, key_len, "key")?.to_string();
        let payload = bytes(data, data_len, "data")?;
        let encoding = text(encoding, encoding_len, "encoding")?.to_string();
        z.runtime
            .block_on(async {
                let mut reply = q.reply(key, payload);
                if !encoding.is_empty() {
                    reply = reply.encoding(Encoding::from(encoding));
                }
                reply.await
            })
            .map_err(zerr)?;
        Ok(IOTCOM_OK)
    })
}

/// Sends an error reply to a query.
///
/// # Safety
/// `data` valid for `data_len` bytes (null allowed when 0).
#[no_mangle]
pub unsafe extern "C" fn iotcom_zenoh_reply_err(ptr: *mut ZenohSession, query: u64, data: *const u8, data_len: usize) -> i32 {
    ffi_guard(|| {
        let z = handle(ptr)?;
        let q = query_of(z, query)?;
        let payload = bytes(data, data_len, "data")?;
        q.reply_err(payload).wait().map_err(zerr)?;
        Ok(IOTCOM_OK)
    })
}

/// Finishes a query: tells the requester that no more replies follow and releases the query id.
///
/// # Safety
/// `ptr` must be a live handle.
#[no_mangle]
pub unsafe extern "C" fn iotcom_zenoh_finish_query(ptr: *mut ZenohSession, query: u64) -> i32 {
    ffi_guard(|| {
        let z = handle(ptr)?;
        let removed = lock(&z.queries).remove(&query);
        // Dropping the last clone of the query sends the final-reply marker.
        removed.map(drop).ok_or_else(|| FfiError::new(IOTCOM_ERR_ZENOH_UNKNOWN_ID, format!("query {query} is unknown or already finished")))?;
        Ok(IOTCOM_OK)
    })
}

/// Starts a `get`. Replies arrive as `reply` / `reply_error` events and a final `get_done`, all carrying
/// the id written to `out_id`. `selector` is a key expression with optional `?parameters`;
/// `timeout_ms` = 0 uses 10 s.
///
/// # Safety
/// `selector` valid for `selector_len`; `data` valid for `data_len` (null allowed when 0); `out_id` valid for one write.
#[no_mangle]
pub unsafe extern "C" fn iotcom_zenoh_get(
    ptr: *mut ZenohSession, selector: *const u8, selector_len: usize, data: *const u8, data_len: usize, timeout_ms: u32, out_id: *mut u64,
) -> i32 {
    ffi_guard(|| {
        let z = handle(ptr)?;
        if out_id.is_null() {
            return Err(FfiError::null("out_id"));
        }
        let session = session_of(z)?;
        let selector = text(selector, selector_len, "selector")?.to_string();
        let payload = bytes(data, data_len, "data")?;
        let timeout = Duration::from_millis(if timeout_ms == 0 { 10_000 } else { u64::from(timeout_ms) });
        let id = z.next_id.fetch_add(1, Ordering::Relaxed);
        let q = z.events.clone();
        let replies = z
            .runtime
            .block_on(async {
                let mut get = session.get(selector).timeout(timeout);
                if !payload.is_empty() {
                    get = get.payload(payload);
                }
                get.await
            })
            .map_err(zerr)?;
        z.runtime.spawn(async move {
            while let Ok(reply) = replies.recv_async().await {
                match reply.result() {
                    Ok(sample) => push(&q, format!("{{\"type\":\"reply\",\"get\":{id},{}}}", sample_fields(sample))),
                    Err(e) => push(&q, format!("{{\"type\":\"reply_error\",\"get\":{id},\"payload\":\"{}\"}}", base64(&e.payload().to_bytes()))),
                }
            }
            push(&q, format!("{{\"type\":\"get_done\",\"get\":{id}}}"));
        });
        // SAFETY: checked non-null above.
        unsafe { *out_id = id };
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
pub unsafe extern "C" fn iotcom_zenoh_poll_event(ptr: *mut ZenohSession, buf: *mut u8, len: usize, written: *mut usize) -> i32 {
    ffi_guard(|| {
        let z = handle(ptr)?;
        if written.is_null() {
            return Err(FfiError::null("written"));
        }
        let mut q = lock(&z.events);
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

#[cfg(test)]
mod tests {
    use super::*;
    use std::ptr;
    use std::time::Instant;

    #[test]
    fn base64_matches_the_rfc_vectors() {
        assert_eq!(base64(b""), "");
        assert_eq!(base64(b"f"), "Zg==");
        assert_eq!(base64(b"fo"), "Zm8=");
        assert_eq!(base64(b"foo"), "Zm9v");
        assert_eq!(base64(b"foobar"), "Zm9vYmFy");
    }

    #[test]
    fn null_handles_are_reported_not_dereferenced() {
        let mut written = 0usize;
        let code = unsafe { iotcom_zenoh_poll_event(ptr::null_mut(), ptr::null_mut(), 0, &mut written) };
        assert_eq!(code, iotcom_ffi_support::IOTCOM_ERR_NULL);
        assert_eq!(unsafe { iotcom_zenoh_open(ptr::null(), 0, ptr::null(), 0, ptr::null(), 0, 0, ptr::null_mut()) }, iotcom_ffi_support::IOTCOM_ERR_NULL);
    }

    #[test]
    fn a_bad_mode_is_rejected() {
        let mut out: *mut ZenohSession = ptr::null_mut();
        let mode = b"router";
        let code = unsafe { iotcom_zenoh_open(mode.as_ptr(), mode.len(), ptr::null(), 0, ptr::null(), 0, 0, &mut out) };
        assert_eq!(code, IOTCOM_ERR_INVALID_ARG);
        assert!(out.is_null());
    }

    fn free_port() -> u16 {
        std::net::TcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port()
    }

    fn open(mode: &str, connect: &str, listen: &str) -> *mut ZenohSession {
        let mut out: *mut ZenohSession = ptr::null_mut();
        let code = unsafe { iotcom_zenoh_open(mode.as_ptr(), mode.len(), connect.as_ptr(), connect.len(), listen.as_ptr(), listen.len(), 0, &mut out) };
        assert_eq!(code, IOTCOM_OK, "open failed");
        out
    }

    fn poll(session: *mut ZenohSession, wanted: &str, timeout: Duration) -> String {
        let deadline = Instant::now() + timeout;
        let mut buf = vec![0u8; 8192];
        while Instant::now() < deadline {
            let mut written = 0usize;
            let code = unsafe { iotcom_zenoh_poll_event(session, buf.as_mut_ptr(), buf.len(), &mut written) };
            if code == 1 {
                let event = String::from_utf8(buf[..written].to_vec()).unwrap();
                if event.contains(wanted) {
                    return event;
                }
            } else {
                std::thread::sleep(Duration::from_millis(20));
            }
        }
        panic!("no event containing {wanted} within {timeout:?}");
    }

    fn put(s: *mut ZenohSession, key: &str, data: &[u8]) -> i32 {
        unsafe { iotcom_zenoh_put(s, key.as_ptr(), key.len(), data.as_ptr(), data.len(), ptr::null(), 0) }
    }

    #[test]
    fn two_sessions_exchange_put_delete_and_query() {
        let port = free_port();
        let endpoint = format!("tcp/127.0.0.1:{port}");
        let a = open("peer", "", &endpoint);
        let b = open("peer", &endpoint, "");

        // B subscribes to sensors/**; A serves queries on cmd/echo.
        let (mut sub, mut queryable) = (0u64, 0u64);
        let pattern = "sensors/**";
        assert_eq!(unsafe { iotcom_zenoh_declare_subscriber(b, pattern.as_ptr(), pattern.len(), &mut sub) }, IOTCOM_OK);
        let echo = "cmd/echo";
        assert_eq!(unsafe { iotcom_zenoh_declare_queryable(a, echo.as_ptr(), echo.len(), &mut queryable) }, IOTCOM_OK);

        // Zenoh propagates declarations asynchronously: keep putting until the subscriber sees a sample.
        let deadline = Instant::now() + Duration::from_secs(15);
        let first = loop {
            assert_eq!(put(a, "sensors/temp", b"21.5"), IOTCOM_OK);
            std::thread::sleep(Duration::from_millis(100));
            let mut buf = vec![0u8; 4096];
            let mut written = 0usize;
            if unsafe { iotcom_zenoh_poll_event(b, buf.as_mut_ptr(), buf.len(), &mut written) } == 1 {
                break String::from_utf8(buf[..written].to_vec()).unwrap();
            }
            assert!(Instant::now() < deadline, "subscriber never saw the put");
        };
        assert!(first.contains("\"type\":\"sample\""), "{first}");
        assert!(first.contains("\"key\":\"sensors/temp\""), "{first}");
        assert!(first.contains("\"kind\":\"put\""), "{first}");
        assert!(first.contains(&format!("\"payload\":\"{}\"", base64(b"21.5"))), "{first}");

        let key = "sensors/temp";
        assert_eq!(unsafe { iotcom_zenoh_delete(a, key.as_ptr(), key.len()) }, IOTCOM_OK);
        let deleted = poll(b, "\"kind\":\"delete\"", Duration::from_secs(10));
        assert!(deleted.contains("\"key\":\"sensors/temp\""), "{deleted}");

        // B queries A.
        let selector = "cmd/echo?x=1";
        let body = b"ping";
        let mut get = 0u64;
        assert_eq!(unsafe { iotcom_zenoh_get(b, selector.as_ptr(), selector.len(), body.as_ptr(), body.len(), 5000, &mut get) }, IOTCOM_OK);
        let query = poll(a, "\"type\":\"query\"", Duration::from_secs(10));
        assert!(query.contains("\"parameters\":\"x=1\""), "{query}");
        assert!(query.contains(&format!("\"payload\":\"{}\"", base64(body))), "{query}");
        let query_id: u64 = query.split("\"query\":").nth(1).unwrap().split(',').next().unwrap().parse().unwrap();
        assert_eq!(unsafe { iotcom_zenoh_reply(a, query_id, echo.as_ptr(), echo.len(), b"pong".as_ptr(), 4, ptr::null(), 0) }, IOTCOM_OK);
        assert_eq!(unsafe { iotcom_zenoh_finish_query(a, query_id) }, IOTCOM_OK);
        assert_eq!(unsafe { iotcom_zenoh_finish_query(a, query_id) }, IOTCOM_ERR_ZENOH_UNKNOWN_ID);

        let reply = poll(b, "\"type\":\"reply\"", Duration::from_secs(10));
        assert!(reply.contains(&format!("\"get\":{get}")), "{reply}");
        assert!(reply.contains(&format!("\"payload\":\"{}\"", base64(b"pong"))), "{reply}");
        let done = poll(b, "\"type\":\"get_done\"", Duration::from_secs(10));
        assert!(done.contains(&format!("\"get\":{get}")), "{done}");

        // Info reports a zid and the other peer.
        let mut buf = vec![0u8; 1024];
        let mut written = 0usize;
        assert_eq!(unsafe { iotcom_zenoh_info(a, buf.as_mut_ptr(), buf.len(), &mut written) }, IOTCOM_OK);
        let info = String::from_utf8(buf[..written].to_vec()).unwrap();
        assert!(info.starts_with("{\"zid\":\""), "{info}");

        assert_eq!(unsafe { iotcom_zenoh_undeclare_subscriber(b, sub) }, IOTCOM_OK);
        assert_eq!(unsafe { iotcom_zenoh_undeclare_subscriber(b, sub) }, IOTCOM_ERR_ZENOH_UNKNOWN_ID);
        assert_eq!(unsafe { iotcom_zenoh_undeclare_queryable(a, queryable) }, IOTCOM_OK);

        assert_eq!(unsafe { iotcom_zenoh_close(a) }, IOTCOM_OK);
        assert_eq!(put(a, "x/y", b"1"), IOTCOM_ERR_ZENOH_CLOSED);
        unsafe {
            iotcom_zenoh_free(a);
            iotcom_zenoh_free(b);
        }
    }
}
