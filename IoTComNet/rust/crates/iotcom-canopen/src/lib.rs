//! # iotcom-canopen
//!
//! CANopen (CiA 301) codecs: COB-ID classification, SDO frames (expedited, segmented, abort) and emergency
//! messages. The runtime (NMT, SDO client/server, PDO, heartbeat) is managed C# in `IoTCom.Net.Protocols.CanOpen`;
//! this crate is its twin, kept in sync by `/conformance/canopen.json` and fuzzed with `cargo fuzz run canopen`.
#![forbid(unsafe_code)]

use std::fmt;

/// Communication objects of the predefined connection set.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Function {
    /// NMT command.
    Nmt,
    /// SYNC.
    Sync,
    /// Emergency.
    Emergency,
    /// Time stamp.
    Time,
    /// Transmit PDO.
    Tpdo,
    /// Receive PDO.
    Rpdo,
    /// SDO server to client.
    SdoResponse,
    /// SDO client to server.
    SdoRequest,
    /// Heartbeat / boot-up.
    Heartbeat,
    /// LSS.
    Lss,
    /// Not in the predefined set.
    Other,
}

impl fmt::Display for Function {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        fmt::Debug::fmt(self, f)
    }
}

/// Classifies a COB-ID: function, node id and PDO number (1–4, 0 otherwise).
pub fn classify(cob_id: u32) -> (Function, u8, u8) {
    match cob_id {
        0x000 => return (Function::Nmt, 0, 0),
        0x080 => return (Function::Sync, 0, 0),
        0x100 => return (Function::Time, 0, 0),
        0x7E4 | 0x7E5 => return (Function::Lss, 0, 0),
        id if id > 0x7FF => return (Function::Other, 0, 0),
        _ => {}
    }
    let node = (cob_id & 0x7F) as u8;
    if node == 0 {
        return (Function::Other, 0, 0);
    }
    match cob_id & 0x780 {
        0x080 => (Function::Emergency, node, 0),
        0x180 => (Function::Tpdo, node, 1),
        0x200 => (Function::Rpdo, node, 1),
        0x280 => (Function::Tpdo, node, 2),
        0x300 => (Function::Rpdo, node, 2),
        0x380 => (Function::Tpdo, node, 3),
        0x400 => (Function::Rpdo, node, 3),
        0x480 => (Function::Tpdo, node, 4),
        0x500 => (Function::Rpdo, node, 4),
        0x580 => (Function::SdoResponse, node, 0),
        0x600 => (Function::SdoRequest, node, 0),
        0x700 => (Function::Heartbeat, node, 0),
        _ => (Function::Other, 0, 0),
    }
}

/// SDO frame kinds.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SdoKind {
    /// Client: initiate upload.
    InitiateUploadRequest,
    /// Server: initiate upload response.
    InitiateUploadResponse,
    /// Client: upload segment request.
    UploadSegmentRequest,
    /// Server: upload segment.
    UploadSegmentResponse,
    /// Client: initiate download.
    InitiateDownloadRequest,
    /// Server: initiate download response.
    InitiateDownloadResponse,
    /// Client: download segment.
    DownloadSegmentRequest,
    /// Server: download segment response.
    DownloadSegmentResponse,
    /// Abort.
    Abort,
}

impl fmt::Display for SdoKind {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        fmt::Debug::fmt(self, f)
    }
}

/// A decoded SDO frame.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Sdo {
    /// Kind.
    pub kind: SdoKind,
    /// Object index.
    pub index: u16,
    /// Sub-index.
    pub sub: u8,
    /// Expedited.
    pub expedited: bool,
    /// Size indicated.
    pub size_indicated: bool,
    /// Size of a segmented transfer.
    pub size: u32,
    /// Toggle bit.
    pub toggle: bool,
    /// Last segment.
    pub last: bool,
    /// Data of this frame.
    pub data: Vec<u8>,
    /// Abort code.
    pub abort: u32,
}

impl Sdo {
    fn new(kind: SdoKind) -> Self {
        Self { kind, index: 0, sub: 0, expedited: false, size_indicated: false, size: 0, toggle: false, last: false, data: Vec::new(), abort: 0 }
    }
}

/// Decoding errors.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Error {
    /// Fewer than 8 bytes.
    Short(usize),
    /// Unsupported command specifier (block transfer or reserved).
    Unsupported(u8),
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Short(n) => write!(f, "an SDO frame has 8 data bytes, got {n}"),
            Self::Unsupported(c) => write!(f, "SDO command specifier 0x{c:02X} is not supported"),
        }
    }
}

impl std::error::Error for Error {}

/// Decodes 8 SDO data bytes; `from_server` selects the direction (0x580 vs 0x600 COB-IDs).
pub fn decode_sdo(d: &[u8], from_server: bool) -> Result<Sdo, Error> {
    if d.len() < 8 {
        return Err(Error::Short(d.len()));
    }
    let cmd = d[0];
    let index = u16::from_le_bytes([d[1], d[2]]);
    let sub = d[3];
    let u32_at = |i: usize| u32::from_le_bytes([d[i], d[i + 1], d[i + 2], d[i + 3]]);
    let cs = cmd >> 5;
    let toggle = cmd & 0x10 != 0;
    let last = cmd & 0x01 != 0;
    if cs == 4 {
        return Ok(Sdo { index, sub, abort: u32_at(4), ..Sdo::new(SdoKind::Abort) });
    }
    let segment = |kind| {
        let n = 7 - usize::from((cmd >> 1) & 0x07);
        Sdo { toggle, last, data: d[1..1 + n].to_vec(), ..Sdo::new(kind) }
    };
    let initiate = |kind| {
        let (e, s) = (cmd & 0x02 != 0, cmd & 0x01 != 0);
        if e {
            let n = if s { 4 - usize::from((cmd >> 2) & 0x03) } else { 4 };
            Sdo { index, sub, expedited: true, size_indicated: s, data: d[4..4 + n].to_vec(), ..Sdo::new(kind) }
        } else {
            Sdo { index, sub, size_indicated: s, size: if s { u32_at(4) } else { 0 }, ..Sdo::new(kind) }
        }
    };
    Ok(match (from_server, cs) {
        (false, 2) => Sdo { index, sub, ..Sdo::new(SdoKind::InitiateUploadRequest) },
        (false, 1) => initiate(SdoKind::InitiateDownloadRequest),
        (false, 3) => Sdo { toggle, ..Sdo::new(SdoKind::UploadSegmentRequest) },
        (false, 0) => segment(SdoKind::DownloadSegmentRequest),
        (true, 2) => initiate(SdoKind::InitiateUploadResponse),
        (true, 3) => Sdo { index, sub, ..Sdo::new(SdoKind::InitiateDownloadResponse) },
        (true, 0) => segment(SdoKind::UploadSegmentResponse),
        (true, 1) => Sdo { toggle, ..Sdo::new(SdoKind::DownloadSegmentResponse) },
        _ => return Err(Error::Unsupported(cmd)),
    })
}

/// Encodes an SDO frame into 8 data bytes. Expedited data must be 1–4 bytes, segment data at most 7.
pub fn encode_sdo(f: &Sdo) -> [u8; 8] {
    let mut b = [0u8; 8];
    let mux = |b: &mut [u8; 8]| {
        b[1..3].copy_from_slice(&f.index.to_le_bytes());
        b[3] = f.sub;
    };
    let t = if f.toggle { 0x10 } else { 0 };
    match f.kind {
        SdoKind::InitiateUploadRequest => {
            b[0] = 0x40;
            mux(&mut b);
        }
        SdoKind::InitiateUploadResponse | SdoKind::InitiateDownloadRequest => {
            let ccs: u8 = if f.kind == SdoKind::InitiateUploadResponse { 0x40 } else { 0x20 };
            if f.expedited {
                let n = f.data.len().clamp(1, 4);
                b[0] = ccs | (((4 - n) as u8) << 2) | 0x03;
                b[4..4 + n].copy_from_slice(&f.data[..n]);
            } else {
                b[0] = ccs | u8::from(f.size_indicated);
                b[4..8].copy_from_slice(&f.size.to_le_bytes());
            }
            mux(&mut b);
        }
        SdoKind::InitiateDownloadResponse => {
            b[0] = 0x60;
            mux(&mut b);
        }
        SdoKind::UploadSegmentRequest => b[0] = 0x60 | t,
        SdoKind::DownloadSegmentResponse => b[0] = 0x20 | t,
        SdoKind::UploadSegmentResponse | SdoKind::DownloadSegmentRequest => {
            let n = f.data.len().min(7);
            b[0] = t | (((7 - n) as u8) << 1) | u8::from(f.last);
            b[1..1 + n].copy_from_slice(&f.data[..n]);
        }
        SdoKind::Abort => {
            b[0] = 0x80;
            mux(&mut b);
            b[4..8].copy_from_slice(&f.abort.to_le_bytes());
        }
    }
    b
}

/// An emergency message.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Emergency {
    /// Error code.
    pub code: u16,
    /// Error register (0x1001).
    pub register: u8,
    /// Manufacturer-specific bytes.
    pub manufacturer: [u8; 5],
}

impl Emergency {
    /// Decodes 8 bytes.
    pub fn decode(d: &[u8]) -> Option<Self> {
        if d.len() < 8 {
            return None;
        }
        let mut manufacturer = [0u8; 5];
        manufacturer.copy_from_slice(&d[3..8]);
        Some(Self { code: u16::from_le_bytes([d[0], d[1]]), register: d[2], manufacturer })
    }

    /// Encodes 8 bytes.
    pub fn encode(&self) -> [u8; 8] {
        let mut b = [0u8; 8];
        b[..2].copy_from_slice(&self.code.to_le_bytes());
        b[2] = self.register;
        b[3..].copy_from_slice(&self.manufacturer);
        b
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hex(s: &str) -> Vec<u8> {
        (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).expect("hex")).collect()
    }

    fn up(b: &[u8]) -> String {
        b.iter().map(|x| format!("{x:02X}")).collect()
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

    /// Runs the shared vectors in /conformance/canopen.json.
    #[test]
    fn conformance_vectors() {
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../../conformance/canopen.json");
        let text = std::fs::read_to_string(path).expect("conformance/canopen.json");
        let mut checked = 0;
        for obj in text.split("\n  {").skip(1) {
            let name = field(obj, "name");
            match field(obj, "kind").as_str() {
                "sdo" => {
                    let data = hex(&field(obj, "data"));
                    let f = decode_sdo(&data, obj.contains("\"from_server\": true")).unwrap_or_else(|e| panic!("{name}: {e}"));
                    let rendered = format!(
                        "{}|{:04X}|{:02X}|{}|{}|{}|{}|{}|{}|{:08X}",
                        f.kind, f.index, f.sub, u8::from(f.expedited), u8::from(f.size_indicated), f.size, u8::from(f.toggle), u8::from(f.last), up(&f.data), f.abort
                    );
                    assert_eq!(rendered, field(obj, "fields"), "{name}");
                    assert_eq!(encode_sdo(&f).to_vec(), data, "{name}: re-encode");
                }
                "sdo-invalid" => assert!(decode_sdo(&hex(&field(obj, "data")), obj.contains("\"from_server\": true")).is_err(), "{name}"),
                "cob" => {
                    let (function, node, pdo) = classify(number(obj, "id"));
                    assert_eq!(format!("{function}|{node}|{pdo}"), field(obj, "fields"), "{name}");
                }
                "emcy" => {
                    let data = hex(&field(obj, "data"));
                    let e = Emergency::decode(&data).expect("emcy");
                    assert_eq!(format!("{:04X}|{:02X}|{}", e.code, e.register, up(&e.manufacturer)), field(obj, "fields"), "{name}");
                    assert_eq!(e.encode().to_vec(), data, "{name}");
                }
                other => panic!("{name}: unknown kind {other}"),
            }
            checked += 1;
        }
        assert!(checked >= 71, "checked {checked}");
    }

    #[test]
    fn short_and_block_frames_are_errors() {
        assert_eq!(decode_sdo(&[0x40, 0, 0x10], false), Err(Error::Short(3)));
        assert!(matches!(decode_sdo(&[0xA0, 0, 0x10, 0, 0, 0, 0, 0], false), Err(Error::Unsupported(0xA0))));
    }
}
