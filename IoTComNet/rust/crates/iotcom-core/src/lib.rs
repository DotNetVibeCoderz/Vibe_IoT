//! # iotcom-core
//!
//! Shared foundation of the IoTCom.Net Rust crates.
//!
//! Every protocol is written **sans-I/O** (inspired by `quinn-proto`): a [`Machine`] receives bytes,
//! commands and timer ticks, and produces bytes to transmit, events and the next deadline.
//! It never touches sockets, serial ports or clocks, so it is deterministic, fuzzable and portable
//! (desktop, embedded Linux, WASM). The C# side owns the I/O and drives the machine through the C ABI.
//!
//! Built by Gravicode Studios, led by Kang Fadhil.
#![forbid(unsafe_code)]

pub mod crc;

use core::fmt;

/// Monotonic time in microseconds, supplied by the caller (the driver loop).
///
/// The origin is arbitrary (e.g. driver start). Using a plain integer keeps the type `repr(C)`-friendly
/// and makes tests fully deterministic.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Instant(pub u64);

impl Instant {
    /// Creates an instant from microseconds.
    pub const fn from_micros(us: u64) -> Self {
        Self(us)
    }

    /// Creates an instant from milliseconds.
    pub const fn from_millis(ms: u64) -> Self {
        Self(ms * 1000)
    }

    /// Microseconds since the origin.
    pub const fn as_micros(self) -> u64 {
        self.0
    }

    /// Adds a duration (saturating).
    pub const fn add_micros(self, us: u64) -> Self {
        Self(self.0.saturating_add(us))
    }
}

/// Errors returned by machines.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Error {
    /// A command argument is outside the protocol limits.
    InvalidArgument(&'static str),
    /// The peer violated the protocol.
    Protocol(&'static str),
    /// The machine cannot accept more work right now (back-pressure).
    Busy,
    /// Output buffer too small; the value is the required size.
    BufferTooSmall(usize),
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Error::InvalidArgument(m) => write!(f, "invalid argument: {m}"),
            Error::Protocol(m) => write!(f, "protocol error: {m}"),
            Error::Busy => f.write_str("busy"),
            Error::BufferTooSmall(n) => write!(f, "buffer too small, {n} bytes required"),
        }
    }
}

impl std::error::Error for Error {}

/// Result alias.
pub type Result<T> = core::result::Result<T, Error>;

/// Describes a datagram/frame produced by [`Machine::poll_transmit`].
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Transmit {
    /// Number of bytes appended to the output buffer.
    pub len: usize,
}

/// A sans-I/O protocol state machine.
///
/// The driver loop (C# `MachineDriver`) repeatedly:
/// 1. feeds received bytes with [`handle_input`](Machine::handle_input),
/// 2. submits user commands with [`handle_command`](Machine::handle_command),
/// 3. calls [`handle_timeout`](Machine::handle_timeout) when [`poll_timeout`](Machine::poll_timeout) expires,
/// 4. drains [`poll_transmit`](Machine::poll_transmit) to the transport and [`poll_event`](Machine::poll_event) to the application.
pub trait Machine {
    /// Events delivered to the application.
    type Event;
    /// Commands accepted from the application.
    type Command;

    /// Feeds bytes received from the transport.
    fn handle_input(&mut self, now: Instant, bytes: &[u8]) -> Result<()>;

    /// Submits an application command.
    fn handle_command(&mut self, now: Instant, cmd: Self::Command) -> Result<()>;

    /// Processes expired timers.
    fn handle_timeout(&mut self, now: Instant);

    /// Appends the next frame to send to `out`. Returns `None` when nothing is pending.
    fn poll_transmit(&mut self, out: &mut Vec<u8>) -> Option<Transmit>;

    /// Returns the next application event.
    fn poll_event(&mut self) -> Option<Self::Event>;

    /// Earliest deadline at which [`handle_timeout`](Machine::handle_timeout) must be called.
    fn poll_timeout(&self) -> Option<Instant>;
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn instant_arithmetic() {
        assert_eq!(Instant::from_millis(2).as_micros(), 2000);
        assert_eq!(Instant(u64::MAX).add_micros(5), Instant(u64::MAX));
    }
}
