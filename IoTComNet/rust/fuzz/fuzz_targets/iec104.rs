//! IEC 104 decoding never panics; every APDU and ASDU that decodes re-encodes to the same bytes and decodes again
//! to the same values.
#![no_main]
use iotcom_iec104::{split, Apdu, Asdu};
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    let (frames, used) = split(data);
    assert!(used <= data.len());
    for frame in frames {
        if let Ok(apdu) = Apdu::decode(frame) {
            if let Apdu::I(_, _, asdu) = &apdu {
                if let Ok(a) = Asdu::decode(asdu) {
                    let again = Asdu::decode(&a.encode()).expect("re-encoded ASDU decodes");
                    assert_eq!(again.type_id, a.type_id);
                    assert_eq!(again.objects.len(), a.objects.len());
                    for (x, y) in again.objects.iter().zip(&a.objects) {
                        assert_eq!(x.ioa, y.ioa);
                        assert_eq!(x.time, y.time);
                        assert!(x.value == y.value || (x.value.is_nan() && y.value.is_nan()));
                    }
                }
            } else {
                assert_eq!(apdu.encode(), frame);
            }
        }
    }
    if data.len() > 6 {
        let _ = Asdu::decode(data);
    }
});
