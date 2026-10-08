//! J1939 codecs never panic; identifiers, NAMEs, DTCs and TP.CM messages survive a round trip.
#![no_main]
use iotcom_j1939::{decode_spns, Dtc, Id, Name, Tp};
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    if data.len() >= 4 {
        let can_id = u32::from_le_bytes([data[0], data[1], data[2], data[3]]) & 0x1FFF_FFFF;
        let id = Id::from_can_id(can_id);
        assert_eq!(id.to_can_id(), can_id);
        assert_eq!(Id::from_can_id(id.to_can_id()), id);
    }
    if let Some(n) = Name::decode(data) {
        assert_eq!(Name::decode(&n.value().to_le_bytes()), Some(n));
    }
    if let Some(d) = Dtc::decode(data) {
        assert_eq!(Dtc::decode(&d.encode()), Some(d));
    }
    if let Some(t) = Tp::decode(data) {
        assert_eq!(Tp::decode(&t.encode()), Some(t));
    }
    for pgn in [0xF004, 0xFEF1, 0xFEEE, 0xFEE5, 0xFEF7] {
        let _ = decode_spns(pgn, data);
    }
});
