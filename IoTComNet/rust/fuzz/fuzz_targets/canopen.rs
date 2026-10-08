//! CANopen codecs: SDO decoding never panics, and what decodes re-encodes to a frame with the same meaning.
#![no_main]
use iotcom_canopen::{classify, decode_sdo, encode_sdo, Emergency};
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    for from_server in [false, true] {
        if let Ok(f) = decode_sdo(data, from_server) {
            let again = decode_sdo(&encode_sdo(&f), from_server).expect("re-encoded SDO decodes");
            assert_eq!((again.kind, again.index, again.sub, &again.data, again.toggle, again.last, again.abort), (f.kind, f.index, f.sub, &f.data, f.toggle, f.last, f.abort));
        }
    }
    if let Some(e) = Emergency::decode(data) {
        assert_eq!(Emergency::decode(&e.encode()), Some(e));
    }
    if data.len() >= 2 {
        let _ = classify(u32::from(u16::from_le_bytes([data[0], data[1]])));
    }
});
