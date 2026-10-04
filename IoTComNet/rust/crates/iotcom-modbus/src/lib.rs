//! # iotcom-modbus
//!
//! Sans-I/O Modbus for IoTCom.Net:
//!
//! * [`frame`] — ADU codecs for Modbus TCP (MBAP), RTU (CRC-16) and ASCII (LRC), with resynchronisation;
//! * [`pdu`] — request builders and response parsers;
//! * [`master::MasterMachine`] — a [`iotcom_core::Machine`] that tracks transactions, pipelining (TCP),
//!   serialisation (RTU/ASCII) and timeouts. It performs no I/O.
//!
//! Built by Gravicode Studios, led by Kang Fadhil.
#![forbid(unsafe_code)]

pub mod frame;
pub mod master;
pub mod pdu;

pub use frame::{Adu, Framing};
pub use master::{MasterCommand, MasterConfig, MasterEvent, MasterMachine};
