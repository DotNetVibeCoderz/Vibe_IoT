//! Firmware images and debug structures: Intel HEX, the DFU 1.1 suffix, ST DfuSe files, DfuSe memory-layout strings
//! and the SEGGER RTT control block.
//!
//! This is the fuzzed twin of the codecs in `IoTCom.Net.Debug` (C#), kept in sync by `/conformance/firmware.json`.
//! Every decoder returns `None` for malformed input instead of panicking.

/// A contiguous block at an address.
pub type Segment = (u32, Vec<u8>);

fn u16_le(d: &[u8], i: usize) -> Option<u16> {
    Some(u16::from_le_bytes([*d.get(i)?, *d.get(i + 1)?]))
}

fn u32_le(d: &[u8], i: usize) -> Option<u32> {
    Some(u32::from_le_bytes([*d.get(i)?, *d.get(i + 1)?, *d.get(i + 2)?, *d.get(i + 3)?]))
}

/// CRC-32 (reflected 0xEDB88320) without the final inversion; start with `0xFFFF_FFFF`.
pub fn crc32_update(mut crc: u32, data: &[u8]) -> u32 {
    for &b in data {
        crc ^= u32::from(b);
        for _ in 0..8 {
            crc = if crc & 1 != 0 { 0xEDB8_8320 ^ (crc >> 1) } else { crc >> 1 };
        }
    }
    crc
}

/// Standard CRC-32.
pub fn crc32(data: &[u8]) -> u32 {
    !crc32_update(0xFFFF_FFFF, data)
}

/// A firmware image: sorted, non-overlapping segments (adjacent ones merged) and an optional entry point.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Image {
    /// Segments in address order.
    pub segments: Vec<Segment>,
    /// Entry point.
    pub entry: Option<u32>,
}

impl Image {
    /// Builds an image; `None` when segments overlap.
    pub fn new(mut segments: Vec<Segment>, entry: Option<u32>) -> Option<Self> {
        segments.retain(|(_, d)| !d.is_empty());
        segments.sort_by_key(|(a, _)| *a);
        let mut merged: Vec<Segment> = Vec::new();
        for (address, data) in segments {
            if let Some((last_address, last)) = merged.last_mut() {
                let end = u64::from(*last_address) + last.len() as u64;
                if u64::from(address) < end {
                    return None;
                }
                if u64::from(address) == end {
                    last.extend_from_slice(&data);
                    continue;
                }
            }
            merged.push((address, data));
        }
        Some(Self { segments: merged, entry })
    }

    /// Normalised description shared with the C# suite.
    pub fn describe(&self) -> String {
        let segs: Vec<String> = self.segments.iter().map(|(a, d)| format!("{a:08X}:{}:{:08X}", d.len(), crc32(d))).collect();
        let entry = self.entry.map_or_else(|| "-".to_string(), |e| format!("{e:08X}"));
        format!("segments={} entry={entry}", segs.join(","))
    }
}

/// Parses Intel HEX text (records 00–05; an end-of-file record is required and must come last).
pub fn parse_ihex(text: &str) -> Option<Image> {
    let mut segments: Vec<Segment> = Vec::new();
    let (mut upper, mut entry, mut eof) = (0u32, None, false);
    let mut current: Vec<u8> = Vec::new();
    let mut start = 0u32;
    for raw in text.split('\n') {
        let r = raw.trim();
        if r.is_empty() {
            continue;
        }
        if eof || !r.starts_with(':') || r.len() < 11 || r.len() % 2 == 0 || !r.is_char_boundary(1) {
            return None;
        }
        let hex = &r[1..];
        let b: Vec<u8> = (0..hex.len()).step_by(2).map(|i| hex.get(i..i + 2).and_then(|p| u8::from_str_radix(p, 16).ok())).collect::<Option<_>>()?;
        let count = usize::from(b[0]);
        if b.len() != count + 5 || b.iter().fold(0u8, |s, x| s.wrapping_add(*x)) != 0 {
            return None;
        }
        let offset = (u32::from(b[1]) << 8) | u32::from(b[2]);
        let data = &b[4..4 + count];
        match (b[3], count) {
            (0, _) => {
                let address = upper.wrapping_add(offset);
                if !current.is_empty() && u64::from(address) != u64::from(start) + current.len() as u64 {
                    segments.push((start, std::mem::take(&mut current)));
                }
                if current.is_empty() {
                    start = address;
                }
                current.extend_from_slice(data);
            }
            (1, _) => eof = true,
            (2, 2) => upper = ((u32::from(data[0]) << 8) | u32::from(data[1])) << 4,
            (4, 2) => upper = ((u32::from(data[0]) << 8) | u32::from(data[1])) << 16,
            (3, 4) => entry = Some((((u32::from(data[0]) << 8) | u32::from(data[1])) << 4).wrapping_add((u32::from(data[2]) << 8) | u32::from(data[3]))),
            (5, 4) => entry = Some(u32::from_be_bytes([data[0], data[1], data[2], data[3]])),
            _ => return None,
        }
    }
    if !eof {
        return None;
    }
    if !current.is_empty() {
        segments.push((start, current));
    }
    Image::new(segments, entry)
}

/// A DFU file.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DfuFile {
    /// idVendor.
    pub vendor: u16,
    /// idProduct.
    pub product: u16,
    /// bcdDevice.
    pub device: u16,
    /// bcdDFU.
    pub dfu: u16,
    /// DfuSe targets (alternate, name); empty for a plain payload.
    pub targets: Vec<(u8, String)>,
    /// Firmware (a plain payload sits at address 0).
    pub image: Image,
}

impl DfuFile {
    /// Parses a file and verifies the suffix CRC.
    pub fn parse(file: &[u8]) -> Option<Self> {
        if file.len() < 16 {
            return None;
        }
        let s = &file[file.len() - 16..];
        if &s[8..11] != b"UFD" || s[11] != 16 || crc32_update(0xFFFF_FFFF, &file[..file.len() - 4]) != u32_le(s, 12)? {
            return None;
        }
        let payload = &file[..file.len() - 16];
        let (device, product, vendor, dfu) = (u16_le(s, 0)?, u16_le(s, 2)?, u16_le(s, 4)?, u16_le(s, 6)?);
        if payload.len() < 11 || &payload[..5] != b"DfuSe" {
            return Some(Self { vendor, product, device, dfu, targets: vec![], image: Image::new(vec![(0, payload.to_vec())], None)? });
        }
        if payload[5] != 1 || u32_le(payload, 6)? as usize != payload.len() {
            return None;
        }
        let (mut targets, mut segments, mut at) = (Vec::new(), Vec::new(), 11usize);
        for _ in 0..payload[10] {
            let t = payload.get(at..at + 274)?;
            if &t[..6] != b"Target" {
                return None;
            }
            let named = u32_le(t, 7)? != 0;
            let raw = &t[11..266];
            let nul = raw.iter().position(|&c| c == 0).unwrap_or(raw.len());
            let name = if named { raw[..nul].iter().map(|&c| if c.is_ascii() { c as char } else { '?' }).collect() } else { String::new() };
            let (size, elements) = (u32_le(t, 266)? as usize, u32_le(t, 270)?);
            at += 274;
            let target_start = at;
            for _ in 0..elements {
                let address = u32_le(payload, at)?;
                let n = u32_le(payload, at + 4)? as usize;
                segments.push((address, payload.get(at + 8..at.checked_add(8)?.checked_add(n)?)?.to_vec()));
                at += 8 + n;
            }
            if at - target_start != size {
                return None;
            }
            targets.push((t[6], name));
        }
        if at != payload.len() {
            return None;
        }
        Some(Self { vendor, product, device, dfu, targets, image: Image::new(segments, None)? })
    }

    /// Normalised description.
    pub fn describe(&self) -> String {
        let targets: Vec<String> = self.targets.iter().map(|(a, n)| format!("{a}:{n}")).collect();
        format!("vid={:04X} pid={:04X} device={:04X} dfu={:04X} targets={} {}", self.vendor, self.product, self.device, self.dfu, targets.join(","), self.image.describe())
    }
}

/// Appends a DFU suffix with its CRC.
pub fn add_suffix(payload: &[u8], vendor: u16, product: u16, device: u16, dfu: u16) -> Vec<u8> {
    let mut f = payload.to_vec();
    for v in [device, product, vendor, dfu] {
        f.extend_from_slice(&v.to_le_bytes());
    }
    f.extend_from_slice(b"UFD");
    f.push(16);
    let crc = crc32_update(0xFFFF_FFFF, &f);
    f.extend_from_slice(&crc.to_le_bytes());
    f
}

/// A DfuSe memory region: start, pages, page size, type bits (1 readable, 2 erasable, 4 writable).
pub type Region = (u32, u32, u32, u8);

/// Parses a DfuSe layout string such as `@Internal Flash  /0x08000000/04*016Kg,01*064Kg`.
pub fn parse_layout(text: &str) -> Option<(String, Vec<Region>)> {
    let body = text.strip_prefix('@')?;
    let parts: Vec<&str> = body.split('/').collect();
    if parts.len() < 3 {
        return None;
    }
    let mut regions = Vec::new();
    let mut i = 1;
    while i + 1 < parts.len() {
        let a = parts[i].trim();
        let hex = a.strip_prefix("0x").or_else(|| a.strip_prefix("0X")).unwrap_or(a);
        let mut start = u32::from_str_radix(hex, 16).ok()?;
        for spec in parts[i + 1].split(',') {
            let spec = spec.trim();
            let (count, rest) = spec.split_once('*')?;
            let bytes = rest.as_bytes();
            if count.is_empty() || !count.bytes().all(|c| c.is_ascii_digit()) || bytes.len() < 3 {
                return None;
            }
            let (digits, unit, kind) = (rest.get(..rest.len() - 2)?, bytes[bytes.len() - 2], bytes[bytes.len() - 1]);
            if digits.is_empty() || !digits.bytes().all(|c| c.is_ascii_digit()) || !(b'a'..=b'g').contains(&kind) {
                return None;
            }
            let multiplier = match unit {
                b' ' => 1u32,
                b'K' => 1024,
                b'M' => 1024 * 1024,
                _ => return None,
            };
            let count: u32 = count.parse().ok()?;
            let size = digits.parse::<u32>().ok()?.checked_mul(multiplier)?;
            regions.push((start, count, size, kind - b'a' + 1));
            start = start.wrapping_add(count.wrapping_mul(size));
        }
        i += 2;
    }
    Some((parts[0].trim().to_string(), regions))
}

/// Normalised layout description.
pub fn describe_layout(name: &str, regions: &[Region]) -> String {
    let r: Vec<String> = regions
        .iter()
        .map(|(a, n, s, t)| format!("{a:08X}+{n}x{s}{}{}{}", if t & 1 != 0 { "r" } else { "" }, if t & 2 != 0 { "e" } else { "" }, if t & 4 != 0 { "w" } else { "" }))
        .collect();
    format!("{name}: {}", r.join(","))
}

/// One RTT buffer descriptor: name pointer, buffer, size, write offset, read offset, flags.
pub type RttDescriptor = [u32; 6];

/// Bytes waiting in a ring.
pub fn rtt_available(write: u32, read: u32, size: u32) -> u32 {
    if write >= read {
        write - read
    } else {
        size.wrapping_sub(read).wrapping_add(write)
    }
}

/// Parses an RTT control block: (up descriptors, down descriptors).
pub fn parse_rtt(b: &[u8]) -> Option<(Vec<RttDescriptor>, Vec<RttDescriptor>)> {
    if b.len() < 24 || &b[..16] != b"SEGGER RTT\0\0\0\0\0\0" {
        return None;
    }
    let up = i32::from_le_bytes([b[16], b[17], b[18], b[19]]);
    let down = i32::from_le_bytes([b[20], b[21], b[22], b[23]]);
    if !(0..=16).contains(&up) || !(0..=16).contains(&down) {
        return None;
    }
    let total = (up + down) as usize;
    if b.len() < 24 + total * 24 {
        return None;
    }
    let mut all = Vec::with_capacity(total);
    for i in 0..total {
        let mut d = [0u32; 6];
        for (k, slot) in d.iter_mut().enumerate() {
            *slot = u32_le(b, 24 + i * 24 + k * 4)?;
        }
        all.push(d);
    }
    let downs = all.split_off(up as usize);
    Some((all, downs))
}

/// Normalised RTT description.
pub fn describe_rtt(up: &[RttDescriptor], down: &[RttDescriptor]) -> String {
    let d = |x: &RttDescriptor| {
        let [_, buffer, size, write, read, flags] = *x;
        let ok = buffer != 0 && size > 1 && write < size && read < size;
        format!("{buffer:08X}:{size}:w{write}:r{read}:f{}:a{}:{}", flags & 3, rtt_available(write, read, size), if ok { "ok" } else { "bad" })
    };
    format!("up={} down={}", up.iter().map(d).collect::<Vec<_>>().join(","), down.iter().map(d).collect::<Vec<_>>().join(","))
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
    fn crc_check_value() {
        assert_eq!(crc32(b"123456789"), 0xCBF4_3926);
    }

    #[test]
    fn conformance_vectors() {
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../../conformance/firmware.json");
        let text = std::fs::read_to_string(path).expect("conformance/firmware.json");
        let mut checked = 0;
        for obj in text.split("\n  {").skip(1) {
            let name = field(obj, "name");
            let expected = field(obj, "expected");
            let data = unhex(&field(obj, "hex"));
            let as_text = String::from_utf8(data.clone()).unwrap_or_default();
            let got = match field(obj, "kind").as_str() {
                "ihex" => parse_ihex(&as_text).map(|i| i.describe()),
                "dfu" => DfuFile::parse(&data).map(|d| d.describe()),
                "layout" => parse_layout(&as_text).map(|(n, r)| describe_layout(&n, &r)),
                "rtt" => parse_rtt(&data).map(|(u, d)| describe_rtt(&u, &d)),
                k => panic!("unknown kind {k}"),
            };
            assert_eq!(got.unwrap_or_else(|| "error".to_string()), expected, "{name}");
            checked += 1;
        }
        assert_eq!(checked, 27);
    }

    #[test]
    fn suffix_round_trip() {
        let f = add_suffix(b"abc", 0x0483, 0xDF11, 0xFFFF, 0x0100);
        assert_eq!(DfuFile::parse(&f).expect("parses").image.segments, vec![(0, b"abc".to_vec())]);
    }
}
