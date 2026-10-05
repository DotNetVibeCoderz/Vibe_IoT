//! # iotcom-coap
//!
//! CoAP (RFC 7252) message codec: header, token, delta-encoded options with extended deltas and lengths,
//! payload marker. Decoding validates every rule of §3 and §4.1 (version, token length, reserved nibbles,
//! truncation, empty-message format). Kept byte-for-byte in sync with the C# codec through
//! `/conformance/coap.json`, which both test suites execute, and fuzzed with `cargo fuzz run coap`.
//!
//! Built by Gravicode Studios, led by Kang Fadhil.
#![forbid(unsafe_code)]

use core::fmt;

/// Message type (RFC 7252 §4.3).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum MessageType {
    /// Confirmable.
    Confirmable = 0,
    /// Non-confirmable.
    NonConfirmable = 1,
    /// Acknowledgement.
    Acknowledgement = 2,
    /// Reset.
    Reset = 3,
}

impl MessageType {
    fn from_bits(v: u8) -> Self {
        match v & 3 {
            0 => Self::Confirmable,
            1 => Self::NonConfirmable,
            2 => Self::Acknowledgement,
            _ => Self::Reset,
        }
    }
}

/// Well-known option numbers (RFC 7252 §5.10, RFC 7641, RFC 7959).
pub mod option {
    /// If-Match.
    pub const IF_MATCH: u16 = 1;
    /// Uri-Host.
    pub const URI_HOST: u16 = 3;
    /// ETag.
    pub const ETAG: u16 = 4;
    /// If-None-Match.
    pub const IF_NONE_MATCH: u16 = 5;
    /// Observe.
    pub const OBSERVE: u16 = 6;
    /// Uri-Port.
    pub const URI_PORT: u16 = 7;
    /// Location-Path.
    pub const LOCATION_PATH: u16 = 8;
    /// Uri-Path.
    pub const URI_PATH: u16 = 11;
    /// Content-Format.
    pub const CONTENT_FORMAT: u16 = 12;
    /// Max-Age.
    pub const MAX_AGE: u16 = 14;
    /// Uri-Query.
    pub const URI_QUERY: u16 = 15;
    /// Accept.
    pub const ACCEPT: u16 = 17;
    /// Block2.
    pub const BLOCK2: u16 = 23;
    /// Block1.
    pub const BLOCK1: u16 = 27;
    /// Size2.
    pub const SIZE2: u16 = 28;
    /// Size1.
    pub const SIZE1: u16 = 60;
}

/// A CoAP message.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Message {
    /// Type.
    pub mtype: MessageType,
    /// Code (class << 5 | detail), e.g. 0x01 GET, 0x45 2.05 Content.
    pub code: u8,
    /// Message ID.
    pub message_id: u16,
    /// Token (0–8 bytes).
    pub token: Vec<u8>,
    /// Options sorted by number (repeatable options keep their order).
    pub options: Vec<(u16, Vec<u8>)>,
    /// Payload.
    pub payload: Vec<u8>,
}

/// Why a datagram is not a valid CoAP message.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum DecodeError {
    /// Shorter than the 4-byte header.
    TooShort,
    /// Version is not 1.
    BadVersion,
    /// Token length 9–15.
    BadTokenLength,
    /// The datagram ends inside the token, an option or its extension.
    Truncated,
    /// Delta or length nibble 15 outside the payload marker.
    ReservedNibble,
    /// Payload marker followed by no payload.
    EmptyPayload,
    /// Code 0.00 with a token, options or payload.
    BadEmptyMessage,
    /// Option number above 65535.
    OptionOverflow,
}

impl fmt::Display for DecodeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::TooShort => "message shorter than 4 bytes",
            Self::BadVersion => "unsupported CoAP version",
            Self::BadTokenLength => "token length 9-15 is reserved",
            Self::Truncated => "message truncated",
            Self::ReservedNibble => "reserved option nibble 15",
            Self::EmptyPayload => "payload marker without payload",
            Self::BadEmptyMessage => "empty message must have no token, options or payload",
            Self::OptionOverflow => "option number above 65535",
        })
    }
}

impl std::error::Error for DecodeError {}

fn read_ext(nibble: u8, rest: &[u8], pos: &mut usize) -> Result<u32, DecodeError> {
    match nibble {
        0..=12 => Ok(u32::from(nibble)),
        13 => {
            let b = *rest.get(*pos).ok_or(DecodeError::Truncated)?;
            *pos += 1;
            Ok(u32::from(b) + 13)
        }
        14 => {
            let hi = *rest.get(*pos).ok_or(DecodeError::Truncated)?;
            let lo = *rest.get(*pos + 1).ok_or(DecodeError::Truncated)?;
            *pos += 2;
            Ok(u32::from(u16::from_be_bytes([hi, lo])) + 269)
        }
        _ => Err(DecodeError::ReservedNibble),
    }
}

/// Decodes one datagram.
pub fn decode(data: &[u8]) -> Result<Message, DecodeError> {
    if data.len() < 4 {
        return Err(DecodeError::TooShort);
    }
    if data[0] >> 6 != 1 {
        return Err(DecodeError::BadVersion);
    }
    let tkl = usize::from(data[0] & 0x0F);
    if tkl > 8 {
        return Err(DecodeError::BadTokenLength);
    }
    if data.len() < 4 + tkl {
        return Err(DecodeError::Truncated);
    }
    let mtype = MessageType::from_bits(data[0] >> 4);
    let code = data[1];
    let message_id = u16::from_be_bytes([data[2], data[3]]);
    let token = data[4..4 + tkl].to_vec();
    let mut pos = 4 + tkl;
    let mut options = Vec::new();
    let mut payload = Vec::new();
    let mut number: u32 = 0;
    while pos < data.len() {
        let b = data[pos];
        pos += 1;
        if b == 0xFF {
            if pos == data.len() {
                return Err(DecodeError::EmptyPayload);
            }
            payload = data[pos..].to_vec();
            break;
        }
        let delta = read_ext(b >> 4, data, &mut pos)?;
        let len = read_ext(b & 0x0F, data, &mut pos)? as usize;
        number += delta;
        if number > u32::from(u16::MAX) {
            return Err(DecodeError::OptionOverflow);
        }
        if pos + len > data.len() {
            return Err(DecodeError::Truncated);
        }
        options.push((number as u16, data[pos..pos + len].to_vec()));
        pos += len;
    }
    if code == 0 && (tkl > 0 || !options.is_empty() || !payload.is_empty()) {
        return Err(DecodeError::BadEmptyMessage);
    }
    Ok(Message { mtype, code, message_id, token, options, payload })
}

fn ext(v: usize) -> (u8, Vec<u8>) {
    if v < 13 {
        (v as u8, Vec::new())
    } else if v < 269 {
        (13, vec![(v - 13) as u8])
    } else {
        (14, ((v - 269) as u16).to_be_bytes().to_vec())
    }
}

/// Encodes a message (options are written in ascending number order; equal numbers keep their order).
///
/// # Panics
/// When the token is longer than 8 bytes or an option value exceeds 65 804 bytes (caller bugs).
pub fn encode(m: &Message, out: &mut Vec<u8>) {
    assert!(m.token.len() <= 8, "token longer than 8 bytes");
    out.push(0x40 | ((m.mtype as u8) << 4) | m.token.len() as u8);
    out.push(m.code);
    out.extend_from_slice(&m.message_id.to_be_bytes());
    out.extend_from_slice(&m.token);
    let mut sorted: Vec<&(u16, Vec<u8>)> = m.options.iter().collect();
    sorted.sort_by_key(|o| o.0);
    let mut last = 0usize;
    for (number, value) in sorted {
        assert!(value.len() <= 65_804, "option value too long");
        let (dn, dx) = ext(usize::from(*number) - last);
        let (ln, lx) = ext(value.len());
        out.push((dn << 4) | ln);
        out.extend_from_slice(&dx);
        out.extend_from_slice(&lx);
        out.extend_from_slice(value);
        last = usize::from(*number);
    }
    if !m.payload.is_empty() {
        out.push(0xFF);
        out.extend_from_slice(&m.payload);
    }
}

/// Encodes an unsigned option value in the minimal number of bytes (0 → empty).
pub fn encode_uint(v: u32) -> Vec<u8> {
    let bytes = v.to_be_bytes();
    let skip = bytes.iter().take_while(|b| **b == 0).count();
    bytes[skip..].to_vec()
}

/// Decodes an unsigned option value (at most 4 bytes).
pub fn decode_uint(v: &[u8]) -> Option<u32> {
    (v.len() <= 4).then(|| v.iter().fold(0u32, |acc, b| (acc << 8) | u32::from(*b)))
}

/// A Block1/Block2 option value (RFC 7959 §2.2).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Block {
    /// Block number.
    pub num: u32,
    /// More blocks follow.
    pub more: bool,
    /// Size exponent: block size = 2^(szx + 4), 0–6.
    pub szx: u8,
}

impl Block {
    /// Block size in bytes.
    pub fn size(self) -> usize {
        16 << self.szx
    }

    /// Decodes an option value.
    pub fn decode(v: &[u8]) -> Option<Self> {
        let raw = decode_uint(v)?;
        let szx = (raw & 7) as u8;
        (szx < 7 && raw >> 4 <= 0x000F_FFFF).then_some(Self { num: raw >> 4, more: raw & 8 != 0, szx })
    }

    /// Encodes the option value.
    pub fn encode(self) -> Vec<u8> {
        encode_uint((self.num << 4) | if self.more { 8 } else { 0 } | u32::from(self.szx))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rfc_example_round_trips() {
        let wire = hex("40017D34BB74656D7065726174757265");
        let m = decode(&wire).unwrap();
        assert_eq!((m.mtype, m.code, m.message_id), (MessageType::Confirmable, 0x01, 0x7D34));
        assert_eq!(m.options, vec![(option::URI_PATH, b"temperature".to_vec())]);
        let mut out = Vec::new();
        encode(&m, &mut out);
        assert_eq!(out, wire);
    }

    #[test]
    fn uint_and_block() {
        assert_eq!(encode_uint(0), Vec::<u8>::new());
        assert_eq!(encode_uint(70_000), vec![0x01, 0x11, 0x70]);
        assert_eq!(decode_uint(&[0x01, 0x11, 0x70]), Some(70_000));
        let b = Block { num: 3, more: true, szx: 6 };
        assert_eq!(b.encode(), vec![0x3E]);
        assert_eq!(Block::decode(&[0x3E]), Some(b));
        assert_eq!(b.size(), 1024);
        assert_eq!(Block::decode(&[0x07]), None);
    }

    /// Runs the shared vectors in /conformance/coap.json.
    #[test]
    fn conformance_vectors() {
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../../conformance/coap.json");
        let text = std::fs::read_to_string(path).expect("conformance/coap.json");
        let mut checked = 0;
        for obj in text.split('{').skip(1) {
            let wire = hex(&field(obj, "wire"));
            let name = field(obj, "name");
            if obj.contains("\"valid\": false") {
                assert!(decode(&wire).is_err(), "{name} should be rejected");
            } else {
                let m = decode(&wire).unwrap_or_else(|e| panic!("{name}: {e}"));
                assert_eq!(m.mtype as u8, num(obj, "type") as u8, "{name}");
                assert_eq!(u32::from(m.code), num(obj, "code"), "{name}");
                assert_eq!(u32::from(m.message_id), num(obj, "messageId"), "{name}");
                assert_eq!(m.token, hex(&field(obj, "token")), "{name}");
                assert_eq!(m.payload, hex(&field(obj, "payload")), "{name}");
                let opts: Vec<String> = m.options.iter().map(|(n, v)| format!("{n}:{}", to_hex(v))).collect();
                assert_eq!(opts.join(";"), field(obj, "options"), "{name}");
                let mut out = Vec::new();
                encode(&m, &mut out);
                assert_eq!(out, wire, "{name}: re-encode");
            }
            checked += 1;
        }
        assert!(checked >= 20, "checked {checked}");
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

    fn hex(s: &str) -> Vec<u8> {
        (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
    }

    fn to_hex(v: &[u8]) -> String {
        v.iter().map(|b| format!("{b:02X}")).collect()
    }
}
