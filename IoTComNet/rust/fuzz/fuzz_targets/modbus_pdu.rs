//! Response PDU parsers must reject malformed input with an error, never panic.
#![no_main]
use iotcom_modbus::pdu;
use libfuzzer_sys::fuzz_target;

fuzz_target!(|data: &[u8]| {
    let _ = pdu::parse_registers(data);
    let count = data.first().map_or(0, |b| usize::from(*b) * 8);
    let _ = pdu::parse_bits(data, count);
    let _ = pdu::exception_code(data);
});
