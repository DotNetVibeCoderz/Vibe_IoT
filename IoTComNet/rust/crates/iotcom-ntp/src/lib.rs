//! NTP packet codec (RFC 5905 §7.3), era-aware timestamp conversion (RFC 4330) and the on-wire offset/delay math.
//!
//! This is the fuzzed twin of `IoTCom.Net.Protocols.Ntp` (C#), kept in sync by `/conformance/ntp.json` and fuzzed
//! with `cargo fuzz run ntp`.
#![forbid(unsafe_code)]

/// Header length in octets.
pub const HEADER_LENGTH: usize = 48;

/// A 64-bit NTP timestamp.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Timestamp(pub u64);

impl Timestamp {
    /// Seconds part.
    pub fn seconds(self) -> u32 {
        (self.0 >> 32) as u32
    }

    /// Fraction part (2⁻³² s).
    pub fn fraction(self) -> u32 {
        self.0 as u32
    }

    /// 100 ns ticks since 1900-01-01 (era 0) or 2036-02-07 06:28:16 (era 1), with the era flag. The fraction is
    /// rounded to the nearest tick, like the C# codec.
    pub fn ticks(self) -> (bool, u64) {
        let era1 = self.seconds() & 0x8000_0000 == 0;
        let frac = (u64::from(self.fraction()) * 10_000_000 + (1 << 31)) >> 32;
        (era1, u64::from(self.seconds()) * 10_000_000 + frac)
    }

    /// `a − b` in units of 2⁻³² s, using on-wire (signed 64-bit) arithmetic.
    pub fn difference(a: Timestamp, b: Timestamp) -> i64 {
        a.0.wrapping_sub(b.0) as i64
    }
}

/// Offset θ and delay δ of one exchange, in units of 2⁻³² s (delay clamped at zero).
pub fn offset_delay(t1: Timestamp, t2: Timestamp, t3: Timestamp, t4: Timestamp) -> (f64, f64) {
    let offset = (Timestamp::difference(t2, t1) as f64 + Timestamp::difference(t3, t4) as f64) / 2.0;
    let delay = (Timestamp::difference(t4, t1) as f64 - Timestamp::difference(t3, t2) as f64).max(0.0);
    (offset, delay)
}

/// An NTP packet.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Packet {
    /// Leap indicator.
    pub leap: u8,
    /// Version.
    pub version: u8,
    /// Mode.
    pub mode: u8,
    /// Stratum.
    pub stratum: u8,
    /// Poll exponent.
    pub poll: i8,
    /// Precision exponent.
    pub precision: i8,
    /// Root delay, NTP short format.
    pub root_delay: u32,
    /// Root dispersion, NTP short format.
    pub root_dispersion: u32,
    /// Reference identifier.
    pub reference_id: u32,
    /// Reference timestamp.
    pub reference: Timestamp,
    /// Originate timestamp.
    pub originate: Timestamp,
    /// Receive timestamp.
    pub receive: Timestamp,
    /// Transmit timestamp.
    pub transmit: Timestamp,
    /// Extension fields and MAC.
    pub trailer: Vec<u8>,
}

fn u32_at(d: &[u8], i: usize) -> u32 {
    u32::from_be_bytes([d[i], d[i + 1], d[i + 2], d[i + 3]])
}

fn u64_at(d: &[u8], i: usize) -> u64 {
    (u64::from(u32_at(d, i)) << 32) | u64::from(u32_at(d, i + 4))
}

impl Packet {
    /// Parses a packet; `None` when shorter than the header.
    pub fn decode(d: &[u8]) -> Option<Self> {
        if d.len() < HEADER_LENGTH {
            return None;
        }
        Some(Packet {
            leap: d[0] >> 6,
            version: (d[0] >> 3) & 7,
            mode: d[0] & 7,
            stratum: d[1],
            poll: d[2] as i8,
            precision: d[3] as i8,
            root_delay: u32_at(d, 4),
            root_dispersion: u32_at(d, 8),
            reference_id: u32_at(d, 12),
            reference: Timestamp(u64_at(d, 16)),
            originate: Timestamp(u64_at(d, 24)),
            receive: Timestamp(u64_at(d, 32)),
            transmit: Timestamp(u64_at(d, 40)),
            trailer: d[HEADER_LENGTH..].to_vec(),
        })
    }

    /// Encodes the packet.
    pub fn encode(&self) -> Vec<u8> {
        let mut out = vec![(self.leap << 6) | ((self.version & 7) << 3) | (self.mode & 7), self.stratum, self.poll as u8, self.precision as u8];
        for v in [self.root_delay, self.root_dispersion, self.reference_id] {
            out.extend_from_slice(&v.to_be_bytes());
        }
        for t in [self.reference, self.originate, self.receive, self.transmit] {
            out.extend_from_slice(&t.0.to_be_bytes());
        }
        out.extend_from_slice(&self.trailer);
        out
    }

    /// A kiss-o'-death packet (stratum 0 from a server, broadcast or symmetric-passive peer).
    pub fn is_kiss_of_death(&self) -> bool {
        self.stratum == 0 && matches!(self.mode, 2 | 4 | 5)
    }

    /// The reference identifier as text: ASCII for stratum 0–1, dotted quad above.
    pub fn reference_text(&self) -> String {
        let b = self.reference_id.to_be_bytes();
        if self.stratum <= 1 {
            let n = b.iter().position(|&c| c == 0).unwrap_or(4);
            if b[..n].iter().all(|&c| (0x20..=0x7E).contains(&c)) {
                return b[..n].iter().map(|&c| c as char).collect();
            }
            return format!("0x{:08X}", self.reference_id);
        }
        format!("{}.{}.{}.{}", b[0], b[1], b[2], b[3])
    }
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

    fn ts(obj: &str, key: &str) -> Timestamp {
        Timestamp(u64::from_str_radix(&field(obj, key), 16).expect("timestamp"))
    }

    /// Civil date from days since 1970-01-01 (Howard Hinnant's algorithm), for the timestamp vectors.
    fn civil(days: i64) -> (i64, u32, u32) {
        let z = days + 719_468;
        let era = z.div_euclid(146_097);
        let doe = z.rem_euclid(146_097);
        let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
        let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        let mp = (5 * doy + 2) / 153;
        let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
        let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
        (yoe + era * 400 + i64::from(m <= 2), m, d)
    }

    fn render(t: Timestamp) -> String {
        let (era1, ticks) = t.ticks();
        // Era 0 starts 2 208 988 800 s before the Unix epoch; era 1 starts 2^32 s after era 0.
        let base: i64 = if era1 { (1i64 << 32) - 2_208_988_800 } else { -2_208_988_800 };
        let total = base * 10_000_000 + ticks as i64;
        let secs = total.div_euclid(10_000_000);
        let rest = total.rem_euclid(10_000_000);
        let (y, m, d) = civil(secs.div_euclid(86_400));
        let s = secs.rem_euclid(86_400);
        format!("{y:04}-{m:02}-{d:02}T{:02}:{:02}:{:02}.{rest:07}", s / 3600, (s / 60) % 60, s % 60)
    }

    #[test]
    fn conformance_vectors() {
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../../conformance/ntp.json");
        let text = std::fs::read_to_string(path).expect("conformance/ntp.json");
        let mut checked = 0;
        for obj in text.split("\n  {").skip(1) {
            let name = field(obj, "name");
            let expected = field(obj, "fields");
            match field(obj, "kind").as_str() {
                "packet" => {
                    let data = hex(&field(obj, "data"));
                    let p = Packet::decode(&data);
                    if expected == "error" {
                        assert!(p.is_none(), "{name}");
                    } else {
                        let p = p.expect("packet");
                        let got = format!(
                            "{}|{}|{}|{}|{}|{}|{}|{}|{}|{:016X}|{:016X}|{:016X}|{:016X}|{}",
                            p.leap,
                            p.version,
                            p.mode,
                            p.stratum,
                            p.poll,
                            p.precision,
                            p.root_delay,
                            p.root_dispersion,
                            p.reference_text(),
                            p.reference.0,
                            p.originate.0,
                            p.receive.0,
                            p.transmit.0,
                            p.trailer.len()
                        );
                        assert_eq!(got, expected, "{name}");
                        assert_eq!(p.encode(), data, "{name}: round trip");
                    }
                }
                "timestamp" => assert_eq!(render(ts(obj, "raw")), expected, "{name}"),
                "exchange" => {
                    let (o, d) = offset_delay(ts(obj, "t1"), ts(obj, "t2"), ts(obj, "t3"), ts(obj, "t4"));
                    let to_ticks = |v: f64| (v * 10_000_000.0 / 4_294_967_296.0).round() as i64;
                    let mut parts = expected.split('|').map(|x| x.parse::<i64>().expect("ticks"));
                    let (eo, ed) = (parts.next().expect("offset"), parts.next().expect("delay"));
                    assert!((to_ticks(o) - eo).abs() <= 1, "{name}: offset {} vs {eo}", to_ticks(o));
                    assert!((to_ticks(d) - ed).abs() <= 1, "{name}: delay {} vs {ed}", to_ticks(d));
                }
                other => panic!("{name}: unknown kind {other}"),
            }
            checked += 1;
        }
        assert!(checked >= 25, "checked {checked}");
    }
}
