//! EtherCAT frames (EtherCAT header and datagrams with working counters), mailboxes, CoE SDO messages and SII images.
//!
//! This is the fuzzed twin of `IoTCom.Net.Protocols.EtherCat` (C#), kept in sync by `/conformance/ethercat.json`.
//! Every decoder returns `None` for malformed input instead of panicking.

/// EtherCAT EtherType.
pub const ETHER_TYPE: u16 = 0x88A4;

/// Datagram header (10 bytes) plus working counter (2 bytes).
pub const DATAGRAM_OVERHEAD: usize = 12;

const COMMANDS: [&str; 15] = [
    "NOP", "APRD", "APWR", "APRW", "FPRD", "FPWR", "FPRW", "BRD", "BWR", "BRW", "LRD", "LWR", "LRW", "ARMW", "FRMW",
];

fn u16_le(d: &[u8], i: usize) -> Option<u16> {
    Some(u16::from_le_bytes([*d.get(i)?, *d.get(i + 1)?]))
}

fn u32_le(d: &[u8], i: usize) -> Option<u32> {
    Some(u32::from_le_bytes([*d.get(i)?, *d.get(i + 1)?, *d.get(i + 2)?, *d.get(i + 3)?]))
}

fn hex(d: &[u8]) -> String {
    d.iter().map(|b| format!("{b:02X}")).collect()
}

/// One EtherCAT datagram.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Datagram {
    /// Command (0 NOP … 14 FRMW).
    pub command: u8,
    /// Index chosen by the master.
    pub index: u8,
    /// Address: ADP (low word) and ADO (high word), or a logical address.
    pub address: u32,
    /// Circulating flag.
    pub circulating: bool,
    /// Interrupt field.
    pub irq: u16,
    /// Data.
    pub data: Vec<u8>,
    /// Working counter.
    pub wkc: u16,
}

impl Datagram {
    /// Bytes on the wire.
    pub fn encoded_len(&self) -> usize {
        DATAGRAM_OVERHEAD + self.data.len()
    }

    /// Normalised description shared with the C# suite.
    pub fn describe(&self) -> String {
        format!(
            "{} idx={} addr=0x{:08X} len={} irq=0x{:04X} wkc={} c={} data={}",
            COMMANDS[self.command as usize],
            self.index,
            self.address,
            self.data.len(),
            self.irq,
            self.wkc,
            u8::from(self.circulating),
            hex(&self.data)
        )
    }
}

/// Decodes the EtherCAT header and datagrams; Ethernet padding after the announced length is ignored.
pub fn decode_payload(p: &[u8]) -> Option<Vec<Datagram>> {
    let header = u16_le(p, 0)?;
    let length = usize::from(header & 0x7FF);
    if header >> 12 != 1 || header & 0x0800 != 0 || length > p.len() - 2 {
        return None;
    }
    let body = &p[2..2 + length];
    let mut out = Vec::new();
    let mut at = 0;
    loop {
        if body.len() - at < DATAGRAM_OVERHEAD {
            return None;
        }
        let command = body[at];
        if command > 14 {
            return None;
        }
        let len_field = u16_le(body, at + 6)?;
        if len_field & 0x3800 != 0 {
            return None;
        }
        let n = usize::from(len_field & 0x7FF);
        if body.len() - at < DATAGRAM_OVERHEAD + n {
            return None;
        }
        out.push(Datagram {
            command,
            index: body[at + 1],
            address: u32_le(body, at + 2)?,
            circulating: len_field & 0x4000 != 0,
            irq: u16_le(body, at + 8)?,
            data: body[at + 10..at + 10 + n].to_vec(),
            wkc: u16_le(body, at + 10 + n)?,
        });
        at += DATAGRAM_OVERHEAD + n;
        if len_field & 0x8000 == 0 {
            return (at == body.len()).then_some(out);
        }
    }
}

/// Encodes datagrams with the EtherCAT header (the "more" flag is set on all but the last). `None` when too large.
pub fn encode_payload(datagrams: &[Datagram]) -> Option<Vec<u8>> {
    let length: usize = datagrams.iter().map(Datagram::encoded_len).sum();
    if datagrams.is_empty() || length > 0x7FF || datagrams.iter().any(|d| d.data.len() > 0x7FF || d.command > 14) {
        return None;
    }
    let mut b = Vec::with_capacity(2 + length);
    b.extend_from_slice(&(u16::try_from(length).ok()? | 0x1000).to_le_bytes());
    for (i, d) in datagrams.iter().enumerate() {
        let mut len_field = u16::try_from(d.data.len()).ok()?;
        if d.circulating {
            len_field |= 0x4000;
        }
        if i + 1 < datagrams.len() {
            len_field |= 0x8000;
        }
        b.push(d.command);
        b.push(d.index);
        b.extend_from_slice(&d.address.to_le_bytes());
        b.extend_from_slice(&len_field.to_le_bytes());
        b.extend_from_slice(&d.irq.to_le_bytes());
        b.extend_from_slice(&d.data);
        b.extend_from_slice(&d.wkc.to_le_bytes());
    }
    Some(b)
}

/// Decodes a full Ethernet frame: (returned by the slaves, datagrams).
pub fn decode_ethernet(f: &[u8]) -> Option<(bool, Vec<Datagram>)> {
    if f.len() < 14 || u16::from_be_bytes([f[12], f[13]]) != ETHER_TYPE {
        return None;
    }
    Some((f[6] & 0x02 != 0, decode_payload(&f[14..])?))
}

/// Normalised description of a datagram list.
pub fn describe(datagrams: &[Datagram]) -> String {
    datagrams.iter().map(Datagram::describe).collect::<Vec<_>>().join(";")
}

/// A mailbox message.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Mailbox {
    /// Protocol (3 = CoE).
    pub kind: u8,
    /// Session counter.
    pub counter: u8,
    /// Station address.
    pub address: u16,
    /// Priority.
    pub priority: u8,
    /// Payload.
    pub payload: Vec<u8>,
}

impl Mailbox {
    /// Decodes a mailbox; trailing padding is ignored.
    pub fn decode(b: &[u8]) -> Option<Self> {
        let length = usize::from(u16_le(b, 0)?);
        if b.len() < 6 || length > b.len() - 6 {
            return None;
        }
        Some(Self { kind: b[5] & 0x0F, counter: (b[5] >> 4) & 7, address: u16_le(b, 2)?, priority: b[4] >> 6, payload: b[6..6 + length].to_vec() })
    }

    /// Encodes without padding.
    pub fn encode(&self) -> Vec<u8> {
        let mut b = Vec::with_capacity(6 + self.payload.len());
        b.extend_from_slice(&(self.payload.len() as u16).to_le_bytes());
        b.extend_from_slice(&self.address.to_le_bytes());
        b.push((self.priority & 3) << 6);
        b.push((self.kind & 0x0F) | ((self.counter & 7) << 4));
        b.extend_from_slice(&self.payload);
        b
    }

    /// Normalised description.
    pub fn describe(&self) -> String {
        format!("type={} counter={} address=0x{:04X} priority={} payload={}", self.kind, self.counter, self.address, self.priority, hex(&self.payload))
    }
}

/// A CoE SDO message.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Sdo {
    /// CoE service (2 request, 3 response).
    pub service: u8,
    /// SDO command byte.
    pub command: u8,
    /// Object index.
    pub index: u16,
    /// Sub-index.
    pub sub: u8,
    /// Data (sized for expedited and normal transfers, raw 4 bytes otherwise).
    pub data: Vec<u8>,
}

impl Sdo {
    /// Decodes a CoE header and SDO; segmented transfers are not supported.
    pub fn decode(b: &[u8]) -> Option<Self> {
        if b.len() < 10 {
            return None;
        }
        let service = (u16_le(b, 0)? >> 12) as u8;
        if service != 2 && service != 3 {
            return None;
        }
        let command = b[2];
        let raw = &b[6..];
        let ccs = command & 0xE0;
        let carries = (service == 2 && ccs == 0x20) || (service == 3 && ccs == 0x40);
        let data = if command == 0x80 || !carries {
            raw[..4].to_vec()
        } else if command & 0x02 != 0 {
            let n = if command & 0x01 != 0 { 4 - usize::from((command >> 2) & 3) } else { 4 };
            raw[..n].to_vec()
        } else {
            let n = usize::try_from(u32_le(raw, 0)?).ok()?;
            if n > raw.len() - 4 {
                return None;
            }
            raw[4..4 + n].to_vec()
        };
        Some(Self { service, command, index: u16_le(b, 3)?, sub: b[5], data })
    }

    /// Normalised description.
    pub fn describe(&self) -> String {
        let mut s = format!("service={} command=0x{:02X} index=0x{:04X} sub={} data={}", self.service, self.command, self.index, self.sub, hex(&self.data));
        if self.command == 0x80 {
            if let Some(code) = u32_le(&self.data, 0) {
                s.push_str(&format!(" abort=0x{code:08X}"));
            }
        }
        s
    }
}

/// What the master reads from an SII image.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Sii {
    /// Vendor ID.
    pub vendor: u32,
    /// Product code.
    pub product: u32,
    /// Revision.
    pub revision: u32,
    /// Serial number.
    pub serial: u32,
    /// Name from the general category.
    pub name: String,
    /// Mailbox: rx offset, rx size, tx offset, tx size, protocols.
    pub mailbox: [u16; 5],
    /// SyncManagers: start, length, control, type.
    pub sync_managers: Vec<(u16, u16, u8, u8)>,
    /// Output bits (RxPDOs).
    pub output_bits: u32,
    /// Input bits (TxPDOs).
    pub input_bits: u32,
}

impl Sii {
    /// Parses an image; `None` when shorter than the header or a category runs past the end.
    pub fn parse(img: &[u8]) -> Option<Self> {
        if img.len() < 0x80 {
            return None;
        }
        let mut sii = Self {
            vendor: u32_le(img, 0x10)?,
            product: u32_le(img, 0x14)?,
            revision: u32_le(img, 0x18)?,
            serial: u32_le(img, 0x1C)?,
            ..Self::default()
        };
        for (i, slot) in sii.mailbox.iter_mut().enumerate() {
            *slot = u16_le(img, 0x30 + i * 2)?;
        }
        let mut strings: Vec<String> = Vec::new();
        let mut name_index = 0usize;
        let mut at = 0x80;
        while at + 4 <= img.len() {
            let kind = u16_le(img, at)?;
            if kind == 0xFFFF {
                break;
            }
            let start = at + 4;
            let end = start + usize::from(u16_le(img, at + 2)?) * 2;
            let data = img.get(start..end)?;
            match kind {
                10 if !data.is_empty() => {
                    let mut pos = 1;
                    for _ in 0..data[0] {
                        let Some(&len) = data.get(pos) else { break };
                        let Some(text) = data.get(pos + 1..pos + 1 + usize::from(len)) else { break };
                        strings.push(text.iter().map(|&c| if c.is_ascii() { c as char } else { '?' }).collect());
                        pos += 1 + usize::from(len);
                    }
                }
                30 if data.len() >= 4 => name_index = usize::from(data[3]),
                41 => {
                    for e in data.chunks_exact(8) {
                        sii.sync_managers.push((u16::from_le_bytes([e[0], e[1]]), u16::from_le_bytes([e[2], e[3]]), e[4], e[7]));
                    }
                }
                50 | 51 => {
                    let mut p = 0;
                    while p + 8 <= data.len() {
                        let entries = usize::from(data[p + 2]);
                        if p + 8 + entries * 8 > data.len() {
                            break;
                        }
                        let bits: u32 = (0..entries).map(|e| u32::from(data[p + 8 + e * 8 + 5])).sum();
                        if kind == 50 {
                            sii.input_bits += bits;
                        } else {
                            sii.output_bits += bits;
                        }
                        p += 8 + entries * 8;
                    }
                }
                _ => {}
            }
            at = end;
        }
        if name_index >= 1 && name_index <= strings.len() {
            sii.name = strings[name_index - 1].clone();
        }
        Some(sii)
    }

    /// Normalised description.
    pub fn describe(&self) -> String {
        let sms = self.sync_managers.iter().map(|(s, l, c, t)| format!("{s:04X}:{l}:{c:02X}:{t}")).collect::<Vec<_>>().join(",");
        let m = &self.mailbox;
        format!(
            "vendor=0x{:08X} product=0x{:08X} revision=0x{:08X} serial={} name={} mailbox={:04X}:{},{:04X}:{},{:04X} sms={} out={} in={}",
            self.vendor, self.product, self.revision, self.serial, self.name, m[0], m[1], m[2], m[3], m[4], sms, self.output_bits, self.input_bits
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
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../../conformance/ethercat.json");
        let text = std::fs::read_to_string(path).expect("conformance/ethercat.json");
        let mut checked = 0;
        for obj in text.split("\n  {").skip(1) {
            let name = field(obj, "name");
            let expected = field(obj, "expected");
            let data = unhex(&field(obj, "hex"));
            let got = match field(obj, "kind").as_str() {
                "payload" => decode_payload(&data).map(|d| {
                    assert_eq!(encode_payload(&d).expect("encode"), data, "{name}");
                    describe(&d)
                }),
                "ethernet" => decode_ethernet(&data).map(|(r, d)| format!("returned={} {}", u8::from(r), describe(&d))),
                "mailbox" => Mailbox::decode(&data).map(|m| m.describe()),
                "sdo" => Sdo::decode(&data).map(|s| s.describe()),
                "sii" => Sii::parse(&data).map(|s| s.describe()),
                k => panic!("unknown kind {k}"),
            };
            assert_eq!(got.unwrap_or_else(|| "error".to_string()), expected, "{name}");
            checked += 1;
        }
        assert_eq!(checked, 37);
    }

    #[test]
    fn mailbox_round_trip() {
        let m = Mailbox { kind: 3, counter: 5, address: 0x1001, priority: 2, payload: vec![0, 0x20, 0x40, 0x08, 0x10, 0] };
        assert_eq!(Mailbox::decode(&m.encode()), Some(m));
    }
}
