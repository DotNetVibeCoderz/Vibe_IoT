//! Matter decoding never panics; described elements stay describable.
#![no_main]
use iotcom_matter::{decode, describe, describe_message, describe_payload, parse_manual, parse_message, parse_qr};
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    if let Some(e) = decode(data) {
        let _ = describe(&e);
    }
    if let Some(m) = parse_message(data) {
        let _ = describe_message(&m);
    }
    if let Ok(text) = std::str::from_utf8(data) {
        if let Some(p) = parse_qr(text) {
            let _ = describe_payload(&p);
        }
        if let Some(p) = parse_manual(text) {
            let _ = describe_payload(&p);
        }
    }
});
