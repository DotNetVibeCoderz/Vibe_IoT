//! DLMS/COSEM codecs: HDLC frames of format type 3 (IEC 62056-46: addresses, control, HCS/FCS) and A-XDR data
//! (IEC 62056-6-2 "Data"). The C# runtime (`IoTCom.Net.Protocols.Dlms`) is managed; this crate is its twin, kept in
//! sync by `/conformance/dlms.json` and fuzzed with `cargo fuzz run dlms`.
#![forbid(unsafe_code)]

use core::fmt;
use iotcom_core::crc::crc16_x25;

/// Why input was rejected.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Error {
    /// More bytes are needed.
    Incomplete,
    /// Not a type-3 frame, or no closing flag where the length says.
    Framing,
    /// Header checksum mismatch.
    Hcs,
    /// Frame checksum mismatch.
    Fcs,
    /// Invalid HDLC address.
    Address,
    /// Malformed A-XDR data.
    Data(&'static str),
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Incomplete => f.write_str("incomplete"),
            Self::Framing => f.write_str("bad HDLC framing"),
            Self::Hcs => f.write_str("HCS mismatch"),
            Self::Fcs => f.write_str("FCS mismatch"),
            Self::Address => f.write_str("bad HDLC address"),
            Self::Data(m) => write!(f, "bad A-XDR data: {m}"),
        }
    }
}

impl std::error::Error for Error {}

/// An HDLC address: upper (client SAP or logical device) and optional lower (physical device), with its encoded size.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Address {
    /// Upper address.
    pub upper: u16,
    /// Lower address.
    pub lower: Option<u16>,
    /// Encoded size: 1, 2 or 4.
    pub size: u8,
}

impl Address {
    fn read(d: &[u8], pos: &mut usize) -> Result<Self, Error> {
        let start = *pos;
        while *pos < d.len() && d[*pos] & 1 == 0 {
            *pos += 1;
        }
        if *pos >= d.len() {
            return Err(Error::Address);
        }
        *pos += 1;
        let b = &d[start..*pos];
        let h = |x: u8| u16::from(x >> 1);
        Ok(match b.len() {
            1 => Self { upper: h(b[0]), lower: None, size: 1 },
            2 => Self { upper: h(b[0]), lower: Some(h(b[1])), size: 2 },
            4 => Self { upper: (h(b[0]) << 7) | h(b[1]), lower: Some((h(b[2]) << 7) | h(b[3])), size: 4 },
            _ => return Err(Error::Address),
        })
    }

    fn write(&self, out: &mut Vec<u8>) {
        let lower = self.lower.unwrap_or(0);
        match self.size {
            1 => out.push(((self.upper << 1) | 1) as u8),
            2 => out.extend_from_slice(&[(self.upper << 1) as u8, ((lower << 1) | 1) as u8]),
            _ => out.extend_from_slice(&[
                ((self.upper >> 7) << 1) as u8,
                ((self.upper & 0x7F) << 1) as u8,
                ((lower >> 7) << 1) as u8,
                (((lower & 0x7F) << 1) | 1) as u8,
            ]),
        }
    }
}

impl fmt::Display for Address {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self.lower {
            Some(l) => write!(f, "{}/{}", self.upper, l),
            None => write!(f, "{}", self.upper),
        }
    }
}

/// An HDLC frame.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Frame {
    /// Destination.
    pub dest: Address,
    /// Source.
    pub src: Address,
    /// Control field.
    pub control: u8,
    /// Information field.
    pub info: Vec<u8>,
    /// Segmentation bit.
    pub segmented: bool,
}

impl Frame {
    /// Encodes the frame with both flags.
    pub fn encode(&self) -> Vec<u8> {
        let mut body = vec![0, 0];
        self.dest.write(&mut body);
        self.src.write(&mut body);
        body.push(self.control);
        let length = body.len() + if self.info.is_empty() { 0 } else { 2 + self.info.len() } + 2;
        body[0] = 0xA0 | if self.segmented { 0x08 } else { 0 } | ((length >> 8) & 0x07) as u8;
        body[1] = (length & 0xFF) as u8;
        if !self.info.is_empty() {
            let hcs = crc16_x25(&body);
            body.extend_from_slice(&hcs.to_le_bytes());
            body.extend_from_slice(&self.info);
        }
        let fcs = crc16_x25(&body);
        body.extend_from_slice(&fcs.to_le_bytes());
        let mut out = Vec::with_capacity(body.len() + 2);
        out.push(0x7E);
        out.extend_from_slice(&body);
        out.push(0x7E);
        out
    }
}

/// Decodes one frame that starts with a flag at `data[0]`; returns the frame and the bytes consumed. Never panics.
pub fn read_frame(data: &[u8]) -> Result<(Frame, usize), Error> {
    if data.len() < 3 {
        return Err(Error::Incomplete);
    }
    if data[0] != 0x7E || data[1] & 0xF0 != 0xA0 {
        return Err(Error::Framing);
    }
    let length = (usize::from(data[1] & 0x07) << 8) | usize::from(data[2]);
    if data.len() < length + 2 {
        return Err(Error::Incomplete);
    }
    if data[length + 1] != 0x7E || length < 7 {
        return Err(Error::Framing);
    }
    let body = &data[1..=length];
    let fcs = crc16_x25(&body[..length - 2]).to_le_bytes();
    if body[length - 2..] != fcs {
        return Err(Error::Fcs);
    }
    let mut pos = 2;
    let dest = Address::read(body, &mut pos)?;
    let src = Address::read(body, &mut pos)?;
    if pos >= length - 2 {
        return Err(Error::Framing);
    }
    let control = body[pos];
    pos += 1;
    let mut info = Vec::new();
    if length - 2 > pos {
        if pos + 2 > length - 2 {
            return Err(Error::Framing);
        }
        let hcs = crc16_x25(&body[..pos]).to_le_bytes();
        if body[pos..pos + 2] != hcs {
            return Err(Error::Hcs);
        }
        info = body[pos + 2..length - 2].to_vec();
    }
    Ok((Frame { dest, src, control, info, segmented: body[0] & 0x08 != 0 }, length + 2))
}

/// An A-XDR value.
#[derive(Debug, Clone, PartialEq)]
pub enum Data {
    /// null-data.
    Null,
    /// array.
    Array(Vec<Data>),
    /// structure.
    Structure(Vec<Data>),
    /// boolean.
    Boolean(bool),
    /// bit-string: bit count and bytes.
    BitString(usize, Vec<u8>),
    /// Integer types with their tag (int8 15, int16 16, int32 5, int64 20, uint8 17, uint16 18, uint32 6, uint64 21, enum 22, bcd 13).
    Integer(u8, i128),
    /// float32 bits.
    Float32(u32),
    /// float64 bits.
    Float64(u64),
    /// octet-string (9), visible-string (10) or utf8-string (12).
    Bytes(u8, Vec<u8>),
    /// date-time (25), date (26) or time (27).
    Fixed(u8, Vec<u8>),
}

fn int_size(tag: u8) -> Option<(usize, bool)> {
    Some(match tag {
        15 => (1, true),
        16 => (2, true),
        5 => (4, true),
        20 => (8, true),
        17 | 22 | 13 => (1, false),
        18 => (2, false),
        6 => (4, false),
        21 => (8, false),
        _ => return None,
    })
}

fn read_len(d: &[u8], pos: &mut usize) -> Result<usize, Error> {
    let first = *d.get(*pos).ok_or(Error::Data("truncated length"))?;
    *pos += 1;
    if first < 0x80 {
        return Ok(usize::from(first));
    }
    let n = usize::from(first & 0x7F);
    if n == 0 || n > 4 || *pos + n > d.len() {
        return Err(Error::Data("invalid length"));
    }
    let mut v = 0usize;
    for _ in 0..n {
        v = (v << 8) | usize::from(d[*pos]);
        *pos += 1;
    }
    Ok(v)
}

fn write_len(out: &mut Vec<u8>, n: usize) {
    if n < 0x80 {
        out.push(n as u8);
    } else if n <= 0xFF {
        out.extend_from_slice(&[0x81, n as u8]);
    } else if n <= 0xFFFF {
        out.extend_from_slice(&[0x82, (n >> 8) as u8, n as u8]);
    } else {
        out.push(0x84);
        out.extend_from_slice(&(n as u32).to_be_bytes());
    }
}

fn take<'a>(d: &'a [u8], pos: &mut usize, n: usize) -> Result<&'a [u8], Error> {
    if *pos + n > d.len() {
        return Err(Error::Data("truncated value"));
    }
    let s = &d[*pos..*pos + n];
    *pos += n;
    Ok(s)
}

/// Decodes one value at the start of `d`; returns it and the bytes consumed. Never panics.
pub fn decode_data(d: &[u8]) -> Result<(Data, usize), Error> {
    let mut pos = 0;
    let v = read(d, &mut pos, 0)?;
    Ok((v, pos))
}

fn read(d: &[u8], pos: &mut usize, depth: usize) -> Result<Data, Error> {
    if depth > 32 {
        return Err(Error::Data("nested too deep"));
    }
    let tag = *d.get(*pos).ok_or(Error::Data("missing tag"))?;
    *pos += 1;
    Ok(match tag {
        0 => Data::Null,
        1 | 2 => {
            let n = read_len(d, pos)?;
            if n > d.len() - *pos {
                return Err(Error::Data("too many items"));
            }
            let mut items = Vec::with_capacity(n);
            for _ in 0..n {
                items.push(read(d, pos, depth + 1)?);
            }
            if tag == 1 { Data::Array(items) } else { Data::Structure(items) }
        }
        3 => Data::Boolean(take(d, pos, 1)?[0] != 0),
        4 => {
            let bits = read_len(d, pos)?;
            Data::BitString(bits, take(d, pos, bits.div_ceil(8))?.to_vec())
        }
        9 | 10 | 12 => {
            let n = read_len(d, pos)?;
            Data::Bytes(tag, take(d, pos, n)?.to_vec())
        }
        25 => Data::Fixed(tag, take(d, pos, 12)?.to_vec()),
        26 => Data::Fixed(tag, take(d, pos, 5)?.to_vec()),
        27 => Data::Fixed(tag, take(d, pos, 4)?.to_vec()),
        23 => Data::Float32(u32::from_be_bytes(take(d, pos, 4)?.try_into().expect("4 bytes"))),
        24 => Data::Float64(u64::from_be_bytes(take(d, pos, 8)?.try_into().expect("8 bytes"))),
        _ => {
            let (size, signed) = int_size(tag).ok_or(Error::Data("unknown tag"))?;
            let raw = take(d, pos, size)?;
            let mut v: i128 = 0;
            for &b in raw {
                v = (v << 8) | i128::from(b);
            }
            if signed && raw[0] & 0x80 != 0 {
                v -= 1i128 << (8 * size);
            }
            Data::Integer(tag, v)
        }
    })
}

impl Data {
    /// Encodes the value.
    pub fn encode(&self) -> Vec<u8> {
        let mut out = Vec::new();
        self.write(&mut out);
        out
    }

    fn write(&self, out: &mut Vec<u8>) {
        match self {
            Data::Null => out.push(0),
            Data::Array(items) | Data::Structure(items) => {
                out.push(if matches!(self, Data::Array(_)) { 1 } else { 2 });
                write_len(out, items.len());
                for i in items {
                    i.write(out);
                }
            }
            Data::Boolean(b) => out.extend_from_slice(&[3, if *b { 0xFF } else { 0 }]),
            Data::BitString(bits, raw) => {
                out.push(4);
                write_len(out, *bits);
                out.extend_from_slice(raw);
            }
            Data::Integer(tag, v) => {
                out.push(*tag);
                let (size, _) = int_size(*tag).unwrap_or((1, false));
                let bytes = v.to_be_bytes();
                out.extend_from_slice(&bytes[16 - size..]);
            }
            Data::Float32(bits) => {
                out.push(23);
                out.extend_from_slice(&bits.to_be_bytes());
            }
            Data::Float64(bits) => {
                out.push(24);
                out.extend_from_slice(&bits.to_be_bytes());
            }
            Data::Bytes(tag, raw) => {
                out.push(*tag);
                write_len(out, raw.len());
                out.extend_from_slice(raw);
            }
            Data::Fixed(tag, raw) => {
                out.push(*tag);
                out.extend_from_slice(raw);
            }
        }
    }

    /// The canonical text form used by the shared vectors.
    pub fn canonical(&self) -> String {
        fn hex(b: &[u8]) -> String {
            b.iter().map(|x| format!("{x:02X}")).collect()
        }
        match self {
            Data::Null => "null".into(),
            Data::Array(i) => format!("array[{}]", i.iter().map(Data::canonical).collect::<Vec<_>>().join(",")),
            Data::Structure(i) => format!("struct[{}]", i.iter().map(Data::canonical).collect::<Vec<_>>().join(",")),
            Data::Boolean(b) => format!("bool:{b}"),
            Data::BitString(n, raw) => format!("bits:{n}:{}", hex(raw)),
            Data::Integer(tag, v) => {
                let name = match tag {
                    15 => "i8",
                    16 => "i16",
                    5 => "i32",
                    20 => "i64",
                    17 => "u8",
                    18 => "u16",
                    6 => "u32",
                    21 => "u64",
                    22 => "enum",
                    _ => "bcd",
                };
                format!("{name}:{v}")
            }
            Data::Float32(bits) => format!("f32:{bits:08X}"),
            Data::Float64(bits) => format!("f64:{bits:016X}"),
            Data::Bytes(tag, raw) => format!("{}:{}", match tag { 9 => "octets", 10 => "vis", _ => "utf8" }, hex(raw)),
            Data::Fixed(tag, raw) => format!("{}:{}", match tag { 25 => "dt", 26 => "date", _ => "time" }, hex(raw)),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hex(s: &str) -> Vec<u8> {
        (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
    }

    fn field(obj: &str, key: &str) -> String {
        let pat = format!("\"{key}\": \"");
        let Some(start) = obj.find(&pat).map(|i| i + pat.len()) else { return String::new() };
        let end = obj[start..].find('"').map_or(start, |i| i + start);
        obj[start..end].to_string()
    }

    fn num(obj: &str, key: &str) -> u32 {
        let pat = format!("\"{key}\": ");
        let start = obj.find(&pat).map(|i| i + pat.len()).expect(key);
        obj[start..].chars().take_while(char::is_ascii_digit).collect::<String>().parse().unwrap()
    }

    /// Runs the shared vectors in /conformance/dlms.json.
    #[test]
    fn conformance_vectors() {
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../../conformance/dlms.json");
        let text = std::fs::read_to_string(path).expect("conformance/dlms.json");
        let mut checked = 0;
        for obj in text.split("\n  {").skip(1) {
            let name = field(obj, "name");
            let wire = hex(&field(obj, "wire"));
            let valid = obj.contains("\"valid\": true");
            if field(obj, "kind") == "hdlc" {
                let r = read_frame(&wire);
                if !valid {
                    assert!(r.is_err(), "{name} should be rejected");
                } else {
                    let (f, n) = r.unwrap_or_else(|e| panic!("{name}: {e}"));
                    assert_eq!(n, wire.len(), "{name}");
                    assert_eq!(u32::from(f.control), num(obj, "control"), "{name}");
                    assert_eq!(f.dest.to_string(), field(obj, "dest"), "{name}");
                    assert_eq!(f.src.to_string(), field(obj, "src"), "{name}");
                    assert_eq!(f.info, hex(&field(obj, "info")), "{name}");
                    assert_eq!(f.encode(), wire, "{name}: re-encode");
                }
            } else {
                let r = decode_data(&wire);
                if !valid {
                    assert!(r.is_err() || r.as_ref().is_ok_and(|(_, n)| *n != wire.len()), "{name} should be rejected");
                } else {
                    let (v, n) = r.unwrap_or_else(|e| panic!("{name}: {e}"));
                    assert_eq!(n, wire.len(), "{name}");
                    assert_eq!(v.canonical(), field(obj, "canonical"), "{name}");
                    assert_eq!(v.encode(), wire, "{name}: re-encode");
                }
            }
            checked += 1;
        }
        assert!(checked >= 28, "checked {checked}");
    }
}
