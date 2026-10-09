//! IEC 60870-5-104 codec: APCI (I/S/U frames), ASDU header, the information elements of the common monitoring,
//! command and system types, and CP56Time2a.
//!
//! This is the fuzzed twin of `IoTCom.Net.Protocols.Iec104` (C#). Both decode the same bytes to the same values,
//! kept in sync by `/conformance/iec104.json` and fuzzed with `cargo fuzz run iec104`. Quality flags use the C#
//! normalisation: OV 0x01, transient 0x02, carry 0x04, adjusted 0x08, BL 0x10, SB 0x20, NT 0x40, IV 0x80.
#![forbid(unsafe_code)]

/// Errors from decoding.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Error {
    /// Malformed APCI (start, length or control field).
    Apci(&'static str),
    /// Type identification this codec does not know.
    UnknownType(u8),
    /// ASDU length disagrees with the number of objects.
    Length,
    /// CP56Time2a out of range.
    Time,
}

/// Seven-octet binary time.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Cp56 {
    /// Year 2000–2099.
    pub year: u16,
    /// Month 1–12.
    pub month: u8,
    /// Day 1–31.
    pub day: u8,
    /// Hour 0–23.
    pub hour: u8,
    /// Minute 0–59.
    pub minute: u8,
    /// Milliseconds within the minute, 0–59999.
    pub millis: u16,
    /// IV bit.
    pub invalid: bool,
    /// SU bit.
    pub summer: bool,
}

fn days_in_month(year: u16, month: u8) -> u8 {
    match month {
        2 if year % 4 == 0 && (year % 100 != 0 || year % 400 == 0) => 29,
        2 => 28,
        4 | 6 | 9 | 11 => 30,
        _ => 31,
    }
}

impl Cp56 {
    /// Decodes 7 octets.
    pub fn decode(d: &[u8]) -> Result<Self, Error> {
        let t = Cp56 {
            millis: u16::from_le_bytes([d[0], d[1]]),
            minute: d[2] & 0x3F,
            invalid: d[2] & 0x80 != 0,
            hour: d[3] & 0x1F,
            summer: d[3] & 0x80 != 0,
            day: d[4] & 0x1F,
            month: d[5] & 0x0F,
            year: 2000 + u16::from(d[6] & 0x7F),
        };
        if t.millis > 59_999 || t.minute > 59 || t.hour > 23 || t.day == 0 || !(1..=12).contains(&t.month) || t.day > days_in_month(t.year, t.month) {
            return Err(Error::Time);
        }
        Ok(t)
    }

    /// Encodes 7 octets (day of week 0).
    pub fn encode(&self) -> [u8; 7] {
        let ms = self.millis.to_le_bytes();
        [
            ms[0],
            ms[1],
            self.minute | if self.invalid { 0x80 } else { 0 },
            self.hour | if self.summer { 0x80 } else { 0 },
            self.day,
            self.month,
            (self.year % 100) as u8,
        ]
    }
}

/// One information object.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Object {
    /// Information object address (24 bits).
    pub ioa: u32,
    /// Element value (see the C# `Iec104Object`).
    pub value: f64,
    /// Normalised quality flags.
    pub quality: u16,
    /// Qualifier octet (command QU/S-E, QOS, QOI, QCC, COI, QRP, counter sequence).
    pub qualifier: u8,
    /// CP56Time2a, for time-tagged types and clock sync.
    pub time: Option<Cp56>,
}

/// An ASDU.
#[derive(Debug, Clone, PartialEq)]
pub struct Asdu {
    /// Type identification.
    pub type_id: u8,
    /// SQ bit.
    pub sequence: bool,
    /// Cause of transmission (6 bits).
    pub cause: u8,
    /// P/N bit.
    pub negative: bool,
    /// T bit.
    pub test: bool,
    /// Originator address.
    pub originator: u8,
    /// Common address.
    pub common_address: u16,
    /// Objects.
    pub objects: Vec<Object>,
}

/// An APDU.
#[derive(Debug, Clone, PartialEq)]
pub enum Apdu {
    /// Information transfer: N(S), N(R), ASDU octets.
    I(u16, u16, Vec<u8>),
    /// Supervisory: N(R).
    S(u16),
    /// Unnumbered: function octet.
    U(u8),
}

/// Whether the type carries a CP56Time2a.
pub fn has_time(t: u8) -> bool {
    matches!(t, 30..=37 | 58..=64 | 103 | 107)
}

fn base_type(t: u8) -> u8 {
    match t {
        30..=37 => (t - 30) * 2 + 1,
        58..=64 => t - 13,
        _ => t,
    }
}

/// Element size after the IOA (with quality and time), or `None` for unknown types.
pub fn element_size(t: u8) -> Option<usize> {
    let base = match base_type(t) {
        1 | 3 | 45 | 46 | 47 | 70 | 100 | 101 | 105 => 1,
        5 | 21 => 2,
        9 | 11 | 48 | 49 => 3,
        51 => 4,
        7 | 13 | 15 | 50 => 5,
        102 | 103 => 0,
        107 => 2,
        _ => return None,
    };
    Some(base + if has_time(t) { 7 } else { 0 })
}

fn q(b: u8) -> u16 {
    u16::from(b & 0xF1)
}

fn decode_element(t: u8, ioa: u32, d: &[u8]) -> Result<Object, Error> {
    let size = element_size(t).ok_or(Error::UnknownType(t))?;
    let time = if has_time(t) { Some(Cp56::decode(&d[size - 7..size])?) } else { None };
    let i16v = |d: &[u8]| f64::from(i16::from_le_bytes([d[0], d[1]]));
    let (value, quality, qualifier) = match base_type(t) {
        1 => (f64::from(d[0] & 1), q(d[0] & 0xF0), 0),
        3 => (f64::from(d[0] & 3), q(d[0] & 0xF0), 0),
        5 => (f64::from(((d[0] << 1) as i8) >> 1), q(d[1]) | if d[0] & 0x80 != 0 { 0x02 } else { 0 }, 0),
        7 => (f64::from(u32::from_le_bytes([d[0], d[1], d[2], d[3]])), q(d[4]), 0),
        9 => (i16v(d) / 32768.0, q(d[2]), 0),
        21 => (i16v(d) / 32768.0, 0, 0),
        11 => (i16v(d), q(d[2]), 0),
        13 => (f64::from(f32::from_le_bytes([d[0], d[1], d[2], d[3]])), q(d[4]), 0),
        15 => {
            let flags = (if d[4] & 0x20 != 0 { 0x04 } else { 0 }) | (if d[4] & 0x40 != 0 { 0x08 } else { 0 }) | (if d[4] & 0x80 != 0 { 0x80 } else { 0 });
            (f64::from(i32::from_le_bytes([d[0], d[1], d[2], d[3]])), flags, d[4] & 0x1F)
        }
        45 => (f64::from(d[0] & 1), 0, d[0] & 0xFC),
        46 | 47 => (f64::from(d[0] & 3), 0, d[0] & 0xFC),
        48 => (i16v(d) / 32768.0, 0, d[2]),
        49 => (i16v(d), 0, d[2]),
        50 => (f64::from(f32::from_le_bytes([d[0], d[1], d[2], d[3]])), 0, d[4]),
        51 => (f64::from(u32::from_le_bytes([d[0], d[1], d[2], d[3]])), 0, 0),
        70 | 100 | 101 | 105 => (0.0, 0, d[0]),
        107 => (f64::from(u16::from_le_bytes([d[0], d[1]])), 0, 0),
        _ => (0.0, 0, 0),
    };
    Ok(Object { ioa, value, quality, qualifier, time })
}

fn encode_element(t: u8, o: &Object, out: &mut Vec<u8>) {
    let qb = (o.quality & 0xF1) as u8;
    let norm = |v: f64| ((v * 32768.0).round().clamp(-32768.0, 32767.0) as i16).to_le_bytes();
    let scaled = |v: f64| (v.round().clamp(-32768.0, 32767.0) as i16).to_le_bytes();
    match base_type(t) {
        1 => out.push(u8::from(o.value != 0.0) | (qb & 0xF0)),
        3 => out.push(((o.value as i64) & 3) as u8 | (qb & 0xF0)),
        5 => {
            out.push(((o.value.clamp(-64.0, 63.0) as i64) & 0x7F) as u8 | if o.quality & 0x02 != 0 { 0x80 } else { 0 });
            out.push(qb);
        }
        7 => {
            out.extend_from_slice(&(o.value as i64 as u32).to_le_bytes());
            out.push(qb);
        }
        9 => {
            out.extend_from_slice(&norm(o.value));
            out.push(qb);
        }
        21 => out.extend_from_slice(&norm(o.value)),
        11 => {
            out.extend_from_slice(&scaled(o.value));
            out.push(qb);
        }
        13 => {
            out.extend_from_slice(&(o.value as f32).to_le_bytes());
            out.push(qb);
        }
        15 => {
            out.extend_from_slice(&(o.value as i64 as i32).to_le_bytes());
            let flags = (if o.quality & 0x04 != 0 { 0x20 } else { 0 }) | (if o.quality & 0x08 != 0 { 0x40 } else { 0 }) | (if o.quality & 0x80 != 0 { 0x80 } else { 0 });
            out.push((o.qualifier & 0x1F) | flags);
        }
        45 => out.push(u8::from(o.value != 0.0) | (o.qualifier & 0xFC)),
        46 | 47 => out.push(((o.value as i64) & 3) as u8 | (o.qualifier & 0xFC)),
        48 => {
            out.extend_from_slice(&norm(o.value));
            out.push(o.qualifier);
        }
        49 => {
            out.extend_from_slice(&scaled(o.value));
            out.push(o.qualifier);
        }
        50 => {
            out.extend_from_slice(&(o.value as f32).to_le_bytes());
            out.push(o.qualifier);
        }
        51 => out.extend_from_slice(&(o.value as i64 as u32).to_le_bytes()),
        70 | 100 | 101 | 105 => out.push(o.qualifier),
        107 => out.extend_from_slice(&(o.value as i64 as u16).to_le_bytes()),
        _ => {}
    }
    if has_time(t) {
        let time = o.time.unwrap_or(Cp56 { year: 2000, month: 1, day: 1, hour: 0, minute: 0, millis: 0, invalid: true, summer: false });
        out.extend_from_slice(&time.encode());
    }
}

impl Asdu {
    /// Decodes an ASDU.
    pub fn decode(d: &[u8]) -> Result<Self, Error> {
        if d.len() < 6 {
            return Err(Error::Length);
        }
        let type_id = d[0];
        let size = element_size(type_id).ok_or(Error::UnknownType(type_id))?;
        let count = usize::from(d[1] & 0x7F);
        let sequence = d[1] & 0x80 != 0;
        if count == 0 {
            return Err(Error::Length);
        }
        let expected = 6 + if sequence { 3 + count * size } else { count * (3 + size) };
        if d.len() != expected {
            return Err(Error::Length);
        }
        let mut objects = Vec::with_capacity(count);
        let mut p = 6;
        let mut ioa = 0u32;
        for i in 0..count {
            if !sequence || i == 0 {
                ioa = u32::from_le_bytes([d[p], d[p + 1], d[p + 2], 0]);
                p += 3;
            } else {
                ioa = (ioa + 1) & 0xFF_FFFF;
            }
            objects.push(decode_element(type_id, ioa, &d[p..p + size])?);
            p += size;
        }
        Ok(Asdu {
            type_id,
            sequence,
            cause: d[2] & 0x3F,
            negative: d[2] & 0x40 != 0,
            test: d[2] & 0x80 != 0,
            originator: d[3],
            common_address: u16::from_le_bytes([d[4], d[5]]),
            objects,
        })
    }

    /// Encodes the ASDU (no length checks beyond what the format needs).
    pub fn encode(&self) -> Vec<u8> {
        let mut out = vec![
            self.type_id,
            (if self.sequence { 0x80 } else { 0 }) | (self.objects.len() as u8 & 0x7F),
            (self.cause & 0x3F) | if self.negative { 0x40 } else { 0 } | if self.test { 0x80 } else { 0 },
            self.originator,
        ];
        out.extend_from_slice(&self.common_address.to_le_bytes());
        for (i, o) in self.objects.iter().enumerate() {
            if !self.sequence || i == 0 {
                out.extend_from_slice(&o.ioa.to_le_bytes()[..3]);
            }
            encode_element(self.type_id, o, &mut out);
        }
        out
    }
}

impl Apdu {
    /// Decodes one complete APDU.
    pub fn decode(d: &[u8]) -> Result<Self, Error> {
        if d.len() < 6 || d[0] != 0x68 || usize::from(d[1]) != d.len() - 2 {
            return Err(Error::Apci("start or length"));
        }
        let c1 = d[2];
        if c1 & 1 == 0 {
            if d[4] & 1 != 0 {
                return Err(Error::Apci("I control field"));
            }
            if d.len() == 6 {
                return Err(Error::Apci("I frame without ASDU"));
            }
            return Ok(Apdu::I(u16::from_le_bytes([d[2], d[3]]) >> 1, u16::from_le_bytes([d[4], d[5]]) >> 1, d[6..].to_vec()));
        }
        if d.len() != 6 {
            return Err(Error::Apci("S/U frame with payload"));
        }
        if c1 & 3 == 1 {
            if c1 != 1 || d[3] != 0 || d[4] & 1 != 0 {
                return Err(Error::Apci("S control field"));
            }
            return Ok(Apdu::S(u16::from_le_bytes([d[4], d[5]]) >> 1));
        }
        if !matches!(c1, 0x07 | 0x0B | 0x13 | 0x23 | 0x43 | 0x83) || d[3] != 0 || d[4] != 0 || d[5] != 0 {
            return Err(Error::Apci("U control field"));
        }
        Ok(Apdu::U(c1))
    }

    /// Encodes the APDU.
    pub fn encode(&self) -> Vec<u8> {
        match self {
            Apdu::I(ns, nr, asdu) => {
                let mut out = vec![0x68, (4 + asdu.len()) as u8];
                out.extend_from_slice(&((ns & 0x7FFF) << 1).to_le_bytes());
                out.extend_from_slice(&((nr & 0x7FFF) << 1).to_le_bytes());
                out.extend_from_slice(asdu);
                out
            }
            Apdu::S(nr) => {
                let n = ((nr & 0x7FFF) << 1).to_le_bytes();
                vec![0x68, 4, 1, 0, n[0], n[1]]
            }
            Apdu::U(f) => vec![0x68, 4, *f, 0, 0, 0],
        }
    }
}

/// Splits a byte stream into APDUs, skipping noise; returns the frames and the number of bytes consumed.
pub fn split(stream: &[u8]) -> (Vec<&[u8]>, usize) {
    let mut frames = Vec::new();
    let mut p = 0;
    while p < stream.len() {
        if stream[p] != 0x68 {
            p += 1;
            continue;
        }
        if p + 2 > stream.len() {
            break;
        }
        let len = usize::from(stream[p + 1]);
        if !(4..=253).contains(&len) {
            p += 1;
            continue;
        }
        if p + 2 + len > stream.len() {
            break;
        }
        frames.push(&stream[p..p + 2 + len]);
        p += 2 + len;
    }
    (frames, p)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hex(s: &str) -> Vec<u8> {
        (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).expect("hex")).collect()
    }

    fn field(obj: &str, key: &str) -> String {
        let pat = format!("\"{key}\": \"");
        let Some(start) = obj.find(&pat).map(|i| i + pat.len()) else { return String::new() };
        let end = obj[start..].find('"').map_or(start, |i| i + start);
        obj[start..end].to_string()
    }

    fn render(v: f64) -> String {
        let s = format!("{v}");
        s.strip_suffix(".0").map(str::to_string).unwrap_or(s)
    }

    fn canonical(frame: &[u8]) -> Result<String, Error> {
        Ok(match Apdu::decode(frame)? {
            Apdu::S(nr) => format!("S|{nr}"),
            Apdu::U(f) => format!("U|{f}"),
            Apdu::I(ns, nr, asdu) => {
                let a = Asdu::decode(&asdu)?;
                let objects: Vec<String> = a
                    .objects
                    .iter()
                    .map(|o| {
                        let t = o.time.map_or("-".to_string(), |t| {
                            format!(
                                "{:04}-{:02}-{:02} {:02}:{:02}:{:02}.{:03}{}{}",
                                t.year,
                                t.month,
                                t.day,
                                t.hour,
                                t.minute,
                                t.millis / 1000,
                                t.millis % 1000,
                                if t.invalid { "I" } else { "" },
                                if t.summer { "S" } else { "" }
                            )
                        });
                        format!("{}:{}:{}:{}:{}", o.ioa, render(o.value), o.quality, o.qualifier, t)
                    })
                    .collect();
                format!(
                    "I|{ns}|{nr}|{}|{}|{}|{}|{}|{}|{}|{}",
                    a.type_id,
                    u8::from(a.sequence),
                    a.cause,
                    u8::from(a.negative),
                    u8::from(a.test),
                    a.originator,
                    a.common_address,
                    objects.join(";")
                )
            }
        })
    }

    /// Runs the shared vectors in /conformance/iec104.json, and re-encodes every valid frame.
    #[test]
    fn conformance_vectors() {
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../../conformance/iec104.json");
        let text = std::fs::read_to_string(path).expect("conformance/iec104.json");
        let mut checked = 0;
        for obj in text.split("\n  {").skip(1) {
            let name = field(obj, "name");
            let data = hex(&field(obj, "data"));
            let expected = field(obj, "fields");
            let got = canonical(&data);
            if expected == "error" {
                assert!(got.is_err(), "{name}: expected an error, got {got:?}");
            } else {
                assert_eq!(got.as_deref(), Ok(expected.as_str()), "{name}");
                let apdu = Apdu::decode(&data).expect("apdu");
                if let Apdu::I(ns, nr, asdu) = &apdu {
                    let re = Asdu::decode(asdu).expect("asdu").encode();
                    assert_eq!(&re, asdu, "{name}: ASDU round trip");
                    assert_eq!(Apdu::I(*ns, *nr, re).encode(), data, "{name}: APDU round trip");
                } else {
                    assert_eq!(apdu.encode(), data, "{name}: round trip");
                }
            }
            checked += 1;
        }
        assert!(checked >= 52, "checked {checked}");
    }

    #[test]
    fn split_skips_noise_and_keeps_partial_frames() {
        let mut stream = vec![0x00, 0x68, 0x02];
        stream.extend_from_slice(&hex("680407000000"));
        stream.extend_from_slice(&hex("680E000000006401060001000000"));
        let (frames, used) = split(&stream);
        assert_eq!(frames, vec![&hex("680407000000")[..]]);
        assert_eq!(used, 9);
    }
}
