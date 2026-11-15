//! EtherCAT decoding never panics, and datagram payloads survive a round trip.
#![no_main]
use iotcom_ethercat::{decode_ethernet, decode_payload, encode_payload, Mailbox, Sdo, Sii};
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    if let Some(d) = decode_payload(data) {
        let again = encode_payload(&d).expect("a decoded payload encodes");
        assert_eq!(decode_payload(&again).as_ref(), Some(&d));
        assert_eq!(again, data[..again.len()]);
        let _ = iotcom_ethercat::describe(&d);
    }
    let _ = decode_ethernet(data);
    if let Some(m) = Mailbox::decode(data) {
        assert_eq!(Mailbox::decode(&m.encode()), Some(m.clone()));
        let _ = m.describe();
    }
    if let Some(s) = Sdo::decode(data) {
        let _ = s.describe();
    }
    if let Some(s) = Sii::parse(data) {
        let _ = s.describe();
    }
});
