//! M-Bus codecs: frames and records never panic; accepted frames re-encode to the exact bytes consumed.
#![no_main]
use iotcom_mbus::{parse_records, read_frame, Frame};
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    if let Ok((frame, n)) = read_frame(data) {
        assert_eq!(frame.encode(), &data[..n]);
        if let Frame::Long { data: user, .. } = frame {
            if user.len() >= 12 {
                let _ = parse_records(&user[12..]);
            }
        }
    }
    let _ = parse_records(data);
});
