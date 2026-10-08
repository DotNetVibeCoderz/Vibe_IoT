//! LoRaWAN PHYPayload codec: any input decodes or is rejected — never panics — and every accepted frame re-encodes
//! to exactly the bytes it was decoded from. Data frames also go through MIC verification and payload decryption, and
//! Join-Accepts through decryption, with a fixed key (the result does not matter, the absence of panics does).
#![no_main]
use iotcom_lorawan::{crypt_payload, decode, verify_data, Body, JoinAccept};
use libfuzzer_sys::fuzz_target;

const KEY: [u8; 16] = [0x2B, 0x7E, 0x15, 0x16, 0x28, 0xAE, 0xD2, 0xA6, 0xAB, 0xF7, 0x15, 0x88, 0x09, 0xCF, 0x4F, 0x3C];

fuzz_target!(|data: &[u8]| {
    if let Ok(p) = decode(data) {
        assert_eq!(p.encode(), data);
        match &p.body {
            Body::Data(d) => {
                let _ = verify_data(data, &KEY, u32::from(d.fcnt));
                let plain = crypt_payload(&KEY, p.mtype().is_uplink(), d.dev_addr, u32::from(d.fcnt), &d.frm_payload);
                assert_eq!(crypt_payload(&KEY, p.mtype().is_uplink(), d.dev_addr, u32::from(d.fcnt), &plain), d.frm_payload);
            }
            Body::Opaque(_) => {
                let _ = JoinAccept::decrypt(data, &KEY);
            }
            Body::JoinRequest { .. } => {}
        }
    }
});
