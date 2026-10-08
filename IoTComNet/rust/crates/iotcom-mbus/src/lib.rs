//! Wired M-Bus codecs (EN 13757-2/-3): frames (E5, short, control, long) and the structure of variable-data records
//! (DIF/DIFE, VIF/VIFE, raw value, storage, tariff, sub-unit). The C# runtime (`IoTCom.Net.Protocols.MBus`) is
//! managed; this crate is its twin, kept in sync by `/conformance/mbus.json` and fuzzed with `cargo fuzz run mbus`.
#![forbid(unsafe_code)]

use core::fmt;

/// Why input was rejected.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Error {
    /// More bytes are needed.
    Incomplete,
    /// Bad start/stop byte or inconsistent length.
    Framing,
    /// Checksum mismatch.
    Checksum,
    /// Malformed record.
    Record(&'static str),
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Incomplete => f.write_str("incomplete"),
            Self::Framing => f.write_str("bad framing"),
            Self::Checksum => f.write_str("checksum mismatch"),
            Self::Record(m) => write!(f, "bad record: {m}"),
        }
    }
}

impl std::error::Error for Error {}

/// An M-Bus frame.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Frame {
    /// E5.
    Ack,
    /// 10 C A CS 16.
    Short {
        /// C field.
        control: u8,
        /// A field.
        address: u8,
    },
    /// 68 L L 68 C A CI data CS 16 (control frame when data is empty).
    Long {
        /// C field.
        control: u8,
        /// A field.
        address: u8,
        /// CI field.
        ci: u8,
        /// User data.
        data: Vec<u8>,
    },
}

impl Frame {
    /// Encodes the frame.
    pub fn encode(&self) -> Vec<u8> {
        match self {
            Frame::Ack => vec![0xE5],
            Frame::Short { control, address } => vec![0x10, *control, *address, control.wrapping_add(*address), 0x16],
            Frame::Long { control, address, ci, data } => {
                let mut body = vec![*control, *address, *ci];
                body.extend_from_slice(data);
                let len = body.len() as u8;
                let cs = body.iter().fold(0u8, |a, b| a.wrapping_add(*b));
                let mut out = vec![0x68, len, len, 0x68];
                out.extend_from_slice(&body);
                out.extend_from_slice(&[cs, 0x16]);
                out
            }
        }
    }
}

/// Decodes the frame at the start of `d`; returns it and the bytes consumed. Never panics.
pub fn read_frame(d: &[u8]) -> Result<(Frame, usize), Error> {
    match d.first() {
        None => Err(Error::Incomplete),
        Some(0xE5) => Ok((Frame::Ack, 1)),
        Some(0x10) => {
            if d.len() < 5 {
                return Err(Error::Incomplete);
            }
            if d[4] != 0x16 {
                return Err(Error::Framing);
            }
            if d[1].wrapping_add(d[2]) != d[3] {
                return Err(Error::Checksum);
            }
            Ok((Frame::Short { control: d[1], address: d[2] }, 5))
        }
        Some(0x68) => {
            if d.len() < 4 {
                return Err(Error::Incomplete);
            }
            let len = usize::from(d[1]);
            if d[1] != d[2] || d[3] != 0x68 || len < 3 {
                return Err(Error::Framing);
            }
            if d.len() < len + 6 {
                return Err(Error::Incomplete);
            }
            let body = &d[4..4 + len];
            if d[5 + len] != 0x16 {
                return Err(Error::Framing);
            }
            if body.iter().fold(0u8, |a, b| a.wrapping_add(*b)) != d[4 + len] {
                return Err(Error::Checksum);
            }
            Ok((Frame::Long { control: body[0], address: body[1], ci: body[2], data: body[3..].to_vec() }, len + 6))
        }
        Some(_) => Err(Error::Framing),
    }
}

/// The structure of one data record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Record {
    /// DIF.
    pub dif: u8,
    /// DIFEs.
    pub dife: Vec<u8>,
    /// VIF.
    pub vif: u8,
    /// VIFEs.
    pub vife: Vec<u8>,
    /// Raw value bytes.
    pub raw: Vec<u8>,
    /// Storage number.
    pub storage: u64,
    /// Tariff.
    pub tariff: u32,
    /// Sub-unit.
    pub subunit: u32,
    /// Integer value (signed little-endian, or BCD), without the VIF's power of ten.
    pub value: i64,
    /// Offset of the DIF within the record area.
    pub offset: usize,
}

/// Parses the records after the 12-byte header (stops at 0x0F/0x1F, skips 0x2F). Never panics.
pub fn parse_records(d: &[u8]) -> Result<Vec<Record>, Error> {
    let mut out = Vec::new();
    let mut pos = 0;
    let byte = |pos: &mut usize| -> Result<u8, Error> {
        let b = *d.get(*pos).ok_or(Error::Record("truncated"))?;
        *pos += 1;
        Ok(b)
    };
    while pos < d.len() {
        let start = pos;
        let dif = byte(&mut pos)?;
        if dif == 0x2F {
            continue;
        }
        if dif & 0x0F == 0x0F {
            break;
        }
        let mut dife = Vec::new();
        let mut last = dif;
        while last & 0x80 != 0 {
            last = byte(&mut pos)?;
            dife.push(last);
            if dife.len() > 10 {
                return Err(Error::Record("too many DIFEs"));
            }
        }
        let vif = byte(&mut pos)?;
        let mut vife = Vec::new();
        last = vif;
        while last & 0x80 != 0 {
            last = byte(&mut pos)?;
            vife.push(last);
            if vife.len() > 10 {
                return Err(Error::Record("too many VIFEs"));
            }
        }
        if vif & 0x7F == 0x7C {
            let n = usize::from(byte(&mut pos)?);
            if pos + n > d.len() {
                return Err(Error::Record("truncated plain-text unit"));
            }
            pos += n;
        }
        let field = dif & 0x0F;
        let size = match field {
            0 | 8 => 0,
            1 | 9 => 1,
            2 | 0xA => 2,
            3 | 0xB => 3,
            4 | 5 | 0xC => 4,
            6 | 0xE => 6,
            7 => 8,
            0xD => {
                let l = byte(&mut pos)?;
                match l {
                    0..=0xBF => usize::from(l),
                    0xC0..=0xCF => usize::from(l - 0xC0),
                    0xD0..=0xDF => usize::from(l - 0xD0),
                    _ => return Err(Error::Record("unsupported LVAR")),
                }
            }
            _ => return Err(Error::Record("unsupported data field")),
        };
        if pos + size > d.len() {
            return Err(Error::Record("truncated value"));
        }
        let raw = d[pos..pos + size].to_vec();
        pos += size;
        let mut storage = u64::from((dif >> 6) & 1);
        let (mut tariff, mut subunit) = (0u32, 0u32);
        for (i, e) in dife.iter().enumerate() {
            let shift = 1 + 4 * i as u32;
            if shift < 60 {
                storage |= u64::from(e & 0x0F) << shift;
            }
            if 2 * i < 30 {
                tariff |= u32::from((e >> 4) & 3) << (2 * i);
            }
            if i < 31 {
                subunit |= u32::from((e >> 6) & 1) << i;
            }
        }
        let value = if matches!(field, 9 | 0xA | 0xB | 0xC | 0xE) {
            raw.iter().rev().fold(0i64, |acc, b| acc * 100 + i64::from((b >> 4).min(9)) * 10 + i64::from((b & 0x0F).min(9)))
        } else if raw.is_empty() || field == 0xD {
            0
        } else {
            let mut v: i64 = 0;
            for &b in raw.iter().rev() {
                v = (v << 8) | i64::from(b);
            }
            let bits = 8 * raw.len();
            if bits < 64 && v & (1 << (bits - 1)) != 0 {
                v -= 1 << bits;
            }
            v
        };
        out.push(Record { dif, dife, vif, vife, raw, storage, tariff, subunit, value, offset: start });
    }
    Ok(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hex(s: &str) -> Vec<u8> {
        (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
    }

    fn up(b: &[u8]) -> String {
        b.iter().map(|x| format!("{x:02X}")).collect()
    }

    fn field(obj: &str, key: &str) -> String {
        let pat = format!("\"{key}\": \"");
        let Some(start) = obj.find(&pat).map(|i| i + pat.len()) else { return String::new() };
        let end = obj[start..].find('"').map_or(start, |i| i + start);
        obj[start..end].to_string()
    }

    /// Runs the shared vectors in /conformance/mbus.json.
    #[test]
    fn conformance_vectors() {
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../../conformance/mbus.json");
        let text = std::fs::read_to_string(path).expect("conformance/mbus.json");
        let mut checked = 0;
        for obj in text.split("\n  {").skip(1) {
            let name = field(obj, "name");
            let wire = hex(&field(obj, "wire"));
            if obj.contains("\"valid\": false") {
                assert!(read_frame(&wire).map(|(_, n)| n != wire.len()).unwrap_or(true), "{name} should be rejected");
                checked += 1;
                continue;
            }
            let (frame, n) = read_frame(&wire).unwrap_or_else(|e| panic!("{name}: {e}"));
            assert_eq!(n, wire.len(), "{name}");
            assert_eq!(frame.encode(), wire, "{name}: re-encode");
            if field(obj, "kind") == "long" {
                let Frame::Long { data, .. } = frame else { panic!("{name}: not long") };
                let records = parse_records(&data[12..]).unwrap_or_else(|e| panic!("{name}: {e}"));
                let rendered: Vec<String> = records
                    .iter()
                    .map(|r| format!("{:02X}|{}|{:02X}|{}|{}|{}|{}|{}|{}|{}", r.dif, up(&r.dife), r.vif, up(&r.vife), up(&r.raw), r.storage, r.tariff, r.subunit, r.value, r.offset))
                    .collect();
                let start = obj.find("\"records\": [").expect("records") + 12;
                let end = obj[start..].find(']').expect("]") + start;
                let expected: Vec<String> = obj[start..end].split('"').skip(1).step_by(2).map(str::to_string).collect();
                assert_eq!(rendered, expected, "{name}");
            }
            checked += 1;
        }
        assert!(checked >= 11, "checked {checked}");
    }
}
