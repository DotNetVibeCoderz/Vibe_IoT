//! # iotcom-mavlink
//!
//! Sans-I/O MAVLink v1/v2 framing: a streaming parser that resynchronises after garbage, checks the X.25 CRC with the
//! per-message CRC_EXTRA (supplied by a lookup, so any dialect works), understands MAVLink 2 truncation and the
//! signing trailer, and an encoder. Kept byte-for-byte in sync with the C# codec through `/conformance/mavlink*.json`
//! and fuzzed with `cargo fuzz run mavlink`.
//!
//! Built by Gravicode Studios, led by Kang Fadhil.
#![forbid(unsafe_code)]

/// MAVLink 1 start byte.
pub const STX_V1: u8 = 0xFE;
/// MAVLink 2 start byte.
pub const STX_V2: u8 = 0xFD;
/// MAVLink 2 incompatibility flag: the frame is signed.
pub const INCOMPAT_SIGNED: u8 = 0x01;

/// X.25 (CRC-16/MCRF4XX) accumulate.
pub fn crc_accumulate(mut crc: u16, data: &[u8]) -> u16 {
    for &b in data {
        let mut t = b ^ (crc as u8);
        t ^= t << 4;
        crc = (crc >> 8) ^ (u16::from(t) << 8) ^ (u16::from(t) << 3) ^ (u16::from(t) >> 4);
    }
    crc
}

/// Facts the parser needs about a message id: CRC_EXTRA and the full payload length (for zero-extension).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct MessageInfo {
    /// CRC_EXTRA seed.
    pub crc_extra: u8,
    /// Payload length including extensions.
    pub max_len: u8,
}

/// A decoded frame.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Frame {
    /// 1 or 2.
    pub version: u8,
    /// Sequence number.
    pub seq: u8,
    /// Sender system id.
    pub sys_id: u8,
    /// Sender component id.
    pub comp_id: u8,
    /// Message id.
    pub msg_id: u32,
    /// Payload, zero-extended to the full length when the dialect knows it.
    pub payload: Vec<u8>,
    /// Link id, timestamp and 6-byte signature of a signed frame (not verified here).
    pub signature: Option<(u8, u64, [u8; 6])>,
}

/// Counters of the parser.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Stats {
    /// Frames with a wrong checksum.
    pub crc_errors: u64,
    /// Frames whose id the lookup did not know.
    pub unknown: u64,
    /// Bytes skipped while searching for a start byte.
    pub discarded: u64,
}

/// Streaming parser.
pub struct Parser<F: Fn(u32) -> Option<MessageInfo>> {
    lookup: F,
    buf: Vec<u8>,
    start: usize,
    /// Counters.
    pub stats: Stats,
}

impl<F: Fn(u32) -> Option<MessageInfo>> Parser<F> {
    /// Creates a parser; `lookup` returns CRC_EXTRA and length for a message id.
    pub fn new(lookup: F) -> Self {
        Self { lookup, buf: Vec::with_capacity(1024), start: 0, stats: Stats::default() }
    }

    /// Appends received bytes.
    pub fn feed(&mut self, data: &[u8]) {
        if self.start > 0 && (self.start == self.buf.len() || self.start > 4096) {
            self.buf.drain(..self.start);
            self.start = 0;
        }
        self.buf.extend_from_slice(data);
    }

    /// Returns the next valid frame, or `None` when more bytes are needed.
    pub fn next_frame(&mut self) -> Option<Frame> {
        loop {
            let span = &self.buf[self.start..];
            let Some(stx) = span.iter().position(|&b| b == STX_V1 || b == STX_V2) else {
                self.stats.discarded += span.len() as u64;
                self.start = self.buf.len();
                return None;
            };
            self.stats.discarded += stx as u64;
            self.start += stx;
            let span = &self.buf[self.start..];
            let v2 = span[0] == STX_V2;
            let header = if v2 { 10 } else { 6 };
            if span.len() < header {
                return None;
            }
            let len = usize::from(span[1]);
            if v2 && span[2] & !INCOMPAT_SIGNED != 0 {
                self.start += 1;
                continue;
            }
            let signed = v2 && span[2] & INCOMPAT_SIGNED != 0;
            let total = header + len + 2 + if signed { 13 } else { 0 };
            if span.len() < total {
                return None;
            }
            let msg_id = if v2 { u32::from(span[7]) | u32::from(span[8]) << 8 | u32::from(span[9]) << 16 } else { u32::from(span[5]) };
            let Some(info) = (self.lookup)(msg_id) else {
                self.stats.unknown += 1;
                self.start += 1;
                continue;
            };
            let crc = crc_accumulate(crc_accumulate(0xFFFF, &span[1..header + len]), &[info.crc_extra]);
            if crc != u16::from_le_bytes([span[header + len], span[header + len + 1]]) {
                self.stats.crc_errors += 1;
                self.start += 1;
                continue;
            }
            let mut payload = span[header..header + len].to_vec();
            if payload.len() < usize::from(info.max_len) {
                payload.resize(usize::from(info.max_len), 0);
            }
            let signature = signed.then(|| {
                let s = &span[header + len + 2..total];
                let ts = s[1..7].iter().rev().fold(0u64, |acc, &b| (acc << 8) | u64::from(b));
                let mut sig = [0u8; 6];
                sig.copy_from_slice(&s[7..13]);
                (s[0], ts, sig)
            });
            let frame = Frame {
                version: if v2 { 2 } else { 1 },
                seq: span[if v2 { 4 } else { 2 }],
                sys_id: span[if v2 { 5 } else { 3 }],
                comp_id: span[if v2 { 6 } else { 4 }],
                msg_id,
                payload,
                signature,
            };
            self.start += total;
            return Some(frame);
        }
    }
}

/// Header fields of a frame to encode.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Header {
    /// 1 or 2.
    pub version: u8,
    /// Sequence number.
    pub seq: u8,
    /// Sender system id.
    pub sys_id: u8,
    /// Sender component id.
    pub comp_id: u8,
    /// Message id.
    pub msg_id: u32,
}

impl Frame {
    /// The header of this frame.
    pub fn header(&self) -> Header {
        Header { version: self.version, seq: self.seq, sys_id: self.sys_id, comp_id: self.comp_id, msg_id: self.msg_id }
    }
}

/// Encodes an unsigned frame. MAVLink 2 truncates trailing zero bytes (keeping at least one).
///
/// # Panics
/// When the payload exceeds 255 bytes, or a MAVLink 1 frame carries an id above 255 (caller bugs).
pub fn encode(h: &Header, payload: &[u8], crc_extra: u8, out: &mut Vec<u8>) {
    let Header { version, seq, sys_id, comp_id, msg_id } = *h;
    assert!(payload.len() <= 255, "payload longer than 255 bytes");
    let start = out.len();
    if version == 1 {
        assert!(msg_id <= 255, "MAVLink 1 ids are 8-bit");
        out.extend_from_slice(&[STX_V1, payload.len() as u8, seq, sys_id, comp_id, msg_id as u8]);
        out.extend_from_slice(payload);
    } else {
        let mut len = payload.len();
        while len > 1 && payload[len - 1] == 0 {
            len -= 1;
        }
        let id = msg_id.to_le_bytes();
        out.extend_from_slice(&[STX_V2, len as u8, 0, 0, seq, sys_id, comp_id, id[0], id[1], id[2]]);
        out.extend_from_slice(&payload[..len]);
    }
    let crc = crc_accumulate(crc_accumulate(0xFFFF, &out[start + 1..]), &[crc_extra]);
    out.extend_from_slice(&crc.to_le_bytes());
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;

    fn hex(s: &str) -> Vec<u8> {
        (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
    }

    fn objects(text: &str) -> Vec<&str> {
        text.split('{').skip(1).collect()
    }

    fn string(obj: &str, key: &str) -> String {
        let pat = format!("\"{key}\": \"");
        let start = obj.find(&pat).map(|i| i + pat.len()).expect(key);
        obj[start..start + obj[start..].find('"').unwrap()].to_string()
    }

    fn number(obj: &str, key: &str) -> u32 {
        let pat = format!("\"{key}\": ");
        let start = obj.find(&pat).map(|i| i + pat.len()).expect(key);
        obj[start..].chars().take_while(char::is_ascii_digit).collect::<String>().parse().unwrap()
    }

    fn table() -> HashMap<u32, MessageInfo> {
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../../conformance/mavlink_messages.json");
        let text = std::fs::read_to_string(path).expect("conformance/mavlink_messages.json");
        objects(&text)
            .into_iter()
            .map(|o| (number(o, "id"), MessageInfo { crc_extra: number(o, "crcExtra") as u8, max_len: number(o, "maxLength") as u8 }))
            .collect()
    }

    /// Runs the shared vectors in /conformance/mavlink.json.
    #[test]
    fn conformance_vectors() {
        let infos = table();
        assert!(infos.len() > 200);
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../../conformance/mavlink.json");
        let text = std::fs::read_to_string(path).expect("conformance/mavlink.json");
        let mut checked = 0;
        for o in objects(&text) {
            let name = string(o, "name");
            let frame = hex(&string(o, "frame"));
            let mut p = Parser::new(|id| infos.get(&id).copied());
            p.feed(&frame);
            let f = p.next_frame().unwrap_or_else(|| panic!("{name}: not parsed"));
            assert_eq!(u32::from(f.version), number(o, "version"), "{name}");
            assert_eq!(u32::from(f.seq), number(o, "sequence"), "{name}");
            assert_eq!(u32::from(f.sys_id), number(o, "systemId"), "{name}");
            assert_eq!(f.msg_id, number(o, "messageId"), "{name}");
            assert_eq!(f.payload, hex(&string(o, "payload")), "{name}: zero-extended payload");
            assert_eq!(f.signature.is_some(), o.contains("\"signed\": true"), "{name}");
            if f.signature.is_none() {
                let mut out = Vec::new();
                encode(&f.header(), &f.payload, infos[&f.msg_id].crc_extra, &mut out);
                assert_eq!(out, frame, "{name}: re-encode");
            }
            checked += 1;
        }
        assert_eq!(checked, 10);
    }

    #[test]
    fn resync_after_garbage_and_bad_crc() {
        let infos = table();
        let mut good = Vec::new();
        encode(&Header { version: 2, seq: 1, sys_id: 1, comp_id: 1, msg_id: 30 }, &[0x11; 28], infos[&30].crc_extra, &mut good);
        let mut bad = good.clone();
        *bad.last_mut().unwrap() ^= 0xFF;
        let mut p = Parser::new(|id| infos.get(&id).copied());
        for b in [&[0x00u8, 0xFD, 0x01, 0x02][..], &bad, &[0x55], &good].concat() {
            p.feed(&[b]);
        }
        let f = p.next_frame().expect("good frame");
        assert_eq!(f.msg_id, 30);
        assert!(p.next_frame().is_none());
        assert_eq!(p.stats.crc_errors, 1);
    }
}
