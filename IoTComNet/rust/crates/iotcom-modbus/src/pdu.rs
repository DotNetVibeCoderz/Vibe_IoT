//! Modbus PDU builders and parsers (function code + data, no addressing).

use iotcom_core::{Error, Result};

/// Read Coils.
pub const READ_COILS: u8 = 0x01;
/// Read Discrete Inputs.
pub const READ_DISCRETE_INPUTS: u8 = 0x02;
/// Read Holding Registers.
pub const READ_HOLDING_REGISTERS: u8 = 0x03;
/// Read Input Registers.
pub const READ_INPUT_REGISTERS: u8 = 0x04;
/// Write Single Coil.
pub const WRITE_SINGLE_COIL: u8 = 0x05;
/// Write Single Register.
pub const WRITE_SINGLE_REGISTER: u8 = 0x06;
/// Write Multiple Coils.
pub const WRITE_MULTIPLE_COILS: u8 = 0x0F;
/// Write Multiple Registers.
pub const WRITE_MULTIPLE_REGISTERS: u8 = 0x10;

/// Maximum PDU size.
pub const MAX_PDU: usize = 253;

/// Builds a read request (0x01–0x04).
pub fn read(function: u8, address: u16, count: u16) -> Result<Vec<u8>> {
    let max = match function {
        READ_COILS | READ_DISCRETE_INPUTS => 2000,
        READ_HOLDING_REGISTERS | READ_INPUT_REGISTERS => 125,
        _ => return Err(Error::InvalidArgument("not a read function")),
    };
    check_range(address, count as usize, max)?;
    let mut pdu = vec![function];
    pdu.extend_from_slice(&address.to_be_bytes());
    pdu.extend_from_slice(&count.to_be_bytes());
    Ok(pdu)
}

/// Builds Write Single Coil.
pub fn write_single_coil(address: u16, value: bool) -> Vec<u8> {
    let mut pdu = vec![WRITE_SINGLE_COIL];
    pdu.extend_from_slice(&address.to_be_bytes());
    pdu.extend_from_slice(if value { &[0xFF, 0x00] } else { &[0x00, 0x00] });
    pdu
}

/// Builds Write Single Register.
pub fn write_single_register(address: u16, value: u16) -> Vec<u8> {
    let mut pdu = vec![WRITE_SINGLE_REGISTER];
    pdu.extend_from_slice(&address.to_be_bytes());
    pdu.extend_from_slice(&value.to_be_bytes());
    pdu
}

/// Builds Write Multiple Registers.
pub fn write_multiple_registers(address: u16, values: &[u16]) -> Result<Vec<u8>> {
    check_range(address, values.len(), 123)?;
    let mut pdu = Vec::with_capacity(6 + values.len() * 2);
    pdu.push(WRITE_MULTIPLE_REGISTERS);
    pdu.extend_from_slice(&address.to_be_bytes());
    pdu.extend_from_slice(&(values.len() as u16).to_be_bytes());
    pdu.push((values.len() * 2) as u8);
    for v in values {
        pdu.extend_from_slice(&v.to_be_bytes());
    }
    Ok(pdu)
}

/// Builds Write Multiple Coils.
pub fn write_multiple_coils(address: u16, values: &[bool]) -> Result<Vec<u8>> {
    check_range(address, values.len(), 1968)?;
    let byte_count = values.len().div_ceil(8);
    let mut pdu = Vec::with_capacity(6 + byte_count);
    pdu.push(WRITE_MULTIPLE_COILS);
    pdu.extend_from_slice(&address.to_be_bytes());
    pdu.extend_from_slice(&(values.len() as u16).to_be_bytes());
    pdu.push(byte_count as u8);
    let start = pdu.len();
    pdu.resize(start + byte_count, 0);
    for (i, &v) in values.iter().enumerate() {
        if v {
            pdu[start + i / 8] |= 1 << (i % 8);
        }
    }
    Ok(pdu)
}

/// Decodes register values from a 0x03/0x04/0x17 response PDU.
pub fn parse_registers(response: &[u8]) -> Result<Vec<u16>> {
    if response.len() < 2 || response.len() < 2 + response[1] as usize || response[1] % 2 != 0 {
        return Err(Error::Protocol("malformed register response"));
    }
    let n = response[1] as usize / 2;
    Ok((0..n).map(|i| u16::from_be_bytes([response[2 + i * 2], response[3 + i * 2]])).collect())
}

/// Decodes `count` bits from a 0x01/0x02 response PDU.
pub fn parse_bits(response: &[u8], count: usize) -> Result<Vec<bool>> {
    if response.len() < 2 || response[1] as usize != count.div_ceil(8) || response.len() < 2 + response[1] as usize {
        return Err(Error::Protocol("malformed bit response"));
    }
    Ok((0..count).map(|i| response[2 + i / 8] & (1 << (i % 8)) != 0).collect())
}

/// Returns `Some(exception_code)` when `response` is an exception PDU.
pub fn exception_code(response: &[u8]) -> Option<u8> {
    match response {
        [fc, code, ..] if fc & 0x80 != 0 => Some(*code),
        [fc] if fc & 0x80 != 0 => Some(0),
        _ => None,
    }
}

fn check_range(address: u16, count: usize, max: usize) -> Result<()> {
    if count == 0 || count > max {
        return Err(Error::InvalidArgument("quantity out of range"));
    }
    if address as usize + count > 65536 {
        return Err(Error::InvalidArgument("address + quantity exceeds 65536"));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn builders_match_spec() {
        assert_eq!(read(READ_HOLDING_REGISTERS, 0x6B, 3).unwrap(), [0x03, 0x00, 0x6B, 0x00, 0x03]);
        assert_eq!(write_single_coil(0xAC, true), [0x05, 0x00, 0xAC, 0xFF, 0x00]);
        assert_eq!(write_multiple_registers(1, &[0x000A, 0x0102]).unwrap(), [0x10, 0x00, 0x01, 0x00, 0x02, 0x04, 0x00, 0x0A, 0x01, 0x02]);
        let coils = [true, false, true, true, false, false, true, true, true, false];
        assert_eq!(write_multiple_coils(0x13, &coils).unwrap(), [0x0F, 0x00, 0x13, 0x00, 0x0A, 0x02, 0xCD, 0x01]);
    }

    #[test]
    fn parsers_and_limits() {
        assert_eq!(parse_registers(&[0x03, 0x06, 0x02, 0x2B, 0x00, 0x00, 0x00, 0x64]).unwrap(), [0x022B, 0, 0x64]);
        assert_eq!(parse_bits(&[0x01, 0x01, 0b101], 3).unwrap(), [true, false, true]);
        assert_eq!(exception_code(&[0x83, 0x02]), Some(2));
        assert!(read(READ_HOLDING_REGISTERS, 0, 126).is_err());
        assert!(read(READ_COILS, 65535, 2).is_err());
    }
}
