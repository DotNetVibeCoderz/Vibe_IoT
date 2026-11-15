//! EtherNet/IP encapsulation, Common Packet Format, CIP Message Router requests/replies with EPATHs, and the
//! ListIdentity item.
//!
//! This is the fuzzed twin of `IoTCom.Net.Protocols.EtherNetIp` (C#), kept in sync by `/conformance/enip.json`.
//! Every decoder returns `None` for malformed input instead of panicking.

/// One Common Packet Format item: type and data.
pub type CpfItem = (u16, Vec<u8>);

/// An encapsulation packet (24-byte little-endian header and data).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Encap {
    /// Encapsulation command.
    pub command: u16,
    /// Session handle.
    pub session: u32,
    /// Encapsulation status.
    pub status: u32,
    /// Sender context, echoed by the target.
    pub context: [u8; 8],
    /// Options (zero).
    pub options: u32,
    /// Command data.
    pub data: Vec<u8>,
}

fn u16_le(d: &[u8], i: usize) -> Option<u16> {
    Some(u16::from_le_bytes([*d.get(i)?, *d.get(i + 1)?]))
}

fn u32_le(d: &[u8], i: usize) -> Option<u32> {
    Some(u32::from_le_bytes([*d.get(i)?, *d.get(i + 1)?, *d.get(i + 2)?, *d.get(i + 3)?]))
}

fn hex(d: &[u8]) -> String {
    d.iter().map(|b| format!("{b:02X}")).collect()
}

impl Encap {
    /// Header size.
    pub const HEADER: usize = 24;

    /// Decodes exactly one packet.
    pub fn decode(d: &[u8]) -> Option<Self> {
        if d.len() < Self::HEADER {
            return None;
        }
        let len = u16_le(d, 2)? as usize;
        if d.len() != Self::HEADER + len {
            return None;
        }
        let mut context = [0u8; 8];
        context.copy_from_slice(&d[12..20]);
        Some(Encap { command: u16_le(d, 0)?, session: u32_le(d, 4)?, status: u32_le(d, 8)?, context, options: u32_le(d, 20)?, data: d[24..].to_vec() })
    }

    /// Encodes header and data.
    pub fn encode(&self) -> Vec<u8> {
        let mut b = Vec::with_capacity(Self::HEADER + self.data.len());
        b.extend_from_slice(&self.command.to_le_bytes());
        b.extend_from_slice(&(self.data.len() as u16).to_le_bytes());
        b.extend_from_slice(&self.session.to_le_bytes());
        b.extend_from_slice(&self.status.to_le_bytes());
        b.extend_from_slice(&self.context);
        b.extend_from_slice(&self.options.to_le_bytes());
        b.extend_from_slice(&self.data);
        b
    }

    /// Normalised description; SendRRData/SendUnitData must carry a valid CPF list after the 6-byte prefix.
    pub fn describe(&self) -> Option<String> {
        let mut s = format!("command=0x{:04X} session=0x{:08X} status={} length={}", self.command, self.session, self.status, self.data.len());
        if (self.command == 0x6F || self.command == 0x70) && !self.data.is_empty() {
            if self.data.len() < 6 {
                return None;
            }
            let items = decode_cpf(&self.data[6..])?;
            let list: Vec<String> = items.iter().map(|(t, d)| format!("{t:04X}:{}", d.len())).collect();
            s.push_str(&format!(" items={}", list.join(",")));
        }
        Some(s)
    }
}

/// Decodes a CPF item list (no trailing bytes allowed).
pub fn decode_cpf(d: &[u8]) -> Option<Vec<CpfItem>> {
    let count = u16_le(d, 0)? as usize;
    let mut o = 2;
    let mut items = Vec::new();
    for _ in 0..count {
        let t = u16_le(d, o)?;
        let len = u16_le(d, o + 2)? as usize;
        let data = d.get(o + 4..o + 4 + len)?.to_vec();
        items.push((t, data));
        o += 4 + len;
    }
    if o != d.len() {
        return None;
    }
    Some(items)
}

/// Encodes a CPF item list.
pub fn encode_cpf(items: &[CpfItem]) -> Vec<u8> {
    let mut b = (items.len() as u16).to_le_bytes().to_vec();
    for (t, d) in items {
        b.extend_from_slice(&t.to_le_bytes());
        b.extend_from_slice(&(d.len() as u16).to_le_bytes());
        b.extend_from_slice(d);
    }
    b
}

/// A CIP EPATH: symbolic segments (with an optional element) and/or logical class/instance/attribute.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Path {
    /// Symbolic segments (tag name parts).
    pub symbols: Vec<String>,
    /// Element (array index) segment.
    pub element: Option<u32>,
    /// Class segment.
    pub class: Option<u32>,
    /// Instance segment.
    pub instance: Option<u32>,
    /// Attribute segment.
    pub attribute: Option<u32>,
}

impl Path {
    /// Decodes a padded EPATH.
    pub fn decode(d: &[u8]) -> Option<Self> {
        let mut p = Path::default();
        let mut o = 0;
        while o < d.len() {
            let seg = d[o];
            if seg == 0x91 {
                let len = *d.get(o + 1)? as usize;
                let name = d.get(o + 2..o + 2 + len)?;
                p.symbols.push(name.iter().map(|&c| c as char).collect());
                o += 2 + len + (len % 2);
                if o > d.len() {
                    return None;
                }
                continue;
            }
            let (value, size) = match seg & 3 {
                0 => (*d.get(o + 1)? as u32, 2),
                1 => {
                    d.get(o + 1)?;
                    (u16_le(d, o + 2)? as u32, 4)
                }
                2 => {
                    d.get(o + 1)?;
                    (u32_le(d, o + 2)?, 6)
                }
                _ => return None,
            };
            match seg & 0xFC {
                0x20 => p.class = Some(value),
                0x24 => p.instance = Some(value),
                0x28 => p.element = Some(value),
                0x30 => p.attribute = Some(value),
                _ => return None,
            }
            o += size;
        }
        Some(p)
    }

    fn logical(b: &mut Vec<u8>, kind: u8, v: u32) {
        if v <= 0xFF {
            b.extend_from_slice(&[kind, v as u8]);
        } else if v <= 0xFFFF {
            b.extend_from_slice(&[kind | 1, 0]);
            b.extend_from_slice(&(v as u16).to_le_bytes());
        } else {
            b.extend_from_slice(&[kind | 2, 0]);
            b.extend_from_slice(&v.to_le_bytes());
        }
    }

    /// Encodes a padded EPATH (symbols, element, class, instance, attribute).
    pub fn encode(&self) -> Vec<u8> {
        let mut b = Vec::new();
        for s in &self.symbols {
            let bytes: Vec<u8> = s.chars().map(|c| c as u8).collect();
            b.push(0x91);
            b.push(bytes.len() as u8);
            b.extend_from_slice(&bytes);
            if bytes.len() % 2 == 1 {
                b.push(0);
            }
        }
        if let Some(e) = self.element {
            Self::logical(&mut b, 0x28, e);
        }
        if let Some(c) = self.class {
            Self::logical(&mut b, 0x20, c);
        }
        if let Some(i) = self.instance {
            Self::logical(&mut b, 0x24, i);
        }
        if let Some(a) = self.attribute {
            Self::logical(&mut b, 0x30, a);
        }
        b
    }

    /// Normalised description.
    pub fn describe(&self) -> String {
        let mut parts: Vec<String> = self.symbols.iter().map(|s| format!("symbol:{s}")).collect();
        if let Some(e) = self.element {
            parts.push(format!("element:{e}"));
        }
        if let Some(c) = self.class {
            parts.push(format!("class:{c}"));
        }
        if let Some(i) = self.instance {
            parts.push(format!("instance:{i}"));
        }
        if let Some(a) = self.attribute {
            parts.push(format!("attribute:{a}"));
        }
        parts.join(",")
    }
}

/// A Message Router request.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Request {
    /// Service code.
    pub service: u8,
    /// Request path.
    pub path: Path,
    /// Request data.
    pub data: Vec<u8>,
}

impl Request {
    /// Decodes a request.
    pub fn decode(d: &[u8]) -> Option<Self> {
        let service = *d.first()?;
        let size = *d.get(1)? as usize * 2;
        let path = Path::decode(d.get(2..2 + size)?)?;
        Some(Request { service, path, data: d[2 + size..].to_vec() })
    }

    /// Encodes a request.
    pub fn encode(&self) -> Vec<u8> {
        let path = self.path.encode();
        let mut b = vec![self.service, (path.len() / 2) as u8];
        b.extend_from_slice(&path);
        b.extend_from_slice(&self.data);
        b
    }

    /// Normalised description.
    pub fn describe(&self) -> String {
        format!("service=0x{:02X} path={} data={}", self.service, self.path.describe(), hex(&self.data))
    }
}

/// A Message Router reply.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Response {
    /// Service code (without the reply bit).
    pub service: u8,
    /// General status.
    pub status: u8,
    /// Additional status words.
    pub additional: Vec<u16>,
    /// Reply data.
    pub data: Vec<u8>,
}

impl Response {
    /// Decodes a reply.
    pub fn decode(d: &[u8]) -> Option<Self> {
        if d.len() < 4 || d[0] & 0x80 == 0 || d[1] != 0 {
            return None;
        }
        let words = d[3] as usize;
        let mut additional = Vec::with_capacity(words);
        for i in 0..words {
            additional.push(u16_le(d, 4 + i * 2)?);
        }
        Some(Response { service: d[0] & 0x7F, status: d[2], additional, data: d[4 + words * 2..].to_vec() })
    }

    /// Encodes a reply.
    pub fn encode(&self) -> Vec<u8> {
        let mut b = vec![self.service | 0x80, 0, self.status, self.additional.len() as u8];
        for a in &self.additional {
            b.extend_from_slice(&a.to_le_bytes());
        }
        b.extend_from_slice(&self.data);
        b
    }

    /// Normalised description.
    pub fn describe(&self) -> String {
        let add: String = self.additional.iter().map(|a| format!("{a:04X}")).collect::<Vec<_>>().join(",");
        format!("service=0x{:02X} status=0x{:02X} additional={add} data={}", self.service, self.status, hex(&self.data))
    }
}

/// The CIP Identity item of ListIdentity.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Identity {
    /// IPv4 address of the socket address.
    pub address: [u8; 4],
    /// TCP port.
    pub port: u16,
    /// Vendor id.
    pub vendor: u16,
    /// Device type.
    pub device_type: u16,
    /// Product code.
    pub product: u16,
    /// Major and minor revision.
    pub revision: (u8, u8),
    /// Identity status word.
    pub status: u16,
    /// Serial number.
    pub serial: u32,
    /// Product name (ASCII).
    pub name: Vec<u8>,
    /// State.
    pub state: u8,
}

impl Identity {
    /// Decodes the identity item.
    pub fn decode(d: &[u8]) -> Option<Self> {
        let name_len = *d.get(32)? as usize;
        let name = d.get(33..33 + name_len)?.to_vec();
        let state = *d.get(33 + name_len)?;
        Some(Identity {
            port: u16::from_be_bytes([*d.get(4)?, *d.get(5)?]),
            address: [d[6], d[7], d[8], d[9]],
            vendor: u16_le(d, 18)?,
            device_type: u16_le(d, 20)?,
            product: u16_le(d, 22)?,
            revision: (d[24], d[25]),
            status: u16_le(d, 26)?,
            serial: u32_le(d, 28)?,
            name,
            state,
        })
    }

    /// Normalised description.
    pub fn describe(&self) -> String {
        let name: String = self.name.iter().map(|&c| c as char).collect();
        format!(
            "vendor={} type={} product={} rev={}.{} status=0x{:04X} serial=0x{:08X} name={name} state={} address={}.{}.{}.{}:{}",
            self.vendor, self.device_type, self.product, self.revision.0, self.revision.1, self.status, self.serial, self.state,
            self.address[0], self.address[1], self.address[2], self.address[3], self.port
        )
    }
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
        let end = obj[start..].find('"').map_or(start, |i| i + start);
        obj[start..end].to_string()
    }

    #[test]
    fn conformance_vectors() {
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../../conformance/enip.json");
        let text = std::fs::read_to_string(path).expect("conformance/enip.json");
        let mut checked = 0;
        for obj in text.split("\n  {").skip(1) {
            let name = field(obj, "name");
            let expected = field(obj, "expected");
            let data = unhex(&field(obj, "hex"));
            let got = match field(obj, "kind").as_str() {
                "encap" => Encap::decode(&data).and_then(|e| {
                    assert_eq!(e.encode(), data, "{name}");
                    e.describe()
                }),
                "request" => Request::decode(&data).map(|r| {
                    assert_eq!(r.encode(), data, "{name}");
                    r.describe()
                }),
                "response" => Response::decode(&data).map(|r| {
                    assert_eq!(r.encode(), data, "{name}");
                    r.describe()
                }),
                "identity" => Identity::decode(&data).map(|i| i.describe()),
                k => panic!("unknown kind {k}"),
            };
            assert_eq!(got.unwrap_or_else(|| "error".to_string()), expected, "{name}");
            checked += 1;
        }
        assert_eq!(checked, 28);
    }

    #[test]
    fn cpf_round_trip() {
        let items = vec![(0u16, vec![]), (0xB2u16, vec![0x0E, 0x02, 0x20, 0x01, 0x24, 0x01])];
        assert_eq!(decode_cpf(&encode_cpf(&items)).unwrap(), items);
    }
}
