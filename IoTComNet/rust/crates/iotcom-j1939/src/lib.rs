//! # iotcom-j1939
//!
//! SAE J1939 codecs: 29-bit identifiers (priority, PGN, destination, source), NAME, transport-protocol
//! connection management (TP.CM), diagnostic trouble codes and SPN scaling for common PGNs. The runtime (address
//! claim, BAM and RTS/CTS sessions, requests) is managed C# in `IoTCom.Net.Protocols.J1939`; this crate is its twin,
//! kept in sync by `/conformance/j1939.json` and fuzzed with `cargo fuzz run j1939`.
#![forbid(unsafe_code)]

use std::fmt;

/// A split 29-bit identifier.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Id {
    /// Priority 0–7.
    pub priority: u8,
    /// Parameter group number.
    pub pgn: u32,
    /// Destination (0xFF global).
    pub destination: u8,
    /// Source address.
    pub source: u8,
}

impl Id {
    /// Splits a CAN identifier.
    pub fn from_can_id(id: u32) -> Self {
        let dp_pf = (id >> 16) & 0x3FF;
        let pf = (dp_pf & 0xFF) as u8;
        let ps = (id >> 8) as u8;
        let (pgn, destination) = if pf < 240 { (dp_pf << 8, ps) } else { ((dp_pf << 8) | u32::from(ps), 0xFF) };
        Self { priority: ((id >> 26) & 7) as u8, pgn, destination, source: id as u8 }
    }

    /// Rebuilds the CAN identifier.
    pub fn to_can_id(self) -> u32 {
        let pf = (self.pgn >> 8) as u8;
        let ps = if pf < 240 { self.destination } else { self.pgn as u8 };
        (u32::from(self.priority & 7) << 26) | ((self.pgn & 0x3FF00) << 8) | (u32::from(ps) << 8) | u32::from(self.source)
    }
}

/// A J1939 NAME.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Name {
    /// 21 bits.
    pub identity: u32,
    /// 11 bits.
    pub manufacturer: u16,
    /// 3 bits.
    pub ecu_instance: u8,
    /// 5 bits.
    pub function_instance: u8,
    /// 8 bits.
    pub function: u8,
    /// 7 bits.
    pub vehicle_system: u8,
    /// 4 bits.
    pub vehicle_system_instance: u8,
    /// 3 bits.
    pub industry_group: u8,
    /// Arbitrary address capable.
    pub arbitrary: bool,
}

impl Name {
    /// Decodes 8 little-endian bytes.
    pub fn decode(d: &[u8]) -> Option<Self> {
        let v = u64::from_le_bytes(d.get(..8)?.try_into().ok()?);
        Some(Self {
            identity: (v & 0x1F_FFFF) as u32,
            manufacturer: ((v >> 21) & 0x7FF) as u16,
            ecu_instance: ((v >> 32) & 7) as u8,
            function_instance: ((v >> 35) & 0x1F) as u8,
            function: (v >> 40) as u8,
            vehicle_system: ((v >> 49) & 0x7F) as u8,
            vehicle_system_instance: ((v >> 56) & 0xF) as u8,
            industry_group: ((v >> 60) & 7) as u8,
            arbitrary: v >> 63 != 0,
        })
    }

    /// The 64-bit value.
    pub fn value(&self) -> u64 {
        u64::from(self.identity & 0x1F_FFFF)
            | (u64::from(self.manufacturer & 0x7FF) << 21)
            | (u64::from(self.ecu_instance & 7) << 32)
            | (u64::from(self.function_instance & 0x1F) << 35)
            | (u64::from(self.function) << 40)
            | (u64::from(self.vehicle_system & 0x7F) << 49)
            | (u64::from(self.vehicle_system_instance & 0xF) << 56)
            | (u64::from(self.industry_group & 7) << 60)
            | (u64::from(self.arbitrary) << 63)
    }
}

/// A diagnostic trouble code (SPN conversion method 0).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Dtc {
    /// SPN (19 bits).
    pub spn: u32,
    /// FMI.
    pub fmi: u8,
    /// Occurrence count.
    pub occurrences: u8,
}

impl Dtc {
    /// Decodes 4 bytes.
    pub fn decode(d: &[u8]) -> Option<Self> {
        let d = d.get(..4)?;
        Some(Self { spn: u32::from(d[0]) | (u32::from(d[1]) << 8) | (u32::from(d[2] >> 5) << 16), fmi: d[2] & 0x1F, occurrences: d[3] & 0x7F })
    }

    /// Encodes 4 bytes.
    pub fn encode(&self) -> [u8; 4] {
        [self.spn as u8, (self.spn >> 8) as u8, (((self.spn >> 16) as u8) << 5) | (self.fmi & 0x1F), self.occurrences & 0x7F]
    }
}

/// TP.CM control bytes.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Control {
    /// Request to send.
    RequestToSend,
    /// Clear to send.
    ClearToSend,
    /// End of message acknowledgement.
    EndOfMessageAck,
    /// Broadcast announce.
    Broadcast,
    /// Abort.
    Abort,
}

impl fmt::Display for Control {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        fmt::Debug::fmt(self, f)
    }
}

/// A TP.CM message.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Tp {
    /// Control.
    pub control: Control,
    /// Message size.
    pub size: u16,
    /// Packets (total, or to send for CTS).
    pub packets: u8,
    /// Next packet (CTS).
    pub next: u8,
    /// Max packets per CTS (RTS).
    pub max_per_cts: u8,
    /// Abort reason.
    pub reason: u8,
    /// PGN of the transported message.
    pub pgn: u32,
}

impl Tp {
    /// Decodes 8 bytes.
    pub fn decode(d: &[u8]) -> Option<Self> {
        let d = d.get(..8)?;
        let size = u16::from_le_bytes([d[1], d[2]]);
        let pgn = u32::from(d[5]) | (u32::from(d[6]) << 8) | (u32::from(d[7]) << 16);
        let base = Self { control: Control::Abort, size: 0, packets: 0, next: 0, max_per_cts: 0, reason: 0, pgn };
        Some(match d[0] {
            16 => Self { control: Control::RequestToSend, size, packets: d[3], max_per_cts: d[4], ..base },
            17 => Self { control: Control::ClearToSend, packets: d[1], next: d[2], ..base },
            19 => Self { control: Control::EndOfMessageAck, size, packets: d[3], ..base },
            32 => Self { control: Control::Broadcast, size, packets: d[3], ..base },
            255 => Self { reason: d[1], ..base },
            _ => return None,
        })
    }

    /// Encodes 8 bytes (reserved bytes 0xFF).
    pub fn encode(&self) -> [u8; 8] {
        let mut b = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, self.pgn as u8, (self.pgn >> 8) as u8, (self.pgn >> 16) as u8];
        let size = self.size.to_le_bytes();
        match self.control {
            Control::RequestToSend => {
                b[0] = 16;
                b[1..3].copy_from_slice(&size);
                b[3] = self.packets;
                b[4] = self.max_per_cts;
            }
            Control::ClearToSend => {
                b[0] = 17;
                b[1] = self.packets;
                b[2] = self.next;
            }
            Control::EndOfMessageAck | Control::Broadcast => {
                b[0] = if self.control == Control::Broadcast { 32 } else { 19 };
                b[1..3].copy_from_slice(&size);
                b[3] = self.packets;
            }
            Control::Abort => {
                b[0] = 255;
                b[1] = self.reason;
            }
        }
        b
    }
}

/// SPN layout: (spn, byte offset, length 0=nibble/1/2/4, scale, offset).
type SpnDef = (u32, usize, usize, f64, f64);

fn definitions(pgn: u32) -> &'static [SpnDef] {
    match pgn {
        0xF004 => &[(899, 0, 0, 1.0, 0.0), (512, 1, 1, 1.0, -125.0), (513, 2, 1, 1.0, -125.0), (190, 3, 2, 0.125, 0.0), (1483, 5, 1, 1.0, 0.0)],
        0xF003 => &[(91, 1, 1, 0.4, 0.0), (92, 2, 1, 1.0, 0.0)],
        0xFEF1 => &[(84, 1, 2, 1.0 / 256.0, 0.0)],
        0xFEEE => &[(110, 0, 1, 1.0, -40.0), (174, 1, 1, 1.0, -40.0), (175, 2, 2, 0.03125, -273.0)],
        0xFEEF => &[(94, 0, 1, 4.0, 0.0), (98, 2, 1, 0.4, 0.0), (100, 3, 1, 4.0, 0.0)],
        0xFEF2 => &[(183, 0, 2, 0.05, 0.0), (51, 6, 1, 0.4, 0.0)],
        0xFEF7 => &[(167, 2, 2, 0.05, 0.0), (168, 4, 2, 0.05, 0.0)],
        0xFEE5 => &[(247, 0, 4, 0.05, 0.0), (249, 4, 4, 1000.0, 0.0)],
        _ => &[],
    }
}

/// Decodes the SPNs of a PGN: `(spn, value)` with `None` for not-available/error raw values.
pub fn decode_spns(pgn: u32, d: &[u8]) -> Vec<(u32, Option<f64>)> {
    definitions(pgn)
        .iter()
        .map(|&(spn, at, len, scale, offset)| {
            if len == 0 {
                return (spn, d.first().map(|b| f64::from(b & 0x0F)));
            }
            let Some(bytes) = d.get(at..at + len) else { return (spn, None) };
            let (raw, max) = match len {
                1 => (u64::from(bytes[0]), 0xFA),
                2 => (u64::from(u16::from_le_bytes([bytes[0], bytes[1]])), 0xFAFF),
                _ => (u64::from(u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]])), 0xFAFF_FFFF),
            };
            let value = (raw <= max).then(|| ((raw as f64 * scale + offset) * 1e6).round() / 1e6);
            (spn, value)
        })
        .collect()
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

    fn number(obj: &str, key: &str) -> u32 {
        let pat = format!("\"{key}\": ");
        let start = obj.find(&pat).expect(key) + pat.len();
        obj[start..].chars().take_while(char::is_ascii_digit).collect::<String>().parse().expect("number")
    }

    fn render(v: f64) -> String {
        // Matches .NET's invariant shortest round-trip rendering for these values.
        let s = format!("{v}");
        s.strip_suffix(".0").map(str::to_string).unwrap_or(s)
    }

    /// Runs the shared vectors in /conformance/j1939.json.
    #[test]
    fn conformance_vectors() {
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../../conformance/j1939.json");
        let text = std::fs::read_to_string(path).expect("conformance/j1939.json");
        let mut checked = 0;
        for obj in text.split("\n  {").skip(1) {
            let name = field(obj, "name");
            let expected = field(obj, "fields");
            match field(obj, "kind").as_str() {
                "id" => {
                    let can_id = number(obj, "can_id");
                    let id = Id::from_can_id(can_id);
                    assert_eq!(format!("{}|{}|{}|{}", id.priority, id.pgn, id.destination, id.source), expected, "{name}");
                    assert_eq!(id.to_can_id(), can_id, "{name}");
                }
                "name" => {
                    let data = hex(&field(obj, "data"));
                    let n = Name::decode(&data).expect("name");
                    let rendered = format!(
                        "{}|{}|{}|{}|{}|{}|{}|{}|{}",
                        n.identity, n.manufacturer, n.ecu_instance, n.function_instance, n.function, n.vehicle_system, n.vehicle_system_instance, n.industry_group, u8::from(n.arbitrary)
                    );
                    assert_eq!(rendered, expected, "{name}");
                    assert_eq!(n.value().to_le_bytes().to_vec(), data, "{name}");
                }
                "dtc" => {
                    let data = hex(&field(obj, "data"));
                    let d = Dtc::decode(&data).expect("dtc");
                    assert_eq!(format!("{}|{}|{}", d.spn, d.fmi, d.occurrences), expected, "{name}");
                    assert_eq!(d.encode().to_vec(), data, "{name}");
                }
                "tp" => {
                    let data = hex(&field(obj, "data"));
                    let t = Tp::decode(&data).expect("tp");
                    assert_eq!(format!("{}|{}|{}|{}|{}|{}|{}", t.control, t.size, t.packets, t.next, t.max_per_cts, t.reason, t.pgn), expected, "{name}");
                    assert_eq!(t.encode().to_vec(), data, "{name}");
                }
                "spn" => {
                    let values = decode_spns(number(obj, "pgn"), &hex(&field(obj, "data")));
                    let rendered: Vec<String> = values.iter().map(|(s, v)| format!("{s}={}", v.map_or("na".to_string(), render))).collect();
                    assert_eq!(rendered.join("|"), expected, "{name}");
                }
                other => panic!("{name}: unknown kind {other}"),
            }
            checked += 1;
        }
        assert!(checked >= 28, "checked {checked}");
    }
}
