//! Small checksum helpers needed inside Rust protocol crates.
//!
//! These intentionally duplicate a few presets of the C# `CrcCatalog` (ADR-006: small stateless
//! functions are not worth an FFI call). Divergence is prevented by the shared vectors in
//! `/conformance/crc.json`, which both test suites execute.

/// CRC-16/MODBUS lookup table (reflected polynomial 0xA001).
const MODBUS_TABLE: [u16; 256] = build_reflected_table(0xA001);

/// CRC-16/IBM-SDLC (X.25) lookup table (reflected polynomial 0x8408).
const X25_TABLE: [u16; 256] = build_reflected_table(0x8408);

const fn build_reflected_table(rpoly: u16) -> [u16; 256] {
    let mut table = [0u16; 256];
    let mut i = 0;
    while i < 256 {
        let mut crc = i as u16;
        let mut b = 0;
        while b < 8 {
            crc = if crc & 1 != 0 { (crc >> 1) ^ rpoly } else { crc >> 1 };
            b += 1;
        }
        table[i] = crc;
        i += 1;
    }
    table
}

/// CRC-16/MODBUS (init 0xFFFF, reflected). Appended to RTU frames low byte first.
pub fn crc16_modbus(data: &[u8]) -> u16 {
    let mut crc: u16 = 0xFFFF;
    for &b in data {
        crc = (crc >> 8) ^ MODBUS_TABLE[((crc ^ b as u16) & 0xFF) as usize];
    }
    crc
}

/// CRC-16/IBM-SDLC (X.25, HDLC FCS-16).
pub fn crc16_x25(data: &[u8]) -> u16 {
    let mut crc: u16 = 0xFFFF;
    for &b in data {
        crc = (crc >> 8) ^ X25_TABLE[((crc ^ b as u16) & 0xFF) as usize];
    }
    !crc
}

/// CRC-16/MCRF4XX (MAVLink, before CRC_EXTRA).
pub fn crc16_mcrf4xx(data: &[u8]) -> u16 {
    let mut crc: u16 = 0xFFFF;
    for &b in data {
        crc = (crc >> 8) ^ X25_TABLE[((crc ^ b as u16) & 0xFF) as usize];
    }
    crc
}

/// Modbus ASCII longitudinal redundancy check.
pub fn lrc(data: &[u8]) -> u8 {
    data.iter().fold(0u8, |acc, b| acc.wrapping_add(*b)).wrapping_neg()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn check_values() {
        assert_eq!(crc16_modbus(b"123456789"), 0x4B37);
        assert_eq!(crc16_x25(b"123456789"), 0x906E);
        assert_eq!(crc16_mcrf4xx(b"123456789"), 0x6F91);
        assert_eq!(lrc(&[0x11, 0x03, 0x00, 0x6B, 0x00, 0x03]), 0x7E);
    }

    /// Runs the shared vectors in /conformance/crc.json for the presets implemented here.
    #[test]
    fn conformance_vectors() {
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../../conformance/crc.json");
        let text = std::fs::read_to_string(path).expect("conformance/crc.json");
        let mut checked = 0;
        for (algo, input, expected) in parse_vectors(&text) {
            let data = hex(&input);
            let got = match algo.as_str() {
                "CRC-16/MODBUS" => crc16_modbus(&data),
                "CRC-16/IBM-SDLC" => crc16_x25(&data),
                "CRC-16/MCRF4XX" => crc16_mcrf4xx(&data),
                _ => continue,
            };
            assert_eq!(format!("{got:04X}"), expected, "{algo} {input}");
            checked += 1;
        }
        assert!(checked >= 15, "expected vectors for 3 presets, checked {checked}");
    }

    /// Minimal parser for the flat JSON array produced by conformance/generate.py (no serde dependency).
    fn parse_vectors(text: &str) -> Vec<(String, String, String)> {
        let field = |obj: &str, key: &str| -> String {
            let pat = format!("\"{key}\": \"");
            let start = obj.find(&pat).map(|i| i + pat.len()).unwrap_or(0);
            let end = obj[start..].find('"').map(|i| i + start).unwrap_or(start);
            obj[start..end].to_string()
        };
        text.split('{')
            .skip(1)
            .map(|obj| (field(obj, "algorithm"), field(obj, "input"), field(obj, "expected")))
            .collect()
    }

    fn hex(s: &str) -> Vec<u8> {
        (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
    }
}
