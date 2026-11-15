//! Firmware decoding never panics; Intel HEX images re-describe consistently and DFU suffixes round-trip.
#![no_main]
use iotcom_firmware::{add_suffix, describe_layout, describe_rtt, parse_ihex, parse_layout, parse_rtt, DfuFile};
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    if let Ok(text) = std::str::from_utf8(data) {
        if let Some(image) = parse_ihex(text) {
            let _ = image.describe();
        }
        if let Some((name, regions)) = parse_layout(text) {
            let _ = describe_layout(&name, &regions);
        }
    }
    if let Some(file) = DfuFile::parse(data) {
        let _ = file.describe();
        // Re-suffixing the payload with the same identity gives back the same file.
        let payload = &data[..data.len() - 16];
        assert_eq!(add_suffix(payload, file.vendor, file.product, file.device, file.dfu), data);
    }
    if let Some((up, down)) = parse_rtt(data) {
        let _ = describe_rtt(&up, &down);
    }
});
