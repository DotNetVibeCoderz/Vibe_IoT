//! ISO-TP channel driven by adversarial frames, sends and clock ticks.
//! Invariants: no panic, frames are valid CAN/CAN FD lengths, received messages respect max_rx_len.
#![no_main]
use iotcom_core::{Instant, Machine};
use iotcom_isotp::{IsoTpConfig, IsoTpEvent, IsoTpMachine};
use libfuzzer_sys::fuzz_target;

const LENGTHS: [usize; 8] = [8, 12, 16, 20, 24, 32, 48, 64];

fuzz_target!(|data: &[u8]| {
    let Some((&sel, script)) = data.split_first() else { return };
    let cfg = IsoTpConfig {
        tx_dl: LENGTHS[usize::from(sel & 7)],
        padding: (sel & 0x08 != 0).then_some(0xCC),
        ext_addr_tx: (sel & 0x10 != 0).then_some(0xF1),
        ext_addr_rx: (sel & 0x20 != 0).then_some(0x10),
        block_size: sel >> 6,
        st_min: sel >> 4,
        max_rx_len: 2048,
        ..IsoTpConfig::default()
    };
    let mut m = IsoTpMachine::new(cfg).unwrap();
    let mut now = Instant::from_micros(0);
    let mut out = Vec::new();
    let mut i = 0;
    while i < script.len() {
        let op = script[i];
        i += 1;
        let len = usize::from(op >> 2).min(script.len() - i);
        let chunk = &script[i..i + len];
        match op & 3 {
            0 => {
                let _ = m.handle_input(now, &chunk[..chunk.len().min(64)]);
                i += len;
            }
            1 => {
                let size = usize::from(op) * 37 % 5000 + 1;
                let _ = m.send(now, &vec![op; size]);
            }
            2 => {
                now = now.add_micros(u64::from(op) * 500);
                m.handle_timeout(now);
            }
            _ => {
                out.clear();
                while let Some(t) = m.poll_transmit(&mut out) {
                    assert!(t.len <= 64 && (t.len <= 8 || LENGTHS.contains(&t.len)), "invalid CAN length {}", t.len);
                    out.clear();
                }
            }
        }
        while let Some(ev) = m.poll_event() {
            if let IsoTpEvent::Received(msg) = ev {
                assert!(!msg.is_empty() && msg.len() <= 2048);
            }
        }
    }
});
