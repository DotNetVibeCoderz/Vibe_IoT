//! LoRaWAN 1.0.x PHYPayload codec: structural decoding (MHDR, Join-Request, data frame header, FOpts, FPort,
//! FRMPayload, MIC), data and Join MICs (AES-CMAC), FRMPayload encryption, Join-Accept encryption and session key
//! derivation.
//!
//! The C# runtime (`IoTCom.Net.Protocols.LoRaWan`) is managed; this crate is its twin for embedded or WASM use, kept
//! honest by `/conformance/lorawan.json`, which both test suites execute, and fuzzed with `cargo fuzz run lorawan`.
#![forbid(unsafe_code)]

use aes::cipher::{BlockDecrypt, BlockEncrypt, KeyInit};
use aes::Aes128;
use cmac::{Cmac, Mac};
use core::fmt;

/// A 128-bit key.
pub type Key = [u8; 16];

/// Message type (MHDR bits 7..5).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum MType {
    /// Join-Request.
    JoinRequest = 0,
    /// Join-Accept (encrypted).
    JoinAccept = 1,
    /// Unconfirmed data uplink.
    UnconfirmedDataUp = 2,
    /// Unconfirmed data downlink.
    UnconfirmedDataDown = 3,
    /// Confirmed data uplink.
    ConfirmedDataUp = 4,
    /// Confirmed data downlink.
    ConfirmedDataDown = 5,
    /// Rejoin-Request (1.1, structural only).
    RejoinRequest = 6,
    /// Proprietary.
    Proprietary = 7,
}

impl MType {
    fn from_mhdr(mhdr: u8) -> Self {
        match mhdr >> 5 {
            0 => Self::JoinRequest,
            1 => Self::JoinAccept,
            2 => Self::UnconfirmedDataUp,
            3 => Self::UnconfirmedDataDown,
            4 => Self::ConfirmedDataUp,
            5 => Self::ConfirmedDataDown,
            6 => Self::RejoinRequest,
            _ => Self::Proprietary,
        }
    }

    /// Sent by a device.
    pub fn is_uplink(self) -> bool {
        matches!(self, Self::JoinRequest | Self::UnconfirmedDataUp | Self::ConfirmedDataUp | Self::RejoinRequest)
    }

    /// A data frame (up or down).
    pub fn is_data(self) -> bool {
        matches!(self, Self::UnconfirmedDataUp | Self::UnconfirmedDataDown | Self::ConfirmedDataUp | Self::ConfirmedDataDown)
    }
}

/// A data frame header and payload as transmitted.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DataFrame {
    /// Device address.
    pub dev_addr: u32,
    /// FCtrl byte (its low nibble is the FOpts length).
    pub fctrl: u8,
    /// The 16 transmitted bits of the frame counter.
    pub fcnt: u16,
    /// MAC commands in the header.
    pub fopts: Vec<u8>,
    /// Port, if FRMPayload is present.
    pub fport: Option<u8>,
    /// FRMPayload, still encrypted.
    pub frm_payload: Vec<u8>,
}

/// The body of a PHYPayload.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Body {
    /// Join-Request fields.
    JoinRequest {
        /// JoinEUI (AppEUI).
        join_eui: u64,
        /// DevEUI.
        dev_eui: u64,
        /// DevNonce.
        dev_nonce: u16,
    },
    /// Data frame.
    Data(DataFrame),
    /// Join-Accept, Rejoin-Request or proprietary: opaque bytes between MHDR and MIC.
    Opaque(Vec<u8>),
}

/// A decoded PHYPayload.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Packet {
    /// MHDR byte.
    pub mhdr: u8,
    /// Body.
    pub body: Body,
    /// MIC as transmitted.
    pub mic: [u8; 4],
}

/// Why a PHYPayload was rejected.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Error {
    /// Shorter than MHDR + MIC, or shorter than a data header.
    TooShort,
    /// A Join-Request is 23 bytes, a Join-Accept 17 or 33.
    BadLength,
    /// FOptsLen points past the MIC.
    FOptsOverrun,
    /// MAC commands both in FOpts and on FPort 0.
    FOptsWithPortZero,
    /// Wrong key length or MIC mismatch.
    Mic,
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(match self {
            Self::TooShort => "frame too short",
            Self::BadLength => "wrong length for the message type",
            Self::FOptsOverrun => "FOptsLen runs past the end of the frame",
            Self::FOptsWithPortZero => "MAC commands both in FOpts and on FPort 0",
            Self::Mic => "MIC mismatch",
        })
    }
}

impl std::error::Error for Error {}

impl Packet {
    /// Message type.
    pub fn mtype(&self) -> MType {
        MType::from_mhdr(self.mhdr)
    }

    /// Re-encodes the packet as transmitted (the inverse of [`decode`]).
    pub fn encode(&self) -> Vec<u8> {
        let mut out = vec![self.mhdr];
        match &self.body {
            Body::JoinRequest { join_eui, dev_eui, dev_nonce } => {
                out.extend_from_slice(&join_eui.to_le_bytes());
                out.extend_from_slice(&dev_eui.to_le_bytes());
                out.extend_from_slice(&dev_nonce.to_le_bytes());
            }
            Body::Data(d) => {
                out.extend_from_slice(&d.dev_addr.to_le_bytes());
                out.push(d.fctrl);
                out.extend_from_slice(&d.fcnt.to_le_bytes());
                out.extend_from_slice(&d.fopts);
                if let Some(port) = d.fport {
                    out.push(port);
                    out.extend_from_slice(&d.frm_payload);
                }
            }
            Body::Opaque(bytes) => out.extend_from_slice(bytes),
        }
        out.extend_from_slice(&self.mic);
        out
    }
}

/// Decodes a PHYPayload structurally (no keys needed). Never panics.
pub fn decode(phy: &[u8]) -> Result<Packet, Error> {
    if phy.len() < 5 {
        return Err(Error::TooShort);
    }
    let mhdr = phy[0];
    let mic_at = phy.len() - 4;
    let mut mic = [0u8; 4];
    mic.copy_from_slice(&phy[mic_at..]);
    let mtype = MType::from_mhdr(mhdr);
    let body = match mtype {
        MType::JoinRequest => {
            if phy.len() != 23 {
                return Err(Error::BadLength);
            }
            Body::JoinRequest {
                join_eui: u64::from_le_bytes(phy[1..9].try_into().expect("8 bytes")),
                dev_eui: u64::from_le_bytes(phy[9..17].try_into().expect("8 bytes")),
                dev_nonce: u16::from_le_bytes([phy[17], phy[18]]),
            }
        }
        MType::JoinAccept => {
            if phy.len() != 17 && phy.len() != 33 {
                return Err(Error::BadLength);
            }
            Body::Opaque(phy[1..mic_at].to_vec())
        }
        _ if mtype.is_data() => {
            if phy.len() < 12 {
                return Err(Error::TooShort);
            }
            let fctrl = phy[5];
            let fhdr_end = 8 + usize::from(fctrl & 0x0F);
            if fhdr_end > mic_at {
                return Err(Error::FOptsOverrun);
            }
            let (fport, frm_payload) = if fhdr_end < mic_at {
                let port = phy[fhdr_end];
                if port == 0 && fhdr_end > 8 {
                    return Err(Error::FOptsWithPortZero);
                }
                (Some(port), phy[fhdr_end + 1..mic_at].to_vec())
            } else {
                (None, Vec::new())
            };
            Body::Data(DataFrame {
                dev_addr: u32::from_le_bytes(phy[1..5].try_into().expect("4 bytes")),
                fctrl,
                fcnt: u16::from_le_bytes([phy[6], phy[7]]),
                fopts: phy[8..fhdr_end].to_vec(),
                fport,
                frm_payload,
            })
        }
        _ => Body::Opaque(phy[1..mic_at].to_vec()),
    };
    Ok(Packet { mhdr, body, mic })
}

/// AES-CMAC (RFC 4493).
pub fn aes_cmac(key: &Key, message: &[u8]) -> [u8; 16] {
    let mut mac = <Cmac<Aes128> as Mac>::new_from_slice(key).expect("16-byte key");
    mac.update(message);
    mac.finalize().into_bytes().into()
}

fn aes_encrypt(key: &Key, block: &mut [u8; 16]) {
    Aes128::new(key.into()).encrypt_block(block.into());
}

fn aes_decrypt(key: &Key, block: &mut [u8; 16]) {
    Aes128::new(key.into()).decrypt_block(block.into());
}

fn mic4(tag: [u8; 16]) -> [u8; 4] {
    [tag[0], tag[1], tag[2], tag[3]]
}

/// MIC of a data frame (B0 block, full 32-bit counter) over `message` (MHDR..FRMPayload).
pub fn data_mic(nwk_s_key: &Key, uplink: bool, dev_addr: u32, fcnt: u32, message: &[u8]) -> [u8; 4] {
    let mut buf = Vec::with_capacity(16 + message.len());
    buf.extend_from_slice(&[0x49, 0, 0, 0, 0, u8::from(!uplink)]);
    buf.extend_from_slice(&dev_addr.to_le_bytes());
    buf.extend_from_slice(&fcnt.to_le_bytes());
    // LoRaWAN frames are at most 255 bytes, so the length always fits the B0 length byte.
    buf.extend_from_slice(&[0, (message.len() & 0xFF) as u8]);
    buf.extend_from_slice(message);
    mic4(aes_cmac(nwk_s_key, &buf))
}

/// Encrypts or decrypts FRMPayload (the operation is its own inverse).
pub fn crypt_payload(key: &Key, uplink: bool, dev_addr: u32, fcnt: u32, data: &[u8]) -> Vec<u8> {
    let mut out = data.to_vec();
    for (i, chunk) in out.chunks_mut(16).enumerate() {
        let mut a = [0u8; 16];
        a[0] = 0x01;
        a[5] = u8::from(!uplink);
        a[6..10].copy_from_slice(&dev_addr.to_le_bytes());
        a[10..14].copy_from_slice(&fcnt.to_le_bytes());
        a[15] = ((i + 1) & 0xFF) as u8;
        aes_encrypt(key, &mut a);
        for (b, s) in chunk.iter_mut().zip(a) {
            *b ^= s;
        }
    }
    out
}

/// Session keys (NwkSKey, AppSKey).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SessionKeys {
    /// Network session key.
    pub nwk_s_key: Key,
    /// Application session key.
    pub app_s_key: Key,
}

/// Encodes a data frame: encrypts `payload` on `fport` (NwkSKey on port 0) and appends the MIC.
#[allow(clippy::too_many_arguments)]
pub fn encode_data(mtype: MType, dev_addr: u32, fctrl: u8, fcnt: u32, fopts: &[u8], fport: Option<u8>, payload: &[u8], keys: &SessionKeys) -> Vec<u8> {
    assert!(mtype.is_data(), "not a data message type");
    assert!(fopts.len() <= 15, "FOpts holds at most 15 bytes");
    let uplink = mtype.is_uplink();
    let mut out = vec![(mtype as u8) << 5];
    out.extend_from_slice(&dev_addr.to_le_bytes());
    out.push((fctrl & 0xF0) | (fopts.len() as u8));
    out.extend_from_slice(&((fcnt & 0xFFFF) as u16).to_le_bytes());
    out.extend_from_slice(fopts);
    if let Some(port) = fport {
        out.push(port);
        let key = if port == 0 { &keys.nwk_s_key } else { &keys.app_s_key };
        out.extend_from_slice(&crypt_payload(key, uplink, dev_addr, fcnt, payload));
    }
    let mic = data_mic(&keys.nwk_s_key, uplink, dev_addr, fcnt, &out);
    out.extend_from_slice(&mic);
    out
}

/// Checks the MIC of a decoded data frame with the full counter `fcnt`.
pub fn verify_data(phy: &[u8], nwk_s_key: &Key, fcnt: u32) -> Result<(), Error> {
    let packet = decode(phy)?;
    let Body::Data(d) = &packet.body else { return Err(Error::Mic) };
    let mic = data_mic(nwk_s_key, packet.mtype().is_uplink(), d.dev_addr, fcnt, &phy[..phy.len() - 4]);
    if mic == packet.mic { Ok(()) } else { Err(Error::Mic) }
}

/// Encodes a Join-Request with its MIC.
pub fn encode_join_request(join_eui: u64, dev_eui: u64, dev_nonce: u16, app_key: &Key) -> Vec<u8> {
    let mut out = vec![0x00];
    out.extend_from_slice(&join_eui.to_le_bytes());
    out.extend_from_slice(&dev_eui.to_le_bytes());
    out.extend_from_slice(&dev_nonce.to_le_bytes());
    let mic = mic4(aes_cmac(app_key, &out));
    out.extend_from_slice(&mic);
    out
}

/// Checks the MIC of a Join-Request.
pub fn verify_join_request(phy: &[u8], app_key: &Key) -> Result<(), Error> {
    if !matches!(decode(phy)?.body, Body::JoinRequest { .. }) {
        return Err(Error::BadLength);
    }
    if mic4(aes_cmac(app_key, &phy[..19])) == phy[19..23] { Ok(()) } else { Err(Error::Mic) }
}

/// The clear-text content of a Join-Accept.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct JoinAccept {
    /// JoinNonce (24 bits).
    pub join_nonce: u32,
    /// NetID (24 bits).
    pub net_id: u32,
    /// Assigned device address.
    pub dev_addr: u32,
    /// DLSettings (RX1DROffset, RX2 data rate).
    pub dl_settings: u8,
    /// RX1 delay.
    pub rx_delay: u8,
    /// Optional channel frequency list.
    pub cf_list: Option<[u8; 16]>,
}

fn u24(bytes: &[u8]) -> u32 {
    u32::from(bytes[0]) | (u32::from(bytes[1]) << 8) | (u32::from(bytes[2]) << 16)
}

fn push_u24(out: &mut Vec<u8>, v: u32) {
    out.extend_from_slice(&v.to_le_bytes()[..3]);
}

impl JoinAccept {
    /// Encodes and encrypts the Join-Accept (network side: AES decryption of the body and MIC).
    pub fn encode(&self, app_key: &Key) -> Vec<u8> {
        let mut plain = vec![0x20];
        push_u24(&mut plain, self.join_nonce);
        push_u24(&mut plain, self.net_id);
        plain.extend_from_slice(&self.dev_addr.to_le_bytes());
        plain.push(self.dl_settings);
        plain.push(self.rx_delay);
        if let Some(cf) = self.cf_list {
            plain.extend_from_slice(&cf);
        }
        let mic = mic4(aes_cmac(app_key, &plain));
        plain.extend_from_slice(&mic);
        for block in plain[1..].chunks_mut(16) {
            let block: &mut [u8; 16] = block.try_into().expect("16-byte blocks");
            aes_decrypt(app_key, block);
        }
        plain
    }

    /// Decrypts a Join-Accept and checks its MIC (device side).
    pub fn decrypt(phy: &[u8], app_key: &Key) -> Result<Self, Error> {
        if decode(phy)?.mtype() != MType::JoinAccept {
            return Err(Error::BadLength);
        }
        let mut plain = phy.to_vec();
        for block in plain[1..].chunks_mut(16) {
            let block: &mut [u8; 16] = block.try_into().expect("16-byte blocks");
            aes_encrypt(app_key, block);
        }
        let mic_at = plain.len() - 4;
        if mic4(aes_cmac(app_key, &plain[..mic_at])) != plain[mic_at..] {
            return Err(Error::Mic);
        }
        Ok(Self {
            join_nonce: u24(&plain[1..4]),
            net_id: u24(&plain[4..7]),
            dev_addr: u32::from_le_bytes(plain[7..11].try_into().expect("4 bytes")),
            dl_settings: plain[11],
            rx_delay: plain[12],
            cf_list: (plain.len() == 33).then(|| plain[13..29].try_into().expect("16 bytes")),
        })
    }

    /// Derives the 1.0.x session keys for `dev_nonce`.
    pub fn session_keys(&self, app_key: &Key, dev_nonce: u16) -> SessionKeys {
        let derive = |kind: u8| {
            let mut b = [0u8; 16];
            b[0] = kind;
            b[1..4].copy_from_slice(&self.join_nonce.to_le_bytes()[..3]);
            b[4..7].copy_from_slice(&self.net_id.to_le_bytes()[..3]);
            b[7..9].copy_from_slice(&dev_nonce.to_le_bytes());
            aes_encrypt(app_key, &mut b);
            b
        };
        SessionKeys { nwk_s_key: derive(1), app_s_key: derive(2) }
    }
}

/// Reconstructs a full 32-bit counter from the 16 transmitted bits and the last counter seen.
pub fn reconstruct_fcnt(last: u32, received: u16) -> u32 {
    let candidate = (last & 0xFFFF_0000) | u32::from(received);
    if candidate <= last { candidate.wrapping_add(0x1_0000) } else { candidate }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hex(s: &str) -> Vec<u8> {
        (0..s.len()).step_by(2).map(|i| u8::from_str_radix(&s[i..i + 2], 16).unwrap()).collect()
    }

    fn key(s: &str) -> Key {
        hex(s).try_into().unwrap()
    }

    #[test]
    fn rfc4493_cmac() {
        let k = key("2B7E151628AED2A6ABF7158809CF4F3C");
        assert_eq!(aes_cmac(&k, &[]).to_vec(), hex("BB1D6929E95937287FA37D129B756746"));
        assert_eq!(aes_cmac(&k, &hex("6BC1BEE22E409F96E93D7E117393172A")).to_vec(), hex("070A16B46B4D4144F79BDD9DD04A287C"));
    }

    #[test]
    fn counter_rollover() {
        assert_eq!(reconstruct_fcnt(0xFFFF, 0), 0x1_0000);
        assert_eq!(reconstruct_fcnt(5, 9), 9);
    }

    /// Runs the shared vectors in /conformance/lorawan.json.
    #[test]
    fn conformance_vectors() {
        let path = concat!(env!("CARGO_MANIFEST_DIR"), "/../../../conformance/lorawan.json");
        let text = std::fs::read_to_string(path).expect("conformance/lorawan.json");
        let mut checked = 0;
        for obj in text.split("\n  {").skip(1) {
            let name = field(obj, "name");
            let phy = hex(&field(obj, "phy"));
            if obj.contains("\"valid\": false") {
                assert!(decode(&phy).is_err(), "{name} should be rejected");
                checked += 1;
                continue;
            }
            let packet = decode(&phy).unwrap_or_else(|e| panic!("{name}: {e}"));
            assert_eq!(packet.encode(), phy, "{name}: structural re-encode");
            match field(obj, "kind").as_str() {
                "data" => {
                    let keys = SessionKeys { nwk_s_key: key(&field(obj, "nwkSKey")), app_s_key: key(&field(obj, "appSKey")) };
                    let fcnt = num(obj, "fcnt");
                    let Body::Data(d) = &packet.body else { panic!("{name}: not data") };
                    assert_eq!(d.dev_addr, u32::from_str_radix(&field(obj, "devAddr"), 16).unwrap(), "{name}");
                    assert_eq!(d.fopts, hex(&field(obj, "fopts")), "{name}");
                    verify_data(&phy, &keys.nwk_s_key, fcnt).unwrap_or_else(|e| panic!("{name}: {e}"));
                    let fport = if obj.contains("\"fport\": null") { None } else { Some(num(obj, "fport") as u8) };
                    assert_eq!(d.fport, fport, "{name}");
                    let payload = hex(&field(obj, "payload"));
                    if let Some(port) = fport {
                        let k = if port == 0 { &keys.nwk_s_key } else { &keys.app_s_key };
                        assert_eq!(crypt_payload(k, packet.mtype().is_uplink(), d.dev_addr, fcnt, &d.frm_payload), payload, "{name}");
                    }
                    let fctrl = num(obj, "fctrl") as u8;
                    assert_eq!(encode_data(packet.mtype(), d.dev_addr, fctrl, fcnt, &d.fopts, fport, &payload, &keys), phy, "{name}");
                }
                "join-request" => {
                    let app_key = key(&field(obj, "appKey"));
                    verify_join_request(&phy, &app_key).unwrap();
                    let Body::JoinRequest { join_eui, dev_eui, dev_nonce } = packet.body else { panic!() };
                    assert_eq!(u32::from(dev_nonce), num(obj, "devNonce"));
                    assert_eq!(encode_join_request(join_eui, dev_eui, dev_nonce, &app_key), phy, "{name}");
                }
                "join-accept" => {
                    let app_key = key(&field(obj, "appKey"));
                    let accept = JoinAccept::decrypt(&phy, &app_key).unwrap();
                    assert_eq!(accept.join_nonce, num(obj, "joinNonce"), "{name}");
                    assert_eq!(accept.net_id, num(obj, "netId"), "{name}");
                    assert_eq!(accept.rx_delay as u32, num(obj, "rxDelay"), "{name}");
                    assert_eq!(accept.encode(&app_key), phy, "{name}");
                    let keys = accept.session_keys(&app_key, num(obj, "devNonce") as u16);
                    assert_eq!(keys.nwk_s_key.to_vec(), hex(&field(obj, "nwkSKey")), "{name}");
                    assert_eq!(keys.app_s_key.to_vec(), hex(&field(obj, "appSKey")), "{name}");
                    assert_eq!(JoinAccept::decrypt(&phy, &[0; 16]), Err(Error::Mic));
                }
                other => panic!("unknown kind {other}"),
            }
            checked += 1;
        }
        assert!(checked >= 16, "checked {checked}");
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
}
