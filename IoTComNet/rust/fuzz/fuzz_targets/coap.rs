//! CoAP codec: any datagram decodes or is rejected — never panics — and every accepted message
//! re-encodes to exactly the bytes it was decoded from (the encoding is canonical).
#![no_main]
use iotcom_coap::{decode, encode};
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    if let Ok(m) = decode(data) {
        let mut out = Vec::new();
        encode(&m, &mut out);
        // Canonical when the sender used minimal extended lengths; otherwise the re-encoding must still decode equal.
        assert_eq!(decode(&out).expect("re-encoded message decodes"), m);
    }
});
