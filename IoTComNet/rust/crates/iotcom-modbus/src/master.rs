//! Sans-I/O Modbus master (client) state machine.

use std::collections::VecDeque;

use iotcom_core::{Error, Instant, Machine, Result, Transmit};

use crate::frame::{self, Decode, Framing};
use crate::pdu::MAX_PDU;

/// Master configuration.
#[derive(Clone, Copy, Debug)]
pub struct MasterConfig {
    /// Framing variant.
    pub framing: Framing,
    /// Response timeout in microseconds.
    pub timeout_us: u64,
    /// Maximum in-flight requests (TCP only; RTU/ASCII always use 1).
    pub max_in_flight: usize,
    /// Maximum queued (not yet sent) requests before [`Error::Busy`].
    pub max_queue: usize,
}

impl Default for MasterConfig {
    fn default() -> Self {
        Self { framing: Framing::Tcp, timeout_us: 1_000_000, max_in_flight: 16, max_queue: 256 }
    }
}

/// A request submitted by the application.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MasterCommand {
    /// Target unit id.
    pub unit_id: u8,
    /// Request PDU (function code + data).
    pub pdu: Vec<u8>,
}

/// Events delivered to the application.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum MasterEvent {
    /// A response PDU (possibly an exception PDU, see [`crate::pdu::exception_code`]).
    Response {
        /// Request id returned by [`MasterMachine::submit`].
        id: u32,
        /// Responding unit.
        unit_id: u8,
        /// Response PDU.
        pdu: Vec<u8>,
    },
    /// No response arrived before the deadline.
    Timeout {
        /// Request id.
        id: u32,
    },
}

#[derive(Debug)]
struct Queued {
    id: u32,
    unit_id: u8,
    pdu: Vec<u8>,
}

#[derive(Debug)]
struct InFlight {
    id: u32,
    tid: u16,
    unit_id: u8,
    function: u8,
    deadline: Instant,
}

/// Modbus master machine: queues requests, frames them, matches responses and expires timeouts.
#[derive(Debug)]
pub struct MasterMachine {
    cfg: MasterConfig,
    next_id: u32,
    next_tid: u16,
    queue: VecDeque<Queued>,
    in_flight: Vec<InFlight>,
    tx: VecDeque<Vec<u8>>,
    rx: Vec<u8>,
    events: VecDeque<MasterEvent>,
}

const MAX_RX_BUFFER: usize = 8 * 1024;

impl MasterMachine {
    /// Creates a machine.
    pub fn new(cfg: MasterConfig) -> Self {
        Self {
            cfg,
            next_id: 0,
            next_tid: 0,
            queue: VecDeque::new(),
            in_flight: Vec::new(),
            tx: VecDeque::new(),
            rx: Vec::new(),
            events: VecDeque::new(),
        }
    }

    /// Configuration.
    pub fn config(&self) -> &MasterConfig {
        &self.cfg
    }

    /// Number of requests waiting or in flight.
    pub fn pending(&self) -> usize {
        self.queue.len() + self.in_flight.len()
    }

    /// Submits a request and returns its id. The frame becomes available from `poll_transmit`.
    pub fn submit(&mut self, now: Instant, unit_id: u8, pdu: &[u8]) -> Result<u32> {
        if pdu.is_empty() || pdu.len() > MAX_PDU {
            return Err(Error::InvalidArgument("PDU length must be 1..=253"));
        }
        if self.queue.len() >= self.cfg.max_queue {
            return Err(Error::Busy);
        }
        self.next_id = self.next_id.wrapping_add(1);
        let id = self.next_id;
        self.queue.push_back(Queued { id, unit_id, pdu: pdu.to_vec() });
        self.pump(now);
        Ok(id)
    }

    /// Returns a frame obtained from `poll_transmit` to the front of the transmit queue
    /// (used by the FFI layer when the caller's buffer was too small).
    pub fn requeue_transmit(&mut self, frame: Vec<u8>) {
        self.tx.push_front(frame);
    }

    fn limit(&self) -> usize {
        if self.cfg.framing.has_transaction_ids() {
            self.cfg.max_in_flight.max(1)
        } else {
            1
        }
    }

    /// Moves queued requests into flight while capacity allows.
    fn pump(&mut self, now: Instant) {
        while self.in_flight.len() < self.limit() {
            let Some(req) = self.queue.pop_front() else { break };
            let tid = if self.cfg.framing.has_transaction_ids() {
                self.next_tid = self.next_tid.wrapping_add(1);
                self.next_tid
            } else {
                0
            };
            let mut frame = Vec::with_capacity(req.pdu.len() + 8);
            frame::encode(self.cfg.framing, tid, req.unit_id, &req.pdu, &mut frame);
            self.tx.push_back(frame);
            self.in_flight.push(InFlight {
                id: req.id,
                tid,
                unit_id: req.unit_id,
                function: req.pdu[0],
                deadline: now.add_micros(self.cfg.timeout_us),
            });
        }
    }

    fn on_frame(&mut self, adu: frame::Adu) {
        let Some(&fc) = adu.pdu.first() else { return };
        let tcp = self.cfg.framing.has_transaction_ids();
        let pos = self.in_flight.iter().position(|f| {
            (if tcp { f.tid == adu.transaction_id } else { f.unit_id == adu.unit_id }) && (fc & 0x7F) == f.function
        });
        if let Some(i) = pos {
            let f = self.in_flight.swap_remove(i);
            self.events.push_back(MasterEvent::Response { id: f.id, unit_id: adu.unit_id, pdu: adu.pdu });
        }
        // Unmatched frames (late responses after a timeout, other masters) are dropped.
    }
}

impl Machine for MasterMachine {
    type Event = MasterEvent;
    type Command = MasterCommand;

    fn handle_input(&mut self, now: Instant, bytes: &[u8]) -> Result<()> {
        self.rx.extend_from_slice(bytes);
        let mut offset = 0;
        loop {
            match frame::decode(self.cfg.framing, &self.rx[offset..], false) {
                Decode::NeedMore => break,
                Decode::Skip(n) => offset += n,
                Decode::Frame(adu, used) => {
                    offset += used;
                    self.on_frame(adu);
                }
            }
        }
        self.rx.drain(..offset);
        if self.rx.len() > MAX_RX_BUFFER {
            self.rx.clear(); // bounded memory against a misbehaving peer
        }
        self.pump(now);
        Ok(())
    }

    fn handle_command(&mut self, now: Instant, cmd: MasterCommand) -> Result<()> {
        self.submit(now, cmd.unit_id, &cmd.pdu).map(|_| ())
    }

    fn handle_timeout(&mut self, now: Instant) {
        let mut expired = false;
        let mut i = 0;
        while i < self.in_flight.len() {
            if self.in_flight[i].deadline <= now {
                let f = self.in_flight.swap_remove(i);
                self.events.push_back(MasterEvent::Timeout { id: f.id });
                expired = true;
            } else {
                i += 1;
            }
        }
        if expired && !self.cfg.framing.has_transaction_ids() {
            self.rx.clear(); // a partial RTU frame from the timed-out exchange must not poison the next one
        }
        self.pump(now);
    }

    fn poll_transmit(&mut self, out: &mut Vec<u8>) -> Option<Transmit> {
        let frame = self.tx.pop_front()?;
        out.extend_from_slice(&frame);
        Some(Transmit { len: frame.len() })
    }

    fn poll_event(&mut self) -> Option<MasterEvent> {
        self.events.pop_front()
    }

    fn poll_timeout(&self) -> Option<Instant> {
        self.in_flight.iter().map(|f| f.deadline).min()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::pdu;

    fn take_tx(m: &mut MasterMachine) -> Vec<Vec<u8>> {
        let mut frames = Vec::new();
        loop {
            let mut out = Vec::new();
            if m.poll_transmit(&mut out).is_none() {
                break;
            }
            frames.push(out);
        }
        frames
    }

    fn respond(framing: Framing, request: &[u8], expect_tid: bool, registers: &[u16]) -> Vec<u8> {
        let frame::Decode::Frame(adu, _) = frame::decode(framing, request, true) else { panic!("bad request") };
        let mut resp = vec![adu.pdu[0], (registers.len() * 2) as u8];
        for r in registers {
            resp.extend_from_slice(&r.to_be_bytes());
        }
        let mut out = Vec::new();
        frame::encode(framing, if expect_tid { adu.transaction_id } else { 0 }, adu.unit_id, &resp, &mut out);
        out
    }

    #[test]
    fn tcp_pipelines_and_matches_out_of_order() {
        let mut m = MasterMachine::new(MasterConfig::default());
        let t0 = Instant::from_millis(0);
        let a = m.submit(t0, 1, &pdu::read(pdu::READ_HOLDING_REGISTERS, 0, 1).unwrap()).unwrap();
        let b = m.submit(t0, 1, &pdu::read(pdu::READ_HOLDING_REGISTERS, 1, 1).unwrap()).unwrap();
        let tx = take_tx(&mut m);
        assert_eq!(tx.len(), 2);
        // answer b first
        m.handle_input(t0, &respond(Framing::Tcp, &tx[1], true, &[22])).unwrap();
        m.handle_input(t0, &respond(Framing::Tcp, &tx[0], true, &[11])).unwrap();
        assert_eq!(m.poll_event(), Some(MasterEvent::Response { id: b, unit_id: 1, pdu: vec![3, 2, 0, 22] }));
        assert_eq!(m.poll_event(), Some(MasterEvent::Response { id: a, unit_id: 1, pdu: vec![3, 2, 0, 11] }));
        assert_eq!(m.poll_timeout(), None);
    }

    #[test]
    fn rtu_serialises_and_handles_split_input() {
        let mut m = MasterMachine::new(MasterConfig { framing: Framing::Rtu, ..Default::default() });
        let t0 = Instant::from_millis(0);
        m.submit(t0, 1, &pdu::read(pdu::READ_HOLDING_REGISTERS, 0, 1).unwrap()).unwrap();
        m.submit(t0, 1, &pdu::read(pdu::READ_HOLDING_REGISTERS, 5, 1).unwrap()).unwrap();
        let tx = take_tx(&mut m);
        assert_eq!(tx.len(), 1, "RTU sends one request at a time");
        let resp = respond(Framing::Rtu, &tx[0], false, &[7]);
        m.handle_input(t0, &resp[..3]).unwrap();
        assert!(m.poll_event().is_none());
        m.handle_input(t0, &resp[3..]).unwrap();
        assert!(matches!(m.poll_event(), Some(MasterEvent::Response { .. })));
        assert_eq!(take_tx(&mut m).len(), 1, "second request released after the first completes");
    }

    #[test]
    fn timeouts_expire_and_release_the_bus() {
        let mut m = MasterMachine::new(MasterConfig { framing: Framing::Rtu, timeout_us: 500_000, ..Default::default() });
        let id = m.submit(Instant::from_millis(0), 9, &pdu::read(pdu::READ_COILS, 0, 8).unwrap()).unwrap();
        m.submit(Instant::from_millis(0), 9, &pdu::read(pdu::READ_COILS, 8, 8).unwrap()).unwrap();
        take_tx(&mut m);
        assert_eq!(m.poll_timeout(), Some(Instant::from_millis(500)));
        m.handle_timeout(Instant::from_millis(499));
        assert!(m.poll_event().is_none());
        m.handle_timeout(Instant::from_millis(500));
        assert_eq!(m.poll_event(), Some(MasterEvent::Timeout { id }));
        assert_eq!(take_tx(&mut m).len(), 1);
    }

    #[test]
    fn back_pressure_and_validation() {
        let mut m = MasterMachine::new(MasterConfig { max_queue: 1, max_in_flight: 1, ..Default::default() });
        let t = Instant::default();
        m.submit(t, 1, &[3, 0, 0, 0, 1]).unwrap(); // in flight
        m.submit(t, 1, &[3, 0, 0, 0, 1]).unwrap(); // queued
        assert_eq!(m.submit(t, 1, &[3, 0, 0, 0, 1]), Err(Error::Busy));
        assert!(m.submit(t, 1, &[]).is_err());
    }
}
