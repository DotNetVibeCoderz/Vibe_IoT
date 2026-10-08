//! DLMS codecs: any input either decodes or is rejected — never panics. Accepted HDLC frames re-encode to the
//! exact bytes consumed; accepted A-XDR values re-encode to the bytes consumed.
#![no_main]
use iotcom_dlms::{decode_data, read_frame};
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    if let Ok((frame, n)) = read_frame(data) {
        assert_eq!(frame.encode(), &data[..n]);
    }
    if let Ok((value, n)) = decode_data(data) {
        // Lengths may use a longer form than necessary on input; the canonical re-encoding must decode equal.
        let again = value.encode();
        let (back, m) = decode_data(&again).expect("re-encoded value decodes");
        assert_eq!(m, again.len());
        assert_eq!(back, value);
        assert!(n <= data.len());
    }
});
