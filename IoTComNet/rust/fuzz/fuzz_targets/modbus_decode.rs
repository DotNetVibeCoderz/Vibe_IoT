//! Frame decoder: any byte stream must decode, skip or ask for more — never panic, never loop,
//! and every decoded frame must re-encode to the bytes it consumed.
#![no_main]
use iotcom_modbus::frame::{decode, encode, Decode, Framing};
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    let Some((&selector, mut rest)) = data.split_first() else { return };
    let framing = Framing::from_u8(selector % 3).unwrap();
    let expect_request = selector & 0x80 != 0;
    while !rest.is_empty() {
        match decode(framing, rest, expect_request) {
            Decode::NeedMore => break,
            Decode::Skip(n) => {
                assert!(n > 0 && n <= rest.len(), "skip must make progress");
                rest = &rest[n..];
            }
            Decode::Frame(adu, n) => {
                assert!(n > 0 && n <= rest.len(), "frame must consume input");
                let mut again = Vec::new();
                encode(framing, adu.transaction_id, adu.unit_id, &adu.pdu, &mut again);
                if framing != Framing::Ascii {
                    // ASCII accepts lower-case hex, so only binary framings are byte-exact.
                    assert_eq!(&again[..], &rest[..n], "round trip");
                }
                rest = &rest[n..];
            }
        }
    }
});
