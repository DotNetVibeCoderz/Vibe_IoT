//! Modbus ADU framing: TCP (MBAP), RTU (CRC-16/MODBUS) and ASCII (LRC).

use iotcom_core::crc::{crc16_modbus, lrc};

/// Framing variant.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum Framing {
    /// Modbus TCP.
    Tcp = 0,
    /// Modbus RTU (serial or RTU-over-TCP).
    Rtu = 1,
    /// Modbus ASCII.
    Ascii = 2,
}

impl Framing {
    /// Converts from the FFI byte.
    pub fn from_u8(v: u8) -> Option<Self> {
        match v {
            0 => Some(Self::Tcp),
            1 => Some(Self::Rtu),
            2 => Some(Self::Ascii),
            _ => None,
        }
    }

    /// True when frames carry transaction ids.
    pub fn has_transaction_ids(self) -> bool {
        self == Self::Tcp
    }
}

/// A decoded application data unit.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Adu {
    /// Transaction id (0 for RTU/ASCII).
    pub transaction_id: u16,
    /// Unit id.
    pub unit_id: u8,
    /// Function code + data.
    pub pdu: Vec<u8>,
}

/// Outcome of a decode attempt.
#[derive(Debug, PartialEq, Eq)]
pub enum Decode {
    /// More bytes needed; nothing consumed.
    NeedMore,
    /// A frame was decoded; `usize` bytes were consumed.
    Frame(Adu, usize),
    /// Garbage or a corrupt frame; skip `usize` bytes and retry.
    Skip(usize),
}

/// Appends an encoded ADU to `out`.
pub fn encode(framing: Framing, transaction_id: u16, unit_id: u8, pdu: &[u8], out: &mut Vec<u8>) {
    match framing {
        Framing::Tcp => {
            out.extend_from_slice(&transaction_id.to_be_bytes());
            out.extend_from_slice(&[0, 0]);
            out.extend_from_slice(&((pdu.len() + 1) as u16).to_be_bytes());
            out.push(unit_id);
            out.extend_from_slice(pdu);
        }
        Framing::Rtu => {
            let start = out.len();
            out.push(unit_id);
            out.extend_from_slice(pdu);
            let crc = crc16_modbus(&out[start..]);
            out.extend_from_slice(&crc.to_le_bytes());
        }
        Framing::Ascii => {
            const DIGITS: &[u8; 16] = b"0123456789ABCDEF";
            let mut bin = Vec::with_capacity(pdu.len() + 2);
            bin.push(unit_id);
            bin.extend_from_slice(pdu);
            bin.push(lrc(&bin));
            out.push(b':');
            for b in bin {
                out.push(DIGITS[(b >> 4) as usize]);
                out.push(DIGITS[(b & 0xF) as usize]);
            }
            out.extend_from_slice(b"\r\n");
        }
    }
}

/// Tries to decode one ADU from the start of `buf`.
pub fn decode(framing: Framing, buf: &[u8], expect_request: bool) -> Decode {
    match framing {
        Framing::Tcp => decode_tcp(buf),
        Framing::Rtu => decode_rtu(buf, expect_request),
        Framing::Ascii => decode_ascii(buf),
    }
}

fn decode_tcp(buf: &[u8]) -> Decode {
    if buf.len() < 7 {
        return Decode::NeedMore;
    }
    let tid = u16::from_be_bytes([buf[0], buf[1]]);
    let pid = u16::from_be_bytes([buf[2], buf[3]]);
    let len = u16::from_be_bytes([buf[4], buf[5]]) as usize;
    if pid != 0 || !(2..=254).contains(&len) {
        return Decode::Skip(1);
    }
    let total = 6 + len;
    if buf.len() < total {
        return Decode::NeedMore;
    }
    Decode::Frame(Adu { transaction_id: tid, unit_id: buf[6], pdu: buf[7..total].to_vec() }, total)
}

enum Len {
    NeedMore,
    Unknown,
    Bytes(usize),
}

fn rtu_response_len(f: &[u8]) -> Len {
    if f.len() < 3 {
        return Len::NeedMore;
    }
    let fc = f[1];
    if fc & 0x80 != 0 {
        return Len::Bytes(5);
    }
    match fc {
        0x01..=0x04 | 0x17 => Len::Bytes(3 + f[2] as usize + 2),
        0x05 | 0x06 | 0x0F | 0x10 => Len::Bytes(8),
        0x16 => Len::Bytes(10),
        0x2B => {
            if f.len() < 8 {
                return Len::NeedMore;
            }
            let mut p = 8;
            for _ in 0..f[7] {
                if f.len() < p + 2 {
                    return Len::NeedMore;
                }
                p += 2 + f[p + 1] as usize;
            }
            Len::Bytes(p + 2)
        }
        _ => Len::Unknown,
    }
}

fn rtu_request_len(f: &[u8]) -> Len {
    if f.len() < 2 {
        return Len::NeedMore;
    }
    match f[1] {
        0x01..=0x06 => Len::Bytes(8),
        0x0F | 0x10 if f.len() < 7 => Len::NeedMore,
        0x0F | 0x10 => Len::Bytes(7 + f[6] as usize + 2),
        0x16 => Len::Bytes(10),
        0x17 if f.len() < 11 => Len::NeedMore,
        0x17 => Len::Bytes(11 + f[10] as usize + 2),
        0x2B => Len::Bytes(7),
        _ => Len::Unknown,
    }
}

fn decode_rtu(buf: &[u8], expect_request: bool) -> Decode {
    if buf.len() < 4 {
        return Decode::NeedMore;
    }
    let len = match if expect_request { rtu_request_len(buf) } else { rtu_response_len(buf) } {
        Len::NeedMore => return Decode::NeedMore,
        Len::Unknown => return Decode::Skip(1),
        Len::Bytes(n) if n > 256 => return Decode::Skip(1),
        Len::Bytes(n) => n,
    };
    if buf.len() < len {
        return Decode::NeedMore;
    }
    let crc = u16::from_le_bytes([buf[len - 2], buf[len - 1]]);
    if crc16_modbus(&buf[..len - 2]) != crc {
        return Decode::Skip(1);
    }
    Decode::Frame(Adu { transaction_id: 0, unit_id: buf[0], pdu: buf[1..len - 2].to_vec() }, len)
}

fn decode_ascii(buf: &[u8]) -> Decode {
    let Some(start) = buf.iter().position(|&b| b == b':') else {
        return if buf.is_empty() { Decode::NeedMore } else { Decode::Skip(buf.len()) };
    };
    if start > 0 {
        return Decode::Skip(start);
    }
    let Some(nl) = buf.iter().position(|&b| b == b'\n') else {
        return if buf.len() > 1 + 512 + 2 { Decode::Skip(1) } else { Decode::NeedMore };
    };
    let consumed = nl + 1;
    let mut text = &buf[1..nl];
    if text.last() == Some(&b'\r') {
        text = &text[..text.len() - 1];
    }
    if text.len() < 6 || text.len() % 2 != 0 {
        return Decode::Skip(consumed);
    }
    let mut bin = Vec::with_capacity(text.len() / 2);
    for pair in text.chunks_exact(2) {
        match (hex(pair[0]), hex(pair[1])) {
            (Some(h), Some(l)) => bin.push(h << 4 | l),
            _ => return Decode::Skip(consumed),
        }
    }
    let (body, check) = bin.split_at(bin.len() - 1);
    if lrc(body) != check[0] {
        return Decode::Skip(consumed);
    }
    Decode::Frame(Adu { transaction_id: 0, unit_id: body[0], pdu: body[1..].to_vec() }, consumed)
}

fn hex(c: u8) -> Option<u8> {
    match c {
        b'0'..=b'9' => Some(c - b'0'),
        b'A'..=b'F' => Some(c - b'A' + 10),
        b'a'..=b'f' => Some(c - b'a' + 10),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn unhex(s: &str) -> Vec<u8> {
        (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
    }

    /// Runs the shared vectors in /conformance/modbus.json (also run by the C# suite).
    #[test]
    fn conformance_vectors() {
        let text = std::fs::read_to_string(concat!(env!("CARGO_MANIFEST_DIR"), "/../../../conformance/modbus.json")).unwrap();
        let field = |obj: &str, key: &str| -> String {
            let pat = format!("\"{key}\": ");
            let start = obj.find(&pat).unwrap() + pat.len();
            let rest = &obj[start..];
            let end = rest.find([',', '\n', '}']).unwrap();
            rest[..end].trim().trim_matches('"').to_string()
        };
        let mut n = 0;
        for obj in text.split('{').skip(1) {
            let framing = match field(obj, "mode").as_str() {
                "tcp" => Framing::Tcp,
                "rtu" => Framing::Rtu,
                _ => Framing::Ascii,
            };
            let tid: u16 = field(obj, "transactionId").parse().unwrap();
            let unit: u8 = field(obj, "unitId").parse().unwrap();
            let pdu = unhex(&field(obj, "pdu"));
            let adu = unhex(&field(obj, "adu"));
            let mut out = Vec::new();
            encode(framing, tid, unit, &pdu, &mut out);
            assert_eq!(out, adu, "encode {}", field(obj, "name"));
            match decode(framing, &adu, field(obj, "direction") == "request") {
                Decode::Frame(a, used) => {
                    assert_eq!(used, adu.len());
                    assert_eq!((a.transaction_id, a.unit_id, a.pdu), (tid, unit, pdu));
                }
                other => panic!("decode {} -> {other:?}", field(obj, "name")),
            }
            n += 1;
        }
        assert!(n >= 10);
    }

    #[test]
    fn rtu_resyncs_after_noise() {
        let mut wire = vec![0x55, 0xAA];
        encode(Framing::Rtu, 0, 1, &[0x03, 0, 0, 0, 2], &mut wire);
        let mut offset = 0;
        loop {
            match decode(Framing::Rtu, &wire[offset..], true) {
                Decode::Skip(n) => offset += n,
                Decode::Frame(adu, _) => {
                    assert_eq!(adu.unit_id, 1);
                    break;
                }
                Decode::NeedMore => panic!("should find the frame"),
            }
        }
    }

    #[test]
    fn decoder_never_panics_on_garbage() {
        // Cheap deterministic fuzz; the real fuzzing lives in rust/fuzz (cargo-fuzz).
        let mut seed = 0x1234_5678u32;
        for _ in 0..20_000 {
            let len = (seed % 40) as usize;
            let buf: Vec<u8> = (0..len)
                .map(|_| {
                    seed ^= seed << 13;
                    seed ^= seed >> 17;
                    seed ^= seed << 5;
                    seed as u8
                })
                .collect();
            for f in [Framing::Tcp, Framing::Rtu, Framing::Ascii] {
                let _ = decode(f, &buf, true);
                let _ = decode(f, &buf, false);
            }
        }
    }
}
