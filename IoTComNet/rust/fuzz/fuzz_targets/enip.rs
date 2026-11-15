//! EtherNet/IP decoding never panics, and whatever decodes survives a round trip.
#![no_main]
use iotcom_enip::{decode_cpf, encode_cpf, Encap, Identity, Path, Request, Response};
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    if let Some(e) = Encap::decode(data) {
        assert_eq!(e.encode(), data);
        let _ = e.describe();
    }
    if let Some(items) = decode_cpf(data) {
        assert_eq!(encode_cpf(&items), data);
    }
    if let Some(r) = Request::decode(data) {
        let _ = r.describe();
        // Paths round-trip when their segments were minimal and in canonical order.
        let again = Request::decode(&r.encode()).expect("re-decode");
        assert_eq!(again, r);
    }
    if let Some(r) = Response::decode(data) {
        assert_eq!(r.encode(), data);
        let _ = r.describe();
    }
    if let Some(p) = Path::decode(data) {
        let _ = p.describe();
    }
    if let Some(i) = Identity::decode(data) {
        let _ = i.describe();
    }
});
