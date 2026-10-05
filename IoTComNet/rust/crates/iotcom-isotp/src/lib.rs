//! # iotcom-isotp
//!
//! Sans-I/O ISO-TP (ISO 15765-2) transport for CAN and CAN FD: segmentation and reassembly of
//! messages up to 4 GiB (`FF_DL` escape), flow control (block size, STmin, WAIT, overflow),
//! sequence-number checking and the `N_Bs` / `N_Cr` timeouts.
//!
//! One [`IsoTpMachine`] is one full-duplex ISO-TP channel (one transmit and one receive identifier).
//! The driver filters received CAN frames by the receive identifier and feeds their data bytes with
//! [`Machine::handle_input`]; every frame from [`Machine::poll_transmit`] goes out on the transmit
//! identifier. The machine never touches the bus or the clock.
//!
//! Built by Gravicode Studios, led by Kang Fadhil.
#![forbid(unsafe_code)]

use std::collections::VecDeque;

use iotcom_core::{Error, Instant, Machine, Result, Transmit};

/// Valid CAN FD data lengths (DLC 0–15).
const FD_LENGTHS: [usize; 16] = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];

/// Channel configuration.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct IsoTpConfig {
    /// Transmit data length: 8 for classic CAN; 8, 12, 16, 20, 24, 32, 48 or 64 for CAN FD.
    pub tx_dl: usize,
    /// Padding byte for unused frame bytes; `None` sends the shortest frame (CAN FD frames are
    /// always padded up to the next valid length with `0xCC`).
    pub padding: Option<u8>,
    /// Extended addressing: target address byte prepended to transmitted frames.
    pub ext_addr_tx: Option<u8>,
    /// Extended addressing: address byte expected first in received frames (others are ignored).
    pub ext_addr_rx: Option<u8>,
    /// Block size announced in our flow-control frames (0 = no further flow control).
    pub block_size: u8,
    /// STmin announced in our flow-control frames (raw ISO encoding: 0–127 ms, 0xF1–0xF9 = 100–900 µs).
    pub st_min: u8,
    /// Time to wait for a flow-control frame (`N_Bs`), µs.
    pub n_bs_us: u64,
    /// Time to wait for the next consecutive frame (`N_Cr`), µs.
    pub n_cr_us: u64,
    /// Maximum consecutive FC.WAIT frames accepted while sending.
    pub max_wait_frames: u16,
    /// Largest message we accept; longer first frames are answered with FC.OVFLW.
    pub max_rx_len: usize,
}

impl Default for IsoTpConfig {
    fn default() -> Self {
        Self {
            tx_dl: 8,
            padding: Some(0xCC),
            ext_addr_tx: None,
            ext_addr_rx: None,
            block_size: 0,
            st_min: 0,
            n_bs_us: 1_000_000,
            n_cr_us: 1_000_000,
            max_wait_frames: 10,
            max_rx_len: 4095,
        }
    }
}

/// Why a transfer failed.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum IsoTpError {
    /// No flow control within `N_Bs` (sender side).
    TimeoutBs = 1,
    /// No consecutive frame within `N_Cr` (receiver side).
    TimeoutCr = 2,
    /// Consecutive frame with an unexpected sequence number; the reception was aborted.
    WrongSequence = 3,
    /// The receiver answered FC.OVFLW: the message is too large for it.
    Overflow = 4,
    /// More FC.WAIT frames than `max_wait_frames`.
    TooManyWaits = 5,
    /// A flow-control frame with an invalid status was received.
    InvalidFlowStatus = 6,
    /// A new first or single frame arrived during a reception; the old one was dropped.
    Interrupted = 7,
    /// The incoming message exceeds `max_rx_len`; FC.OVFLW was sent.
    RxTooLarge = 8,
}

/// Events delivered to the application.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum IsoTpEvent {
    /// A complete message was received.
    Received(Vec<u8>),
    /// A first frame announced a message of this length (useful for progress).
    RxStarted(usize),
    /// The message passed to [`IsoTpMachine::send`] has been fully handed to the bus.
    Sent,
    /// A transfer failed.
    Error(IsoTpError),
}

#[derive(Debug)]
enum TxState {
    /// First frame queued, waiting for flow control.
    WaitFc { deadline: Instant, waits: u16 },
    /// Sending consecutive frames.
    Sending { remaining_in_block: Option<u16>, st_min_us: u64, next_at: Instant },
}

#[derive(Debug)]
struct Tx {
    data: Vec<u8>,
    offset: usize,
    sn: u8,
    state: TxState,
}

#[derive(Debug)]
struct Rx {
    data: Vec<u8>,
    expected: usize,
    sn: u8,
    block_left: u16,
    deadline: Instant,
}

/// One full-duplex ISO-TP channel.
#[derive(Debug)]
pub struct IsoTpMachine {
    cfg: IsoTpConfig,
    now: Instant,
    tx: Option<Tx>,
    rx: Option<Rx>,
    out: VecDeque<Vec<u8>>,
    events: VecDeque<IsoTpEvent>,
}

/// Decodes an STmin byte to microseconds (reserved values mean the maximum, 127 ms).
pub fn st_min_to_micros(raw: u8) -> u64 {
    match raw {
        0x00..=0x7F => u64::from(raw) * 1000,
        0xF1..=0xF9 => u64::from(raw - 0xF0) * 100,
        _ => 127_000,
    }
}

impl IsoTpMachine {
    /// Creates a channel. Fails if `tx_dl` is not a valid CAN/CAN FD length ≥ 8.
    pub fn new(cfg: IsoTpConfig) -> Result<Self> {
        if cfg.tx_dl < 8 || !FD_LENGTHS.contains(&cfg.tx_dl) {
            return Err(Error::InvalidArgument("tx_dl must be 8, 12, 16, 20, 24, 32, 48 or 64"));
        }
        Ok(Self { cfg, now: Instant::default(), tx: None, rx: None, out: VecDeque::new(), events: VecDeque::new() })
    }

    /// Configuration.
    pub fn config(&self) -> &IsoTpConfig {
        &self.cfg
    }

    /// True while a message is being transmitted.
    pub fn is_sending(&self) -> bool {
        self.tx.is_some()
    }

    /// True while a multi-frame message is being received.
    pub fn is_receiving(&self) -> bool {
        self.rx.is_some()
    }

    /// Starts sending `payload` (1 byte to 4 GiB). Only one transmission at a time ([`Error::Busy`]).
    pub fn send(&mut self, now: Instant, payload: &[u8]) -> Result<()> {
        self.now = now;
        if payload.is_empty() || payload.len() > u32::MAX as usize {
            return Err(Error::InvalidArgument("payload length must be 1..=4294967295"));
        }
        if self.tx.is_some() {
            return Err(Error::Busy);
        }
        let ext = usize::from(self.cfg.ext_addr_tx.is_some());
        let room = self.cfg.tx_dl - ext;
        // Single frame: classic PCI is 1 byte (len ≤ 7); CAN FD escape is 2 bytes (len ≤ tx_dl - 2).
        let sf_max = if self.cfg.tx_dl == 8 { room - 1 } else { room - 2 };
        if payload.len() <= sf_max {
            let mut f = Vec::with_capacity(self.cfg.tx_dl);
            if payload.len() <= 7 && payload.len() < room {
                f.push(payload.len() as u8);
            } else {
                f.push(0x00);
                f.push(payload.len() as u8);
            }
            f.extend_from_slice(payload);
            self.queue(f);
            self.events.push_back(IsoTpEvent::Sent);
            return Ok(());
        }
        let mut f = Vec::with_capacity(self.cfg.tx_dl);
        if payload.len() <= 4095 {
            f.push(0x10 | (payload.len() >> 8) as u8);
            f.push(payload.len() as u8);
        } else {
            f.extend_from_slice(&[0x10, 0x00]);
            f.extend_from_slice(&(payload.len() as u32).to_be_bytes());
        }
        let first = room - f.len();
        f.extend_from_slice(&payload[..first]);
        self.queue(f);
        self.tx = Some(Tx {
            data: payload.to_vec(),
            offset: first,
            sn: 1,
            state: TxState::WaitFc { deadline: now.add_micros(self.cfg.n_bs_us), waits: 0 },
        });
        Ok(())
    }

    /// Returns a frame obtained from `poll_transmit` to the front of the queue
    /// (used by the FFI layer when the caller's buffer was too small).
    pub fn requeue_transmit(&mut self, frame: Vec<u8>) {
        self.out.push_front(frame);
    }

    /// Aborts the current transmission and reception without events.
    pub fn reset(&mut self) {
        self.tx = None;
        self.rx = None;
        self.out.clear();
    }

    fn queue(&mut self, mut frame: Vec<u8>) {
        if let Some(a) = self.cfg.ext_addr_tx {
            frame.insert(0, a);
        }
        let target = match self.cfg.padding {
            _ if frame.len() > 8 => FD_LENGTHS.iter().copied().find(|&l| l >= frame.len()).unwrap_or(64),
            Some(_) => self.cfg.tx_dl.min(8).max(frame.len()),
            None => frame.len(),
        };
        let pad = self.cfg.padding.unwrap_or(0xCC);
        frame.resize(target, pad);
        self.out.push_back(frame);
    }

    fn flow_control(&mut self, status: u8) {
        let (bs, st) = (self.cfg.block_size, self.cfg.st_min);
        self.queue(vec![0x30 | status, bs, st]);
    }

    /// Emits consecutive frames that are due now.
    fn pump(&mut self) {
        let room = self.cfg.tx_dl - usize::from(self.cfg.ext_addr_tx.is_some()) - 1;
        let now = self.now;
        loop {
            let Some(tx) = self.tx.as_mut() else { return };
            let TxState::Sending { remaining_in_block, st_min_us, next_at } = &mut tx.state else { return };
            if now < *next_at || *remaining_in_block == Some(0) {
                return;
            }
            let end = (tx.offset + room).min(tx.data.len());
            let mut f = Vec::with_capacity(room + 1);
            f.push(0x20 | tx.sn);
            f.extend_from_slice(&tx.data[tx.offset..end]);
            tx.offset = end;
            tx.sn = (tx.sn + 1) & 0x0F;
            *next_at = now.add_micros(*st_min_us);
            let st = *st_min_us;
            let done = tx.offset >= tx.data.len();
            if let Some(n) = remaining_in_block {
                *n -= 1;
                if *n == 0 && !done {
                    tx.state = TxState::WaitFc { deadline: now.add_micros(self.cfg.n_bs_us), waits: 0 };
                }
            }
            self.queue(f);
            if done {
                self.tx = None;
                self.events.push_back(IsoTpEvent::Sent);
                return;
            }
            if st > 0 {
                return; // next frame after STmin, via poll_timeout/handle_timeout
            }
        }
    }

    fn on_flow_control(&mut self, pci: u8, data: &[u8]) {
        let max_waits = self.cfg.max_wait_frames;
        let n_bs = self.cfg.n_bs_us;
        let now = self.now;
        let Some(tx) = self.tx.as_mut() else { return };
        let TxState::WaitFc { waits, .. } = tx.state else { return };
        match pci & 0x0F {
            0 => {
                let bs = data.first().copied().unwrap_or(0);
                let st = st_min_to_micros(data.get(1).copied().unwrap_or(0));
                tx.state = TxState::Sending {
                    remaining_in_block: (bs > 0).then_some(u16::from(bs)),
                    st_min_us: st,
                    next_at: now,
                };
                self.pump();
            }
            1 => {
                if waits >= max_waits {
                    self.tx = None;
                    self.events.push_back(IsoTpEvent::Error(IsoTpError::TooManyWaits));
                } else {
                    tx.state = TxState::WaitFc { deadline: now.add_micros(n_bs), waits: waits + 1 };
                }
            }
            2 => {
                self.tx = None;
                self.events.push_back(IsoTpEvent::Error(IsoTpError::Overflow));
            }
            _ => {
                self.tx = None;
                self.events.push_back(IsoTpEvent::Error(IsoTpError::InvalidFlowStatus));
            }
        }
    }

    fn interrupt_rx(&mut self) {
        if self.rx.take().is_some() {
            self.events.push_back(IsoTpEvent::Error(IsoTpError::Interrupted));
        }
    }

    fn on_frame(&mut self, frame: &[u8]) {
        let frame = match self.cfg.ext_addr_rx {
            Some(a) => match frame.split_first() {
                Some((&b, rest)) if b == a => rest,
                _ => return,
            },
            None => frame,
        };
        let Some((&pci, rest)) = frame.split_first() else { return };
        match pci >> 4 {
            0 => {
                // Single frame: classic length in the low nibble, or CAN FD escape (low nibble 0 + length byte).
                let (len, body) = if pci & 0x0F != 0 {
                    (usize::from(pci & 0x0F), rest)
                } else {
                    match rest.split_first() {
                        Some((&l, b)) if frame.len() > 8 => (usize::from(l), b),
                        _ => return,
                    }
                };
                if len == 0 || len > body.len() {
                    return;
                }
                self.interrupt_rx();
                self.events.push_back(IsoTpEvent::Received(body[..len].to_vec()));
            }
            1 => {
                if rest.is_empty() {
                    return;
                }
                let short = (usize::from(pci & 0x0F) << 8) | usize::from(rest[0]);
                let (len, body) = if short != 0 {
                    (short, &rest[1..])
                } else if rest.len() >= 5 {
                    (u32::from_be_bytes([rest[1], rest[2], rest[3], rest[4]]) as usize, &rest[5..])
                } else {
                    return;
                };
                // A first frame must not fit a single frame; ignore malformed ones.
                if len <= body.len() {
                    return;
                }
                self.interrupt_rx();
                if len > self.cfg.max_rx_len {
                    self.flow_control(2);
                    self.events.push_back(IsoTpEvent::Error(IsoTpError::RxTooLarge));
                    return;
                }
                let mut data = Vec::with_capacity(len);
                data.extend_from_slice(body);
                self.events.push_back(IsoTpEvent::RxStarted(len));
                self.rx = Some(Rx {
                    data,
                    expected: len,
                    sn: 1,
                    block_left: u16::from(self.cfg.block_size),
                    deadline: self.now.add_micros(self.cfg.n_cr_us),
                });
                self.flow_control(0);
            }
            2 => {
                let Some(rx) = self.rx.as_mut() else { return }; // unexpected CF: ignore
                if pci & 0x0F != rx.sn {
                    self.rx = None;
                    self.events.push_back(IsoTpEvent::Error(IsoTpError::WrongSequence));
                    return;
                }
                let take = (rx.expected - rx.data.len()).min(rest.len());
                rx.data.extend_from_slice(&rest[..take]);
                rx.sn = (rx.sn + 1) & 0x0F;
                rx.deadline = self.now.add_micros(self.cfg.n_cr_us);
                if rx.data.len() >= rx.expected {
                    let rx = self.rx.take().expect("present");
                    self.events.push_back(IsoTpEvent::Received(rx.data));
                    return;
                }
                if self.cfg.block_size > 0 {
                    rx.block_left -= 1;
                    if rx.block_left == 0 {
                        rx.block_left = u16::from(self.cfg.block_size);
                        self.flow_control(0);
                    }
                }
            }
            3 => self.on_flow_control(pci, rest),
            _ => {}
        }
    }
}

/// Commands accepted through [`Machine::handle_command`].
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum IsoTpCommand {
    /// Send a message.
    Send(Vec<u8>),
    /// Abort everything.
    Reset,
}

impl Machine for IsoTpMachine {
    type Event = IsoTpEvent;
    type Command = IsoTpCommand;

    /// Feeds the data bytes of one received CAN frame (already filtered by receive identifier).
    fn handle_input(&mut self, now: Instant, bytes: &[u8]) -> Result<()> {
        self.now = now;
        self.on_frame(bytes);
        Ok(())
    }

    fn handle_command(&mut self, now: Instant, cmd: IsoTpCommand) -> Result<()> {
        match cmd {
            IsoTpCommand::Send(p) => self.send(now, &p),
            IsoTpCommand::Reset => {
                self.now = now;
                self.reset();
                Ok(())
            }
        }
    }

    fn handle_timeout(&mut self, now: Instant) {
        self.now = now;
        if let Some(rx) = &self.rx {
            if now >= rx.deadline {
                self.rx = None;
                self.events.push_back(IsoTpEvent::Error(IsoTpError::TimeoutCr));
            }
        }
        if let Some(Tx { state: TxState::WaitFc { deadline, .. }, .. }) = &self.tx {
            if now >= *deadline {
                self.tx = None;
                self.events.push_back(IsoTpEvent::Error(IsoTpError::TimeoutBs));
            }
        }
        self.pump();
    }

    /// Appends the data of the next CAN frame to transmit.
    fn poll_transmit(&mut self, out: &mut Vec<u8>) -> Option<Transmit> {
        let f = self.out.pop_front()?;
        out.extend_from_slice(&f);
        Some(Transmit { len: f.len() })
    }

    fn poll_event(&mut self) -> Option<IsoTpEvent> {
        self.events.pop_front()
    }

    fn poll_timeout(&self) -> Option<Instant> {
        let rx = self.rx.as_ref().map(|r| r.deadline);
        let tx = match &self.tx {
            Some(Tx { state: TxState::WaitFc { deadline, .. }, .. }) => Some(*deadline),
            Some(Tx { state: TxState::Sending { next_at, remaining_in_block, .. }, .. }) if remaining_in_block != &Some(0) => {
                Some(*next_at)
            }
            _ => None,
        };
        match (rx, tx) {
            (Some(a), Some(b)) => Some(a.min(b)),
            (a, b) => a.or(b),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn t(ms: u64) -> Instant {
        Instant::from_millis(ms)
    }

    fn frames(m: &mut IsoTpMachine) -> Vec<Vec<u8>> {
        let mut v = Vec::new();
        let mut out = Vec::new();
        while m.poll_transmit(&mut out).is_some() {
            v.push(std::mem::take(&mut out));
        }
        v
    }

    fn events(m: &mut IsoTpMachine) -> Vec<IsoTpEvent> {
        std::iter::from_fn(|| m.poll_event()).collect()
    }

    /// Shuttles frames between two machines until both are quiet; advances time to due timers.
    fn run(a: &mut IsoTpMachine, b: &mut IsoTpMachine, mut now: Instant) -> Instant {
        for _ in 0..100_000 {
            let fa = frames(a);
            let fb = frames(b);
            for f in &fa {
                b.handle_input(now, f).unwrap();
            }
            for f in &fb {
                a.handle_input(now, f).unwrap();
            }
            if fa.is_empty() && fb.is_empty() {
                match [a.poll_timeout(), b.poll_timeout()].into_iter().flatten().min() {
                    Some(d) if a.is_sending() || b.is_sending() => {
                        now = now.max(d);
                        a.handle_timeout(now);
                        b.handle_timeout(now);
                    }
                    _ => return now,
                }
            }
        }
        panic!("did not settle");
    }

    #[test]
    fn single_frame_classic_is_padded() {
        let mut m = IsoTpMachine::new(IsoTpConfig::default()).unwrap();
        m.send(t(0), &[0x22, 0xF1, 0x90]).unwrap();
        assert_eq!(frames(&mut m), vec![vec![0x03, 0x22, 0xF1, 0x90, 0xCC, 0xCC, 0xCC, 0xCC]]);
        assert_eq!(events(&mut m), vec![IsoTpEvent::Sent]);
    }

    #[test]
    fn receives_known_vin_response() {
        // Classic multi-frame VIN response (UDS 0x62 F190 + 17 chars) as seen on a real bus.
        let mut m = IsoTpMachine::new(IsoTpConfig::default()).unwrap();
        m.handle_input(t(0), &[0x10, 0x14, 0x62, 0xF1, 0x90, b'W', b'V', b'W']).unwrap();
        assert_eq!(frames(&mut m), vec![vec![0x30, 0x00, 0x00, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC]]);
        m.handle_input(t(1), &[0x21, b'Z', b'Z', b'Z', b'1', b'J', b'Z', b'3']).unwrap();
        m.handle_input(t(2), &[0x22, b'W', b'0', b'0', b'0', b'0', b'0', b'1']).unwrap();
        let ev = events(&mut m);
        assert_eq!(ev[0], IsoTpEvent::RxStarted(20));
        assert_eq!(ev[1], IsoTpEvent::Received(b"\x62\xF1\x90WVWZZZ1JZ3W000001".to_vec()));
    }

    #[test]
    fn multi_frame_round_trip_with_block_size_and_st_min() {
        let mut a = IsoTpMachine::new(IsoTpConfig::default()).unwrap();
        let mut b = IsoTpMachine::new(IsoTpConfig { block_size: 4, st_min: 5, ..IsoTpConfig::default() }).unwrap();
        let payload: Vec<u8> = (0..300u16).map(|i| i as u8).collect();
        a.send(t(0), &payload).unwrap();
        let end = run(&mut a, &mut b, t(0));
        let ev = events(&mut b);
        assert_eq!(ev.last(), Some(&IsoTpEvent::Received(payload)));
        assert_eq!(events(&mut a), vec![IsoTpEvent::Sent]);
        // 42 consecutive frames in blocks of 4: 3 STmin gaps per block (the first CF follows the FC at once).
        assert_eq!(end, t(10 * 3 * 5 + 5), "STmin pacing");
    }

    #[test]
    fn can_fd_and_escaped_first_frame() {
        let fd = IsoTpConfig { tx_dl: 64, max_rx_len: 100_000, ..IsoTpConfig::default() };
        let mut a = IsoTpMachine::new(fd).unwrap();
        let mut b = IsoTpMachine::new(fd).unwrap();
        // CAN FD single frame with the escape length byte.
        a.send(t(0), &[7u8; 40]).unwrap();
        let f = frames(&mut a);
        assert_eq!((f[0][0], f[0][1], f[0].len()), (0x00, 40, 48));
        // > 4095 bytes uses the 32-bit FF_DL escape.
        let big: Vec<u8> = (0..10_000u32).map(|i| (i * 7) as u8).collect();
        a.send(t(0), &big).unwrap();
        assert_eq!(&a.out[0][..6], &[0x10, 0x00, 0x00, 0x00, 0x27, 0x10]);
        run(&mut a, &mut b, t(0));
        assert_eq!(events(&mut b).last(), Some(&IsoTpEvent::Received(big)));
    }

    #[test]
    fn timeouts_overflow_and_wrong_sequence() {
        // N_Bs: nobody answers the first frame.
        let mut a = IsoTpMachine::new(IsoTpConfig::default()).unwrap();
        a.send(t(0), &[1; 20]).unwrap();
        frames(&mut a);
        assert_eq!(a.poll_timeout(), Some(t(1000)));
        a.handle_timeout(t(1000));
        assert_eq!(events(&mut a), vec![IsoTpEvent::Error(IsoTpError::TimeoutBs)]);

        // Overflow: receiver limit smaller than the message.
        let mut b = IsoTpMachine::new(IsoTpConfig { max_rx_len: 10, ..IsoTpConfig::default() }).unwrap();
        a.send(t(0), &[1; 20]).unwrap();
        run(&mut a, &mut b, t(0));
        assert_eq!(events(&mut a), vec![IsoTpEvent::Error(IsoTpError::Overflow)]);
        assert_eq!(events(&mut b), vec![IsoTpEvent::Error(IsoTpError::RxTooLarge)]);

        // Wrong sequence number and N_Cr.
        let mut r = IsoTpMachine::new(IsoTpConfig::default()).unwrap();
        r.handle_input(t(0), &[0x10, 20, 1, 2, 3, 4, 5, 6]).unwrap();
        r.handle_input(t(1), &[0x22, 0, 0, 0, 0, 0, 0, 0]).unwrap();
        assert_eq!(events(&mut r).last(), Some(&IsoTpEvent::Error(IsoTpError::WrongSequence)));
        r.handle_input(t(2), &[0x10, 20, 1, 2, 3, 4, 5, 6]).unwrap();
        r.handle_timeout(t(1002));
        assert_eq!(events(&mut r).last(), Some(&IsoTpEvent::Error(IsoTpError::TimeoutCr)));
    }

    #[test]
    fn wait_frames_and_extended_addressing() {
        let mut a = IsoTpMachine::new(IsoTpConfig { max_wait_frames: 1, ext_addr_tx: Some(0xF1), ..IsoTpConfig::default() }).unwrap();
        a.send(t(0), &[9; 30]).unwrap();
        let f = frames(&mut a);
        assert_eq!(&f[0][..3], &[0xF1, 0x10, 30]);
        a.handle_input(t(1), &[0x31, 0, 0]).unwrap(); // WAIT
        assert!(events(&mut a).is_empty());
        a.handle_input(t(2), &[0x31, 0, 0]).unwrap(); // second WAIT > max
        assert_eq!(events(&mut a), vec![IsoTpEvent::Error(IsoTpError::TooManyWaits)]);

        let mut b = IsoTpMachine::new(IsoTpConfig { ext_addr_rx: Some(0x10), ..IsoTpConfig::default() }).unwrap();
        b.handle_input(t(0), &[0x99, 0x02, 1, 2]).unwrap(); // other address: ignored
        b.handle_input(t(0), &[0x10, 0x02, 1, 2]).unwrap();
        assert_eq!(events(&mut b), vec![IsoTpEvent::Received(vec![1, 2])]);
    }

    #[test]
    fn st_min_encoding() {
        assert_eq!(st_min_to_micros(0x14), 20_000);
        assert_eq!(st_min_to_micros(0xF3), 300);
        assert_eq!(st_min_to_micros(0x80), 127_000);
    }
}
