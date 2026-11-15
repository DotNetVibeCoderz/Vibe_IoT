//! Matter codecs: TLV, onboarding payloads (QR base38 and manual codes) and message/protocol headers.
//!
//! This is the fuzzed twin of `IoTCom.Net.Protocols.Matter` (C#), kept in sync by `/conformance/matter.json`.
//! Every decoder returns `None` for malformed input instead of panicking.

/// A TLV tag.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Tag {
    /// No tag.
    Anonymous,
    /// Context-specific.
    Context(u8),
    /// Common profile.
    Common(u32),
    /// Implicit profile.
    Implicit(u32),
    /// Fully qualified: vendor, profile, tag.
    FullyQualified(u16, u16, u32),
}

/// A TLV value.
#[derive(Debug, Clone, PartialEq)]
pub enum Value {
    /// Signed integer.
    Int(i64),
    /// Unsigned integer.
    UInt(u64),
    /// Boolean.
    Bool(bool),
    /// Single.
    Float(f32),
    /// Double.
    Double(f64),
    /// UTF-8 string.
    Utf8(String),
    /// Octet string.
    Bytes(Vec<u8>),
    /// Null.
    Null,
    /// Structure.
    Structure(Vec<Element>),
    /// Array.
    Array(Vec<Element>),
    /// List.
    List(Vec<Element>),
}

/// A tagged TLV element.
#[derive(Debug, Clone, PartialEq)]
pub struct Element {
    /// Tag.
    pub tag: Tag,
    /// Value.
    pub value: Value,
}

fn le(d: &[u8], at: &mut usize, size: usize) -> Option<u64> {
    let bytes = d.get(*at..*at + size)?;
    *at += size;
    Some(bytes.iter().rev().fold(0u64, |v, &b| (v << 8) | u64::from(b)))
}

fn read(d: &[u8], at: &mut usize, depth: usize) -> Option<Option<Element>> {
    if depth > 32 {
        return None;
    }
    let control = *d.get(*at)?;
    *at += 1;
    let kind = control & 0x1F;
    if kind == 0x18 {
        return (control == 0x18).then_some(None);
    }
    if kind > 0x18 {
        return None;
    }
    let tag = match control >> 5 {
        0 => Tag::Anonymous,
        1 => Tag::Context(le(d, at, 1)? as u8),
        2 => Tag::Common(le(d, at, 2)? as u32),
        3 => Tag::Common(le(d, at, 4)? as u32),
        4 => Tag::Implicit(le(d, at, 2)? as u32),
        5 => Tag::Implicit(le(d, at, 4)? as u32),
        6 => Tag::FullyQualified(le(d, at, 2)? as u16, le(d, at, 2)? as u16, le(d, at, 2)? as u32),
        _ => Tag::FullyQualified(le(d, at, 2)? as u16, le(d, at, 2)? as u16, le(d, at, 4)? as u32),
    };
    let value = match kind {
        0..=3 => {
            let size = 1usize << kind;
            let raw = le(d, at, size)?;
            let shift = 64 - 8 * size as u32;
            Value::Int(((raw << shift) as i64) >> shift)
        }
        4..=7 => Value::UInt(le(d, at, 1 << (kind - 4))?),
        8 => Value::Bool(false),
        9 => Value::Bool(true),
        0x0A => Value::Float(f32::from_bits(le(d, at, 4)? as u32)),
        0x0B => Value::Double(f64::from_bits(le(d, at, 8)?)),
        0x0C..=0x13 => {
            let utf8 = kind <= 0x0F;
            let len = usize::try_from(le(d, at, 1 << ((kind - if utf8 { 0x0C } else { 0x10 }) & 3))?).ok()?;
            let bytes = d.get(*at..at.checked_add(len)?)?.to_vec();
            *at += len;
            if utf8 {
                Value::Utf8(String::from_utf8(bytes).ok()?)
            } else {
                Value::Bytes(bytes)
            }
        }
        0x14 => Value::Null,
        _ => {
            let mut members = Vec::new();
            while let Some(m) = read(d, at, depth + 1)? {
                if kind == 0x16 && m.tag != Tag::Anonymous || kind == 0x15 && m.tag == Tag::Anonymous {
                    return None;
                }
                members.push(m);
            }
            match kind {
                0x15 => Value::Structure(members),
                0x16 => Value::Array(members),
                _ => Value::List(members),
            }
        }
    };
    Some(Some(Element { tag, value }))
}

/// Decodes exactly one element spanning the whole input.
pub fn decode(d: &[u8]) -> Option<Element> {
    let mut at = 0;
    let e = read(d, &mut at, 0)??;
    (at == d.len()).then_some(e)
}

/// Normalised description shared with the C# suite.
pub fn describe(e: &Element) -> String {
    let prefix = match &e.tag {
        Tag::Anonymous => String::new(),
        Tag::Context(n) => format!("{n}="),
        Tag::Common(n) => format!("common:{n}="),
        Tag::Implicit(n) => format!("implicit:{n}="),
        Tag::FullyQualified(v, p, n) => format!("{v:04X}:{p:04X}:{n}="),
    };
    let join = |m: &[Element]| m.iter().map(describe).collect::<Vec<_>>().join(",");
    let body = match &e.value {
        Value::Int(v) => v.to_string(),
        Value::UInt(v) => format!("{v}u"),
        Value::Bool(b) => b.to_string(),
        Value::Float(f) => format!("{f}f"),
        Value::Double(f) => format!("{f}d"),
        Value::Utf8(s) => format!("\"{s}\""),
        Value::Bytes(b) => format!("h'{}'", b.iter().map(|x| format!("{x:02X}")).collect::<String>()),
        Value::Null => "null".to_string(),
        Value::Structure(m) => format!("{{{}}}", join(m)),
        Value::Array(m) => format!("[{}]", join(m)),
        Value::List(m) => format!("({})", join(m)),
    };
    prefix + &body
}

/// An onboarding payload.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Payload {
    /// Vendor id.
    pub vendor: u16,
    /// Product id.
    pub product: u16,
    /// Commissioning flow.
    pub flow: u8,
    /// Discovery capabilities.
    pub discovery: u8,
    /// 12-bit discriminator.
    pub discriminator: u16,
    /// Setup passcode.
    pub passcode: u32,
}

const ALPHABET: &[u8] = b"0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-.";
const INVALID: [u32; 12] = [0, 11111111, 22222222, 33333333, 44444444, 55555555, 66666666, 77777777, 88888888, 99999999, 12345678, 87654321];

fn valid_passcode(p: u32) -> bool {
    (1..=99_999_998).contains(&p) && !INVALID.contains(&p)
}

/// Decodes base38.
pub fn base38_decode(text: &str) -> Option<Vec<u8>> {
    let chars = text.as_bytes();
    let mut out = Vec::new();
    let mut i = 0;
    while i < chars.len() {
        let n = match chars.len() - i {
            r if r >= 5 => 5,
            4 => 4,
            2 => 2,
            _ => return None,
        };
        let mut v: u64 = 0;
        for &c in chars[i..i + n].iter().rev() {
            v = v * 38 + ALPHABET.iter().position(|&a| a == c)? as u64;
        }
        let bytes = match n {
            5 => 3,
            4 => 2,
            _ => 1,
        };
        if v >> (8 * bytes) != 0 {
            return None;
        }
        out.extend((0..bytes).map(|k| (v >> (8 * k)) as u8));
        i += n;
    }
    Some(out)
}

/// Decodes `MT:` QR text.
pub fn parse_qr(text: &str) -> Option<Payload> {
    let body = text.strip_prefix("MT:")?;
    let body = body.split('*').next()?;
    let bits = base38_decode(body)?;
    if bits.len() < 11 {
        return None;
    }
    let mut at = 0usize;
    let mut get = |count: usize| {
        let mut v = 0u64;
        for i in 0..count {
            if bits[at / 8] & (1 << (at % 8)) != 0 {
                v |= 1 << i;
            }
            at += 1;
        }
        v
    };
    if get(3) != 0 {
        return None;
    }
    let p = Payload { vendor: get(16) as u16, product: get(16) as u16, flow: get(2) as u8, discovery: get(8) as u8, discriminator: get(12) as u16, passcode: get(27) as u32 };
    valid_passcode(p.passcode).then_some(p)
}

const D5: [[u8; 10]; 10] = [
    [0, 1, 2, 3, 4, 5, 6, 7, 8, 9],
    [1, 2, 3, 4, 0, 6, 7, 8, 9, 5],
    [2, 3, 4, 0, 1, 7, 8, 9, 5, 6],
    [3, 4, 0, 1, 2, 8, 9, 5, 6, 7],
    [4, 0, 1, 2, 3, 9, 5, 6, 7, 8],
    [5, 9, 8, 7, 6, 0, 4, 3, 2, 1],
    [6, 5, 9, 8, 7, 1, 0, 4, 3, 2],
    [7, 6, 5, 9, 8, 2, 1, 0, 4, 3],
    [8, 7, 6, 5, 9, 3, 2, 1, 0, 4],
    [9, 8, 7, 6, 5, 4, 3, 2, 1, 0],
];
const P8: [[u8; 10]; 8] = [
    [0, 1, 2, 3, 4, 5, 6, 7, 8, 9],
    [1, 5, 7, 6, 2, 8, 3, 0, 9, 4],
    [5, 8, 0, 3, 7, 9, 6, 1, 4, 2],
    [8, 9, 1, 6, 0, 4, 3, 5, 2, 7],
    [9, 4, 5, 3, 1, 2, 6, 8, 7, 0],
    [4, 2, 8, 6, 5, 7, 3, 9, 0, 1],
    [2, 7, 9, 3, 8, 0, 6, 4, 1, 5],
    [7, 0, 4, 6, 9, 1, 3, 2, 5, 8],
];

/// True when the last digit is the Verhoeff check digit.
pub fn verhoeff_valid(digits: &[u8]) -> bool {
    let mut c = 0u8;
    for (i, &d) in digits.iter().rev().enumerate() {
        c = D5[usize::from(c)][usize::from(P8[i % 8][usize::from(d)])];
    }
    c == 0
}

/// Decodes an 11 or 21-digit manual pairing code (dashes and spaces ignored).
pub fn parse_manual(text: &str) -> Option<Payload> {
    let digits: Vec<u8> = text.bytes().filter(u8::is_ascii_digit).map(|b| b - b'0').collect();
    if (digits.len() != 11 && digits.len() != 21) || !verhoeff_valid(&digits) {
        return None;
    }
    let num = |from: usize, len: usize| digits[from..from + len].iter().fold(0u32, |v, &d| v * 10 + u32::from(d));
    let chunk1 = u32::from(digits[0]);
    let vid_pid = chunk1 & 4 != 0;
    if vid_pid != (digits.len() == 21) {
        return None;
    }
    let chunk2 = num(1, 5);
    let chunk3 = num(6, 4);
    let short = ((chunk1 & 3) << 2) | (chunk2 >> 14);
    let passcode = (chunk3 << 14) | (chunk2 & 0x3FFF);
    if !valid_passcode(passcode) {
        return None;
    }
    let (vendor, product) = if vid_pid { (u16::try_from(num(10, 5)).ok()?, u16::try_from(num(15, 5)).ok()?) } else { (0, 0) };
    Some(Payload { vendor, product, flow: u8::from(vid_pid), discovery: 0, discriminator: (short << 8) as u16, passcode })
}

/// Normalised payload description.
pub fn describe_payload(p: &Payload) -> String {
    format!("vid={:04X} pid={:04X} flow={} disc={} discriminator={} passcode={}", p.vendor, p.product, p.flow, p.discovery, p.discriminator, p.passcode)
}

/// A decoded unsecured message.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Message {
    /// Session id.
    pub session: u16,
    /// Counter.
    pub counter: u32,
    /// Source node id.
    pub source: Option<u64>,
    /// Destination node id.
    pub destination: Option<u64>,
    /// Destination group id.
    pub group: Option<u16>,
    /// Exchange flags.
    pub exchange_flags: u8,
    /// Opcode.
    pub opcode: u8,
    /// Exchange id.
    pub exchange: u16,
    /// Protocol vendor id.
    pub vendor: u16,
    /// Protocol id.
    pub protocol: u16,
    /// Acknowledged counter.
    pub ack: Option<u32>,
    /// Payload.
    pub payload: Vec<u8>,
}

/// Decodes an unsecured message (header and protocol header).
pub fn parse_message(m: &[u8]) -> Option<Message> {
    if m.len() < 8 {
        return None;
    }
    let flags = m[0];
    let security = m[3];
    if flags >> 4 != 0 || flags & 3 == 3 || security & 0xA0 != 0 {
        return None;
    }
    let mut at = 8usize;
    let source = if flags & 4 != 0 { Some(le(m, &mut at, 8)?) } else { None };
    let (destination, group) = match flags & 3 {
        1 => (Some(le(m, &mut at, 8)?), None),
        2 => (None, Some(le(m, &mut at, 2)? as u16)),
        _ => (None, None),
    };
    if m.len() < at + 6 {
        return None;
    }
    let ex = m[at];
    let opcode = m[at + 1];
    let exchange = u16::from_le_bytes([m[at + 2], m[at + 3]]);
    at += 4;
    let vendor = if ex & 0x10 != 0 { le(m, &mut at, 2)? as u16 } else { 0 };
    let protocol = le(m, &mut at, 2)? as u16;
    let ack = if ex & 2 != 0 { Some(le(m, &mut at, 4)? as u32) } else { None };
    if ex & 8 != 0 {
        return None;
    }
    Some(Message {
        session: u16::from_le_bytes([m[1], m[2]]),
        counter: u32::from_le_bytes([m[4], m[5], m[6], m[7]]),
        source,
        destination,
        group,
        exchange_flags: ex,
        opcode,
        exchange,
        vendor,
        protocol,
        ack,
        payload: m[at..].to_vec(),
    })
}

/// Normalised message description.
pub fn describe_message(m: &Message) -> String {
    let src = m.source.map_or_else(|| "-".to_string(), |s| format!("{s:016X}"));
    let dst = match (m.destination, m.group) {
        (Some(d), _) => format!("{d:016X}"),
        (None, Some(g)) => format!("g{g:04X}"),
        _ => "-".to_string(),
    };
    let flags = format!("{}{}{}", if m.exchange_flags & 1 != 0 { "I" } else { "" }, if m.exchange_flags & 4 != 0 { "R" } else { "" }, if m.ack.is_some() { "A" } else { "" });
    format!(
        "session={} counter={} src={src} dst={dst} exchange={} {flags} ack={} protocol={:04X}:{:04X} opcode=0x{:02X} payload={}",
        m.session,
        m.counter,
        m.exchange,
        m.ack.map_or_else(|| "-".to_string(), |a| a.to_string()),
        m.vendor,
        m.protocol,
        m.opcode,
        m.payload.iter().map(|b| format!("{b:02X}")).collect::<String>()
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    fn unhex(s: &str) -> Vec<u8> {
        (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).expect("hex")).collect()
    }

    fn field(obj: &str, key: &str) -> String {
        let pat = format!("\"{key}\": \"");
        let Some(start) = obj.find(&pat).map(|i| i + pat.len()) else { return String::new() };
        let rest = &obj[start..];
        let mut end = 0;
        let bytes = rest.as_bytes();
        while end < bytes.len() && !(bytes[end] == b'"' && (end == 0 || bytes[end - 1] != b'\\')) {
            end += 1;
        }
        rest[..end].replace("\\\"", "\"").replace("\\\\", "\\")
    }

    #[test]
    fn conformance_vectors() {
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../../conformance/matter.json");
        let text = std::fs::read_to_string(path).expect("conformance/matter.json");
        let mut checked = 0;
        for obj in text.split("\n  {").skip(1) {
            let name = field(obj, "name");
            let expected = field(obj, "expected");
            let data = unhex(&field(obj, "hex"));
            let as_text = String::from_utf8(data.clone()).unwrap_or_default();
            let got = match field(obj, "kind").as_str() {
                "tlv" => decode(&data).map(|e| describe(&e)),
                "qr" => parse_qr(&as_text).map(|p| describe_payload(&p)),
                "manual" => parse_manual(&as_text).map(|p| describe_payload(&p)),
                "message" => parse_message(&data).map(|m| describe_message(&m)),
                _ => continue, // cfid and spake2p are checked by the C# suite (crypto)
            };
            assert_eq!(got.unwrap_or_else(|| "error".to_string()), expected, "{name}");
            checked += 1;
        }
        assert_eq!(checked, 38);
    }
}
