//! Master state machine driven by adversarial input: interleaves requests, peer bytes and clock ticks.
//! Invariants: no panic, bounded queue, every event refers to a submitted id, at most one event per id.
#![no_main]
use std::collections::HashSet;

use iotcom_core::{Instant, Machine};
use iotcom_modbus::frame::Framing;
use iotcom_modbus::master::{MasterConfig, MasterEvent, MasterMachine};
use iotcom_modbus::pdu;
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    let Some((&selector, script)) = data.split_first() else { return };
    let cfg = MasterConfig {
        framing: Framing::from_u8(selector % 3).unwrap(),
        timeout_us: 50_000,
        max_in_flight: 4,
        max_queue: 8,
    };
    let mut m = MasterMachine::new(cfg);
    let mut now = Instant::from_micros(0);
    let (mut submitted, mut finished) = (HashSet::new(), HashSet::new());
    let mut out = Vec::new();
    let mut i = 0;
    while i < script.len() {
        let op = script[i];
        i += 1;
        match op % 4 {
            0 => {
                if let Ok(p) = pdu::read(3, u16::from(op), 1 + u16::from(op % 8)) {
                    if let Ok(id) = m.submit(now, op % 4, &p) {
                        assert!(submitted.insert(id));
                    }
                }
            }
            1 => {
                let len = usize::from(script.get(i).copied().unwrap_or(0) % 64);
                let end = (i + 1 + len).min(script.len());
                let _ = m.handle_input(now, script.get(i + 1..end).unwrap_or_default());
                i = end;
            }
            2 => {
                now = now.add_micros(u64::from(op) * 1_000);
                m.handle_timeout(now);
            }
            _ => {
                out.clear();
                while m.poll_transmit(&mut out).is_some() {}
            }
        }
        while let Some(ev) = m.poll_event() {
            let id = match ev {
                MasterEvent::Response { id, .. } | MasterEvent::Timeout { id } => id,
            };
            assert!(submitted.contains(&id), "event for an unknown id");
            assert!(finished.insert(id), "two events for one id");
        }
        assert!(m.pending() <= cfg.max_queue + cfg.max_in_flight);
    }
});
