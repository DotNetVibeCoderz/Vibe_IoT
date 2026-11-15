#!/usr/bin/env python3
"""Generates the shared conformance vectors used by BOTH the C# and the Rust test suites.

The CRC reference here is an intentionally naive bit-by-bit implementation, independent from the
table-driven engines in C# and Rust, so a bug in either engine cannot hide in the vectors.
Spec examples (Modbus Application Protocol / Modbus over Serial Line, Wikipedia COBS examples) are
hard-coded verbatim.

Run:  python conformance/generate.py
"""
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))


def reflect(v, w):
    r = 0
    for _ in range(w):
        r = (r << 1) | (v & 1)
        v >>= 1
    return r


def crc_bitwise(data, width, poly, init, refin, refout, xorout):
    mask = (1 << width) - 1
    top = 1 << (width - 1)
    crc = init
    for byte in data:
        if refin:
            byte = reflect(byte, 8)
        crc ^= byte << (width - 8)
        for _ in range(8):
            crc = ((crc << 1) ^ poly) if crc & top else (crc << 1)
            crc &= mask
    if refout:
        crc = reflect(crc, width)
    return (crc ^ xorout) & mask


CRCS = [
    ("CRC-8/SMBUS", 8, 0x07, 0x00, False, False, 0x00, 0xF4),
    ("CRC-8/MAXIM-DOW", 8, 0x31, 0x00, True, True, 0x00, 0xA1),
    ("CRC-8/SAE-J1850", 8, 0x1D, 0xFF, False, False, 0xFF, 0x4B),
    ("CRC-16/ARC", 16, 0x8005, 0x0000, True, True, 0x0000, 0xBB3D),
    ("CRC-16/MODBUS", 16, 0x8005, 0xFFFF, True, True, 0x0000, 0x4B37),
    ("CRC-16/IBM-3740", 16, 0x1021, 0xFFFF, False, False, 0x0000, 0x29B1),
    ("CRC-16/XMODEM", 16, 0x1021, 0x0000, False, False, 0x0000, 0x31C3),
    ("CRC-16/KERMIT", 16, 0x1021, 0x0000, True, True, 0x0000, 0x2189),
    ("CRC-16/IBM-SDLC", 16, 0x1021, 0xFFFF, True, True, 0xFFFF, 0x906E),
    ("CRC-16/MCRF4XX", 16, 0x1021, 0xFFFF, True, True, 0x0000, 0x6F91),
    ("CRC-16/DNP", 16, 0x3D65, 0x0000, True, True, 0xFFFF, 0xEA82),
    ("CRC-16/EN-13757", 16, 0x3D65, 0x0000, False, False, 0xFFFF, 0xC2B7),
    ("CRC-16/USB", 16, 0x8005, 0xFFFF, True, True, 0xFFFF, 0xB4C8),
    ("CRC-16/MAXIM-DOW", 16, 0x8005, 0x0000, True, True, 0xFFFF, 0x44C2),
    ("CRC-24/OPENPGP", 24, 0x864CFB, 0xB704CE, False, False, 0x000000, 0x21CF02),
    ("CRC-24/LTE-A", 24, 0x864CFB, 0x000000, False, False, 0x000000, 0xCDE703),
    ("CRC-24/BLE", 24, 0x00065B, 0x555555, True, True, 0x000000, 0xC25A56),
    ("CRC-32/ISO-HDLC", 32, 0x04C11DB7, 0xFFFFFFFF, True, True, 0xFFFFFFFF, 0xCBF43926),
    ("CRC-32/ISCSI", 32, 0x1EDC6F41, 0xFFFFFFFF, True, True, 0xFFFFFFFF, 0xE3069283),
    ("CRC-32/MPEG-2", 32, 0x04C11DB7, 0xFFFFFFFF, False, False, 0x00000000, 0x0376E6E7),
    ("CRC-32/BZIP2", 32, 0x04C11DB7, 0xFFFFFFFF, False, False, 0xFFFFFFFF, 0xFC891918),
    ("CRC-64/XZ", 64, 0x42F0E1EBA9EA3693, 0xFFFFFFFFFFFFFFFF, True, True, 0xFFFFFFFFFFFFFFFF, 0x995DC9BBDF1939FA),
    ("CRC-64/ECMA-182", 64, 0x42F0E1EBA9EA3693, 0, False, False, 0, 0x6C40DF5F0B497347),
]

INPUTS = [
    b"123456789",
    b"",
    bytes([0x01, 0x03, 0x00, 0x00, 0x00, 0x0A]),
    bytes(range(256)),
    b"IoTCom.Net by Gravicode Studios",
]


def crc_vectors():
    out = []
    for name, w, poly, init, ri, ro, xo, check in CRCS:
        got = crc_bitwise(b"123456789", w, poly, init, ri, ro, xo)
        assert got == check, f"{name}: reference {got:#x} != catalogue {check:#x}"
        for data in INPUTS:
            out.append({
                "algorithm": name,
                "input": data.hex().upper(),
                "expected": f"{crc_bitwise(data, w, poly, init, ri, ro, xo):0{w // 4}X}",
            })
    return out


def cobs_vectors():
    r = lambda a, b: bytes(range(a, b + 1))
    cases = [
        ("empty", b"", b"\x01"),
        ("single zero", b"\x00", b"\x01\x01"),
        ("two zeros", b"\x00\x00", b"\x01\x01\x01"),
        ("zero data zero", b"\x00\x11\x00", b"\x01\x02\x11\x01"),
        ("mixed", b"\x11\x22\x00\x33", b"\x03\x11\x22\x02\x33"),
        ("no zeros", b"\x11\x22\x33\x44", b"\x05\x11\x22\x33\x44"),
        ("trailing zeros", b"\x11\x00\x00\x00", b"\x02\x11\x01\x01\x01"),
        ("254 non-zero", r(1, 254), b"\xff" + r(1, 254)),
        ("zero then 254", b"\x00" + r(1, 254), b"\x01\xff" + r(1, 254)),
        ("255 non-zero", r(1, 255), b"\xff" + r(1, 254) + b"\x02\xff"),
        ("2..255 then zero", r(2, 255) + b"\x00", b"\xff" + r(2, 255) + b"\x01\x01"),
        ("3..255 zero 1", r(3, 255) + b"\x00\x01", b"\xfe" + r(3, 255) + b"\x02\x01"),
    ]
    return [{"name": n, "decoded": d.hex().upper(), "encoded": e.hex().upper()} for n, d, e in cases]


def slip_vectors():
    cases = [
        ("plain", b"\x01\x02\x03", b"\xc0\x01\x02\x03\xc0"),
        ("end and esc", b"\xc0\xdb\x01", b"\xc0\xdb\xdc\xdb\xdd\x01\xc0"),
        ("empty", b"", b"\xc0\xc0"),
    ]
    return [{"name": n, "decoded": d.hex().upper(), "encoded": e.hex().upper()} for n, d, e in cases]


def modbus_crc(data):
    c = crc_bitwise(data, 16, 0x8005, 0xFFFF, True, True, 0)
    return bytes([c & 0xFF, c >> 8])


def lrc(data):
    return (-sum(data)) & 0xFF


def modbus_vectors():
    out = []

    def add(name, mode, direction, tid, unit, pdu, adu):
        out.append({"name": name, "mode": mode, "direction": direction, "transactionId": tid, "unitId": unit,
                    "pdu": pdu.hex().upper(), "adu": adu.hex().upper()})

    # Spec examples (Modbus over Serial Line V1.02 / Application Protocol V1.1b3)
    pdu = bytes.fromhex("030000000A")
    rtu = bytes([1]) + pdu + modbus_crc(bytes([1]) + pdu)
    assert rtu.hex().upper() == "01030000000AC5CD", rtu.hex()
    add("read holding 0..10 rtu", "rtu", "request", 0, 1, pdu, rtu)
    pdu = bytes.fromhex("03006B0003")
    rtu = bytes([0x11]) + pdu + modbus_crc(bytes([0x11]) + pdu)
    assert rtu.hex().upper() == "1103006B00037687", rtu.hex()
    add("spec read holding rtu", "rtu", "request", 0, 0x11, pdu, rtu)
    asc = b":" + (bytes([0x11]) + pdu + bytes([lrc(bytes([0x11]) + pdu)])).hex().upper().encode() + b"\r\n"
    assert asc == b":1103006B00037E\r\n", asc
    add("spec read holding ascii", "ascii", "request", 0, 0x11, pdu, asc)
    resp = bytes.fromhex("0306022B00000064")
    add("spec read holding response rtu", "rtu", "response", 0, 0x11, resp, bytes([0x11]) + resp + modbus_crc(bytes([0x11]) + resp))
    add("read holding tcp", "tcp", "request", 1, 1, bytes.fromhex("030000000A"), bytes.fromhex("000100000006" + "01030000000A"))
    add("write single coil tcp", "tcp", "request", 0x1234, 0xFF, bytes.fromhex("0500ACFF00"), bytes.fromhex("123400000006" + "FF0500ACFF00"))
    for name, unit, p in [
        ("write single register rtu", 1, "0600010003"),
        ("write multiple registers rtu", 1, "100001000204000A0102"),
        ("write multiple coils rtu", 1, "0F0013000A02CD01"),
        ("exception response rtu", 1, "8302"),
        ("read coils response rtu", 1, "0103CD6B05"),
    ]:
        pb = bytes.fromhex(p)
        direction = "response" if p.startswith("83") or p.startswith("0103") else "request"
        add(name, "rtu", direction, 0, unit, pb, bytes([unit]) + pb + modbus_crc(bytes([unit]) + pb))
    return out


def coap_encode(mtype, code, mid, token, options, payload):
    """Reference CoAP encoder (RFC 7252 §3): independent from the C# and Rust codecs."""
    out = bytearray([0x40 | (mtype << 4) | len(token), code, mid >> 8, mid & 0xFF])
    out += token
    last = 0

    def ext(v):
        if v < 13:
            return v, b""
        if v < 269:
            return 13, bytes([v - 13])
        return 14, (v - 269).to_bytes(2, "big")

    for number, value in sorted(options, key=lambda o: o[0]):
        dn, dx = ext(number - last)
        ln, lx = ext(len(value))
        out.append((dn << 4) | ln)
        out += dx + lx + value
        last = number
    if payload:
        out.append(0xFF)
        out += payload
    return bytes(out)


def uint(v):
    return v.to_bytes((v.bit_length() + 7) // 8, "big") if v else b""


def coap_vectors():
    out = []

    def add(name, mtype, code, mid, token, options, payload, expected=None):
        wire = coap_encode(mtype, code, mid, token, options, payload)
        if expected is not None:
            assert wire.hex().upper() == expected, (name, wire.hex())
        out.append({
            "name": name, "valid": True, "type": mtype, "code": code, "messageId": mid,
            "token": token.hex().upper(),
            "options": ";".join(f"{n}:{v.hex().upper()}" for n, v in sorted(options, key=lambda o: o[0])),
            "payload": payload.hex().upper(), "wire": wire.hex().upper(),
        })

    # RFC 7252 Appendix A, figure 16: CON GET /temperature, MID 0x7d34, no token.
    add("rfc7252 get temperature", 0, 0x01, 0x7D34, b"", [(11, b"temperature")], b"", "40017D34BB74656D7065726174757265")
    # Its piggybacked response: ACK 2.05 "22.3 C".
    add("rfc7252 ack content", 2, 0x45, 0x7D34, b"", [], b"22.3 C", "60457D34FF32322E332043")
    add("empty ack", 2, 0, 0x1234, b"", [], b"", "60001234")
    add("reset", 3, 0, 0x0001, b"", [], b"", "70000001")
    add("token and query", 0, 0x01, 0x0101, bytes.fromhex("CAFEBABE"),
        [(11, b"sensors"), (11, b"temp"), (15, b"unit=c"), (17, uint(50))], b"")
    add("observe register", 0, 0x01, 0xBEEF, bytes.fromhex("A1"), [(6, b""), (11, b"temp")], b"")
    add("notification", 1, 0x45, 0x0002, bytes.fromhex("A1"), [(6, uint(12)), (12, uint(0)), (14, uint(60))], b"21.5")
    add("block2 response", 2, 0x45, 0x0003, bytes.fromhex("01"), [(12, uint(42)), (23, uint((3 << 4) | 0x08 | 6))], bytes(range(64)))
    add("delta 13 extended", 0, 0x02, 0x0004, bytes([7]), [(1, bytes([1])), (35, b"coap://proxy/x")], b"p")
    add("delta 269 extended", 1, 0x03, 0x0005, b"", [(300, bytes([0xAB])), (2048, b"")], b"")
    add("length 13 and 269", 0, 0x03, 0x0006, b"", [(11, b"a" * 20), (15, b"q" * 300)], b"x" * 3)
    add("8-byte token", 0, 0x04, 0xFFFF, bytes(range(1, 9)), [(60, uint(70000))], b"")
    add("error response", 2, 0x84, 0x0007, bytes([0x10]), [(12, uint(0))], b"not found")

    def bad(name, wire):
        out.append({"name": name, "valid": False, "wire": wire.hex().upper()})

    bad("too short", bytes.fromhex("400100"))
    bad("version 2", bytes.fromhex("80017D34"))
    bad("token length 9", bytes.fromhex("49017D34") + bytes(9))
    bad("token truncated", bytes.fromhex("44017D34AABB"))
    bad("reserved delta 15", bytes.fromhex("40017D34F1"))
    bad("reserved length 15", bytes.fromhex("40017D341F"))
    bad("option truncated", bytes.fromhex("40017D34B5616263"))
    bad("payload marker without payload", bytes.fromhex("40017D34FF"))
    bad("empty message with token", bytes.fromhex("6100123401"))
    bad("empty message with payload", bytes.fromhex("60001234FF01"))
    return out


# ---------------------------------------------------------------------------------------------------------------
# MAVLink: an independent reference (CRC_EXTRA from the XML, v1/v2 framing, truncation, signing).
import hashlib
import struct
import xml.etree.ElementTree as ET

MAV_DIR = os.path.join(HERE, "..", "src", "IoTCom.Net.Protocols.Mavlink", "Dialects")
MAV_SIZES = {"char": 1, "uint8_t": 1, "int8_t": 1, "uint16_t": 2, "int16_t": 2, "uint32_t": 4, "int32_t": 4,
             "float": 4, "uint64_t": 8, "int64_t": 8, "double": 8, "uint8_t_mavlink_version": 1}
MAV_STRUCT = {"char": "s", "uint8_t": "B", "int8_t": "b", "uint16_t": "H", "int16_t": "h", "uint32_t": "I",
              "int32_t": "i", "float": "f", "uint64_t": "Q", "int64_t": "q", "double": "d", "uint8_t_mavlink_version": "B"}


def x25(data, crc=0xFFFF):
    for b in data:
        t = b ^ (crc & 0xFF)
        t = (t ^ (t << 4)) & 0xFF
        crc = ((crc >> 8) ^ (t << 8) ^ (t << 3) ^ (t >> 4)) & 0xFFFF
    return crc


def mav_messages():
    """All messages of common.xml and its includes: name -> (id, base fields in wire order, extension fields)."""
    out = {}

    def load(name):
        root = ET.parse(os.path.join(MAV_DIR, name)).getroot()
        for inc in root.findall("include"):
            load(inc.text.strip())
        for m in root.iter("message"):
            fields, ext, in_ext = [], [], False
            for c in m:
                if c.tag == "extensions":
                    in_ext = True
                elif c.tag == "field":
                    t = c.get("type")
                    base, n = (t.split("[")[0], int(t.split("[")[1][:-1])) if "[" in t else (t, 0)
                    (ext if in_ext else fields).append((c.get("name"), base, n))
            fields.sort(key=lambda f: -MAV_SIZES[f[1]])  # stable: equal sizes keep XML order
            out[m.get("name")] = (int(m.get("id")), fields, ext)

    load("common.xml")
    return out


def crc_extra(name, fields):
    crc = x25((name + " ").encode())
    for fname, base, n in fields:
        crc = x25(((base if base != "uint8_t_mavlink_version" else "uint8_t") + " ").encode(), crc)
        crc = x25((fname + " ").encode(), crc)
        if n:
            crc = x25(bytes([n]), crc)
    return (crc & 0xFF) ^ (crc >> 8)


def mav_length(fields):
    return sum(MAV_SIZES[b] * (n or 1) for _, b, n in fields)


def mavlink_message_table():
    msgs = mav_messages()
    known = {"HEARTBEAT": 50, "SYS_STATUS": 124, "SYSTEM_TIME": 137, "PARAM_REQUEST_READ": 214, "PARAM_REQUEST_LIST": 159,
             "PARAM_VALUE": 220, "PARAM_SET": 168, "GPS_RAW_INT": 24, "ATTITUDE": 39, "GLOBAL_POSITION_INT": 104,
             "VFR_HUD": 20, "COMMAND_LONG": 152, "COMMAND_ACK": 143, "STATUSTEXT": 83, "BATTERY_STATUS": 154}
    table = []
    for name, (mid, fields, ext) in sorted(msgs.items(), key=lambda kv: kv[1][0]):
        ce = crc_extra(name, fields)
        if name in known:
            assert ce == known[name], f"{name}: CRC_EXTRA {ce} != published {known[name]}"
        table.append({"name": name, "id": mid, "crcExtra": ce, "minLength": mav_length(fields), "maxLength": mav_length(fields + ext)})
    assert len(table) > 200
    return table


def mav_payload(name, values):
    mid, fields, ext = mav_messages()[name]
    buf = b""
    for fname, base, n in fields + ext:
        v = values.get(fname, 0 if base != "char" else b"")
        if base == "char":
            buf += struct.pack(f"<{n or 1}s", v.encode() if isinstance(v, str) else v)
        elif n:
            buf += struct.pack(f"<{n}{MAV_STRUCT[base]}", *(list(v) + [0] * (n - len(v))))
        else:
            buf += struct.pack("<" + MAV_STRUCT[base], v)
    return mid, buf


def mav_frame(version, seq, sys_id, comp, name, values, signing=None):
    mid, payload = mav_payload(name, values)
    _, fields, _ = mav_messages()[name]
    extra = crc_extra(name, fields)
    if version == 1:
        header = bytes([0xFE, len(payload), seq, sys_id, comp, mid])
        crc = x25(bytes([extra]), x25(header[1:] + payload))
        return header + payload + struct.pack("<H", crc)
    trimmed = payload.rstrip(b"\x00") or payload[:1]  # MAVLink 2 truncates trailing zeros, keeping at least one byte
    incompat = 0x01 if signing else 0
    header = bytes([0xFD, len(trimmed), incompat, 0, seq, sys_id, comp]) + mid.to_bytes(3, "little")
    crc = x25(bytes([extra]), x25(header[1:] + trimmed))
    frame = header + trimmed + struct.pack("<H", crc)
    if signing:
        key, link_id, timestamp = signing
        sig_input = key + frame + bytes([link_id]) + timestamp.to_bytes(6, "little")
        frame += bytes([link_id]) + timestamp.to_bytes(6, "little") + hashlib.sha256(sig_input).digest()[:6]
    return frame


def mavlink_frames():
    out = []

    def add(name, version, seq, sys_id, comp, message, values, signing=None):
        mid, payload = mav_payload(message, values)
        frame = mav_frame(version, seq, sys_id, comp, message, values, signing)
        out.append({"name": name, "version": version, "sequence": seq, "systemId": sys_id, "componentId": comp,
                    "messageId": mid, "message": message, "payload": payload.hex().upper(), "frame": frame.hex().upper(),
                    "signed": bool(signing)})

    hb = {"type": 2, "autopilot": 3, "base_mode": 0x51, "custom_mode": 0, "system_status": 4, "mavlink_version": 3}
    add("heartbeat v1", 1, 0, 1, 1, "HEARTBEAT", hb)
    add("heartbeat v2", 2, 7, 1, 1, "HEARTBEAT", hb)
    add("attitude v2", 2, 8, 1, 1, "ATTITUDE", {"time_boot_ms": 123456, "roll": 0.1, "pitch": -0.05, "yaw": 1.5,
                                                "rollspeed": 0.0, "pitchspeed": 0.0, "yawspeed": 0.25})
    add("global position v2", 2, 9, 1, 1, "GLOBAL_POSITION_INT", {"time_boot_ms": 5000, "lat": -69147000, "lon": 1076098000,
                                                                  "alt": 712000, "relative_alt": 15000, "vx": 120, "vy": -40, "vz": 0, "hdg": 9000})
    add("command long arm v2", 2, 0, 255, 190, "COMMAND_LONG", {"target_system": 1, "target_component": 1, "command": 400,
                                                                "confirmation": 0, "param1": 1.0})
    add("command long arm v1", 1, 1, 255, 190, "COMMAND_LONG", {"target_system": 1, "target_component": 1, "command": 400, "param1": 1.0})
    add("statustext with extensions v2", 2, 3, 1, 1, "STATUSTEXT", {"severity": 6, "text": "Armed", "id": 0, "chunk_seq": 0})
    add("param value v2", 2, 4, 1, 1, "PARAM_VALUE", {"param_id": "WPNAV_SPEED", "param_value": 500.0, "param_type": 9,
                                                       "param_count": 12, "param_index": 3})
    add("all-zero payload keeps one byte", 2, 5, 1, 1, "COMMAND_ACK", {"command": 0, "result": 0})
    add("signed heartbeat", 2, 9, 1, 1, "HEARTBEAT", hb, (bytes(range(32)), 1, 0x0123456789))
    return out


# ---------------------------------------------------------------------------------------------------------------------
# LoRaWAN 1.0.x. A deliberately plain AES-128 (FIPS-197) and AES-CMAC (RFC 4493), self-checked against the published
# test vectors below, so the vectors do not depend on any of the engines under test.

def _xtime(a):
    return ((a << 1) ^ 0x1B) & 0xFF if a & 0x80 else a << 1


def _gmul(a, b):
    r = 0
    while b:
        if b & 1:
            r ^= a
        a = _xtime(a)
        b >>= 1
    return r


def _sbox():
    box = [0] * 256
    for x in range(256):
        inv = 0 if x == 0 else next(y for y in range(1, 256) if _gmul(x, y) == 1)
        s = inv
        for i in range(1, 5):
            s ^= ((inv << i) | (inv >> (8 - i))) & 0xFF
        box[x] = s ^ 0x63
    return box


SBOX = _sbox()
INV_SBOX = [SBOX.index(i) for i in range(256)]


def _expand(key):
    w = [list(key[i:i + 4]) for i in range(0, 16, 4)]
    rcon = 1
    for i in range(4, 44):
        t = list(w[i - 1])
        if i % 4 == 0:
            t = [SBOX[b] for b in t[1:] + t[:1]]
            t[0] ^= rcon
            rcon = _xtime(rcon)
        w.append([a ^ b for a, b in zip(w[i - 4], t)])
    return [sum(w[r * 4:r * 4 + 4], []) for r in range(11)]


def _mix(s, m):
    out = []
    for j in range(0, 16, 4):
        c = s[j:j + 4]
        for row in range(4):
            out.append(_gmul(c[0], m[row][0]) ^ _gmul(c[1], m[row][1]) ^ _gmul(c[2], m[row][2]) ^ _gmul(c[3], m[row][3]))
    return out


MIX = [[2, 3, 1, 1], [1, 2, 3, 1], [1, 1, 2, 3], [3, 1, 1, 2]]
INV_MIX = [[14, 11, 13, 9], [9, 14, 11, 13], [13, 9, 14, 11], [11, 13, 9, 14]]


def aes_encrypt(key, block):
    rk = _expand(key)
    s = [b ^ k for b, k in zip(block, rk[0])]
    for r in range(1, 11):
        s = [SBOX[b] for b in s]
        s = [s[(i + 4 * (i % 4)) % 16] for i in range(16)]  # ShiftRows (column-major state)
        if r != 10:
            s = _mix(s, MIX)
        s = [b ^ k for b, k in zip(s, rk[r])]
    return bytes(s)


def aes_decrypt(key, block):
    rk = _expand(key)
    s = [b ^ k for b, k in zip(block, rk[10])]
    for r in range(9, -1, -1):
        s = [s[(i - 4 * (i % 4)) % 16] for i in range(16)]  # InvShiftRows
        s = [INV_SBOX[b] for b in s]
        s = [b ^ k for b, k in zip(s, rk[r])]
        if r != 0:
            s = _mix(s, INV_MIX)
    return bytes(s)


def _dbl(b):
    v = int.from_bytes(b, "big") << 1
    if b[0] & 0x80:
        v ^= 0x87
    return (v & ((1 << 128) - 1)).to_bytes(16, "big")


def aes_cmac(key, msg):
    k1 = _dbl(aes_encrypt(key, bytes(16)))
    k2 = _dbl(k1)
    n = max(1, (len(msg) + 15) // 16)
    last = msg[(n - 1) * 16:]
    if len(msg) and len(msg) % 16 == 0:
        last = bytes(a ^ b for a, b in zip(last, k1))
    else:
        last = bytes(a ^ b for a, b in zip(last + b"\x80" + bytes(15 - len(last)), k2))
    x = bytes(16)
    for i in range(n - 1):
        x = aes_encrypt(key, bytes(a ^ b for a, b in zip(x, msg[i * 16:i * 16 + 16])))
    return aes_encrypt(key, bytes(a ^ b for a, b in zip(x, last)))


def _aes_self_check():
    k = bytes(range(16))
    assert aes_encrypt(k, bytes.fromhex("00112233445566778899aabbccddeeff")).hex() == "69c4e0d86a7b0430d8cdb78070b4c55a"
    assert aes_decrypt(k, bytes.fromhex("69c4e0d86a7b0430d8cdb78070b4c55a")).hex() == "00112233445566778899aabbccddeeff"
    rk = bytes.fromhex("2b7e151628aed2a6abf7158809cf4f3c")
    m = bytes.fromhex("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51"
                      "30c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710")
    for length, tag in ((0, "bb1d6929e95937287fa37d129b756746"), (16, "070a16b46b4d4144f79bdd9dd04a287c"),
                        (40, "dfa66747de9ae63030ca32611497c827"), (64, "51f0bebf7e3b9d92fc49741779363cfe")):
        assert aes_cmac(rk, m[:length]).hex() == tag, length


def lw_b0(uplink, dev_addr, fcnt, length):
    return (bytes([0x49, 0, 0, 0, 0, 0 if uplink else 1]) + dev_addr.to_bytes(4, "little")
            + fcnt.to_bytes(4, "little") + bytes([0, length]))


def lw_crypt(key, uplink, dev_addr, fcnt, data):
    out = bytearray()
    for i in range(0, len(data), 16):
        a = (bytes([1, 0, 0, 0, 0, 0 if uplink else 1]) + dev_addr.to_bytes(4, "little")
             + fcnt.to_bytes(4, "little") + bytes([0, i // 16 + 1]))
        s = aes_encrypt(key, a)
        out += bytes(x ^ y for x, y in zip(data[i:i + 16], s))
    return bytes(out)


def lw_data(mtype, dev_addr, fctrl, fcnt, fopts, fport, payload, nwk, app):
    uplink = mtype in (2, 4)
    fctrl = (fctrl & 0xF0) | len(fopts)
    msg = bytes([mtype << 5]) + dev_addr.to_bytes(4, "little") + bytes([fctrl]) + (fcnt & 0xFFFF).to_bytes(2, "little") + fopts
    if fport is not None:
        msg += bytes([fport]) + lw_crypt(nwk if fport == 0 else app, uplink, dev_addr, fcnt, payload)
    return msg + aes_cmac(nwk, lw_b0(uplink, dev_addr, fcnt, len(msg)) + msg)[:4]


def lorawan_vectors():
    _aes_self_check()
    out = []
    nwk = bytes.fromhex("44024241ED4CE9A68C6A8BC055233FD3")
    app = bytes.fromhex("EC925802AE430CA77FD3DD73CB2CC588")

    def data(name, mtype, dev_addr, fctrl, fcnt, fopts, fport, payload, wire=None):
        phy = lw_data(mtype, dev_addr, fctrl, fcnt, fopts, fport, payload, nwk, app)
        if wire is not None:
            assert phy.hex().upper() == wire, (name, phy.hex())
        out.append({"name": name, "kind": "data", "valid": True, "phy": phy.hex().upper(), "mtype": mtype,
                    "devAddr": f"{dev_addr:08X}", "fctrl": (fctrl & 0xF0) | len(fopts), "fcnt": fcnt,
                    "fopts": fopts.hex().upper(), "fport": fport, "payload": payload.hex().upper(),
                    "mic": phy[-4:].hex().upper(), "nwkSKey": nwk.hex().upper(), "appSKey": app.hex().upper()})

    # The example from the lora-packet README ("test" on FPort 1, FCnt 2) pins the reference to a published frame.
    data("lora-packet readme uplink", 2, 0x49BE7DF1, 0x00, 2, b"", 1, b"test", wire="40F17DBE4900020001954378762B11FF0D")
    data("confirmed up with FOpts", 4, 0x26011BDA, 0x80, 17, bytes([0x02, 0x06, 0xE6, 0x0A]), 1, bytes(range(20)))
    data("downlink ack only", 3, 0x26011BDA, 0x20, 5, b"", None, b"")
    data("downlink mac on port 0", 3, 0x26011BDA, 0x00, 6, b"", 0, bytes([0x02, 0x0C, 0x02, 0x06]))
    data("confirmed down 40 bytes", 5, 0x26011BDA, 0x10, 7, b"", 10, bytes(range(100, 140)))
    data("fcnt above 16 bits", 2, 0x00ABCDEF, 0x00, 0x00012345, b"", 2, bytes.fromhex("00002EE000"))
    data("empty payload with port", 2, 0x01020304, 0x00, 1, b"", 1, b"")

    app_key = bytes.fromhex("2B7E151628AED2A6ABF7158809CF4F3C")
    for name, join_eui, dev_eui, nonce in (("join request", 0x70B3D57ED0000000, 0x0004A30B001C0530, 1),
                                           ("join request nonce 0x1234", 0, 0x70B3D57ED0000101, 0x1234)):
        body = bytes([0x00]) + join_eui.to_bytes(8, "little") + dev_eui.to_bytes(8, "little") + nonce.to_bytes(2, "little")
        phy = body + aes_cmac(app_key, body)[:4]
        out.append({"name": name, "kind": "join-request", "valid": True, "phy": phy.hex().upper(),
                    "appKey": app_key.hex().upper(), "joinEui": f"{join_eui:016X}", "devEui": f"{dev_eui:016X}",
                    "devNonce": nonce, "mic": phy[-4:].hex().upper()})

    cflist = bytes.fromhex("184F84E85684B85E8488668400000000")
    for name, join_nonce, net_id, dev_addr, dl, rx_delay, cf, dev_nonce in (
            ("join accept", 0x00A1B2, 0x000013, 0x26011BDA, 0x02, 1, b"", 1),
            ("join accept with cflist", 0x123456, 0x00000B, 0x16AB0001, 0x13, 5, cflist, 0x1234)):
        plain = (bytes([0x20]) + join_nonce.to_bytes(3, "little") + net_id.to_bytes(3, "little")
                 + dev_addr.to_bytes(4, "little") + bytes([dl, rx_delay]) + cf)
        plain += aes_cmac(app_key, plain)[:4]
        phy = bytes([0x20]) + b"".join(aes_decrypt(app_key, plain[1 + i:17 + i]) for i in range(0, len(plain) - 1, 16))

        def key(kind):
            return aes_encrypt(app_key, bytes([kind]) + join_nonce.to_bytes(3, "little") + net_id.to_bytes(3, "little")
                               + dev_nonce.to_bytes(2, "little") + bytes(7))
        out.append({"name": name, "kind": "join-accept", "valid": True, "phy": phy.hex().upper(),
                    "appKey": app_key.hex().upper(), "joinNonce": join_nonce, "netId": net_id, "devAddr": f"{dev_addr:08X}",
                    "dlSettings": dl, "rxDelay": rx_delay, "cfList": cf.hex().upper(), "devNonce": dev_nonce,
                    "nwkSKey": key(1).hex().upper(), "appSKey": key(2).hex().upper()})

    for name, wire in (("too short", "40010203"), ("join request wrong length", "00" + "11" * 20),
                       ("join accept wrong length", "20" + "00" * 20), ("fopts past end", "400403020105000001020304"),
                       ("fport 0 with fopts", "4004030201010000020001020304")):
        out.append({"name": name, "kind": "invalid", "valid": False, "phy": wire.upper()})
    return out

# ---------------------------------------------------------------------------------------------------------------------
# DLMS/COSEM: HDLC frames (IEC 62056-46) and A-XDR data. Data vectors carry a canonical text form that both suites
# render identically: null, bool:true, i8:-5 … u64:…, enum:3, bcd:9, f32:<bits>, f64:<bits>, octets:HEX, vis:HEX,
# utf8:HEX, bits:N:HEX, dt:HEX, date:HEX, time:HEX, array[…], struct[…].

def hdlc_fcs(data):
    return crc_bitwise(data, 16, 0x1021, 0xFFFF, True, True, 0xFFFF)


def hdlc_addr(upper, lower=None, size=0):
    if size == 0:
        size = (1 if upper < 0x80 else 4) if lower is None else (2 if upper < 0x80 and lower < 0x80 else 4)
    if size == 1:
        return bytes([(upper << 1) | 1])
    if size == 2:
        return bytes([upper << 1, (lower << 1) | 1])
    lower = lower or 0
    return bytes([(upper >> 7) << 1, (upper & 0x7F) << 1, (lower >> 7) << 1, ((lower & 0x7F) << 1) | 1])


def hdlc_frame(dest, src, control, info=b"", segmented=False):
    head = dest + src + bytes([control])
    length = 2 + len(head) + (2 + len(info) if info else 0) + 2
    body = bytes([0xA0 | (0x08 if segmented else 0) | (length >> 8), length & 0xFF]) + head
    if info:
        hcs = hdlc_fcs(body)
        body += bytes([hcs & 0xFF, hcs >> 8]) + info
    fcs = hdlc_fcs(body)
    return b"\x7e" + body + bytes([fcs & 0xFF, fcs >> 8]) + b"\x7e"


def axdr_len(n):
    if n < 0x80:
        return bytes([n])
    if n <= 0xFF:
        return bytes([0x81, n])
    return bytes([0x82, n >> 8, n & 0xFF])


def axdr(v):
    """v = (kind, value) → (bytes, canonical)."""
    kind, val = v
    ints = {"i8": (15, 1, True), "i16": (16, 2, True), "i32": (5, 4, True), "i64": (20, 8, True),
            "u8": (17, 1, False), "u16": (18, 2, False), "u32": (6, 4, False), "u64": (21, 8, False),
            "enum": (22, 1, False), "bcd": (13, 1, False)}
    if kind == "null":
        return b"\x00", "null"
    if kind == "bool":
        return bytes([3, 0xFF if val else 0]), "bool:" + ("true" if val else "false")
    if kind in ints:
        tag, size, signed = ints[kind]
        return bytes([tag]) + val.to_bytes(size, "big", signed=signed), f"{kind}:{val}"
    if kind in ("f32", "f64"):
        import struct
        raw = struct.pack(">f" if kind == "f32" else ">d", val)
        return bytes([23 if kind == "f32" else 24]) + raw, f"{kind}:{raw.hex().upper()}"
    if kind in ("octets", "vis", "utf8"):
        tag = {"octets": 9, "vis": 10, "utf8": 12}[kind]
        return bytes([tag]) + axdr_len(len(val)) + val, f"{kind}:{val.hex().upper()}"
    if kind == "bits":
        nbits, raw = val
        return bytes([4]) + axdr_len(nbits) + raw, f"bits:{nbits}:{raw.hex().upper()}"
    if kind in ("dt", "date", "time"):
        tag = {"dt": 25, "date": 26, "time": 27}[kind]
        return bytes([tag]) + val, f"{kind}:{val.hex().upper()}"
    if kind in ("array", "struct"):
        parts = [axdr(x) for x in val]
        return (bytes([1 if kind == "array" else 2]) + axdr_len(len(parts)) + b"".join(p[0] for p in parts),
                f"{kind}[" + ",".join(p[1] for p in parts) + "]")
    raise ValueError(kind)


def dlms_vectors():
    out = []

    def frame(name, raw, control, dest, src, info, segmented, published=None):
        if published is not None:
            assert raw.hex().upper() == published, (name, raw.hex())
        out.append({"name": name, "kind": "hdlc", "valid": True, "wire": raw.hex().upper(), "control": control,
                    "dest": dest, "src": src, "info": info.hex().upper(), "segmented": segmented})

    client = hdlc_addr(16)
    server = hdlc_addr(1, 17)
    frame("snrm (classic)", hdlc_frame(hdlc_addr(1), client, 0x93), 0x93, "1", "16", b"", False, "7EA0070321930F017E")
    params = bytes.fromhex("818014050200800602008007040000000108040000000 1".replace(" ", ""))
    frame("ua with parameters", hdlc_frame(client, server, 0x73, params), 0x73, "16", "1/17", params, False)
    get = bytes.fromhex("E6E600C001C100030100010800FF0200")
    frame("i-frame get", hdlc_frame(server, client, 0x10, get), 0x10, "1/17", "16", get, False)
    seg = bytes(range(60))
    frame("segmented i-frame", hdlc_frame(client, server, 0x32, seg, True), 0x32, "16", "1/17", seg, True)
    frame("rr", hdlc_frame(server, client, 0x51), 0x51, "1/17", "16", b"", False)
    big = hdlc_addr(1, 0x3FFF, 4)
    frame("four-byte server address", hdlc_frame(big, client, 0x53), 0x53, "1/16383", "16", b"", False)

    good = hdlc_frame(server, client, 0x10, get)
    bad_fcs = bytearray(good)
    bad_fcs[-3] ^= 1
    bad_hcs = bytearray(good)
    bad_hcs[10] ^= 1
    for name, wire in (("bad fcs", bytes(bad_fcs)), ("bad hcs", bytes(bad_hcs)), ("not type 3", b"\x7e\x80\x05\x03\x21\x93\x7e"),
                       ("length past the closing flag", b"\x7e\xa0\x20\x03\x21\x93\x0f\x01\x7e")):
        out.append({"name": name, "kind": "hdlc", "valid": False, "wire": wire.hex().upper()})

    values = [
        ("null", ("null", None)),
        ("register value", ("u32", 4812345)),
        ("negative power", ("i32", -1250)),
        ("scaler unit", ("struct", [("i8", -1), ("enum", 35)])),
        ("all integers", ("struct", [("i8", -128), ("i16", -32768), ("i64", -2), ("u8", 255), ("u16", 65535), ("u64", 18446744073709551615), ("bcd", 0x42), ("bool", True)])),
        ("floats", ("struct", [("f32", 1.5), ("f64", -2.25)])),
        ("strings", ("struct", [("vis", b"IOT2026000017"), ("utf8", "Rp 1.500".encode()), ("octets", bytes(range(6)))])),
        ("long octet string", ("octets", bytes(range(200)))),
        ("bit string", ("bits", (11, bytes([0xA5, 0xE0])))),
        ("dates", ("struct", [("dt", bytes.fromhex("07EA0A0804132D1E00FE5C00")), ("date", bytes.fromhex("07EA0A08FF")), ("time", bytes.fromhex("132D1EFF"))])),
        ("profile buffer", ("array", [("struct", [("octets", bytes.fromhex("07EA0A0804130000FFFE5C00")), ("u32", 4812000 + i * 250), ("u32", 1206000), ("u16", 2301)]) for i in range(3)])),
        ("capture objects", ("array", [("struct", [("u16", 8), ("octets", bytes([0, 0, 1, 0, 0, 255])), ("i8", 2), ("u16", 0)])])),
    ]
    for name, v in values:
        raw, canon = axdr(v)
        out.append({"name": name, "kind": "axdr", "valid": True, "wire": raw.hex().upper(), "canonical": canon})
    for name, wire in (("truncated u32", "0600"), ("array past end", "0103 1100"), ("unknown tag", "08"), ("octet length past end", "090500"),
                       ("bad length form", "0985"), ("deep nesting", "0201" * 40 + "00")):
        out.append({"name": name, "kind": "axdr", "valid": False, "wire": wire.replace(" ", "")})
    return out


# ---------------------------------------------------------------------------------------------------------------------
# M-Bus (EN 13757-2/-3): frames and the structure of variable-data records (DIF/DIFE/VIF/VIFE, raw value).

def mbus_long(c, a, ci, data):
    body = bytes([c, a, ci]) + data
    return bytes([0x68, len(body), len(body), 0x68]) + body + bytes([sum(body) & 0xFF, 0x16])


def mbus_vectors():
    out = []
    reference = bytes.fromhex("681F1F680802727856341224400107550000000313153100DA023B13018B60043718021816")
    assert mbus_long(0x08, 0x02, 0x72, reference[7:-2]) == reference

    def records(data):
        recs, pos = [], 0
        while pos < len(data):
            start = pos
            dif = data[pos]
            pos += 1
            if dif == 0x2F:
                continue
            if dif & 0x0F == 0x0F:
                break
            dife = []
            last = dif
            while last & 0x80:
                last = data[pos]
                pos += 1
                dife.append(last)
            vif = data[pos]
            pos += 1
            vife = []
            last = vif
            while last & 0x80:
                last = data[pos]
                pos += 1
                vife.append(last)
            size = {0: 0, 1: 1, 2: 2, 3: 3, 4: 4, 5: 4, 6: 6, 7: 8, 9: 1, 10: 2, 11: 3, 12: 4, 14: 6}[dif & 0x0F]
            raw = data[pos:pos + size]
            pos += size
            storage = (dif >> 6) & 1
            tariff = subunit = 0
            for i, e in enumerate(dife):
                storage |= (e & 0x0F) << (1 + 4 * i)
                tariff |= ((e >> 4) & 3) << (2 * i)
                subunit |= ((e >> 6) & 1) << i
            if dif & 0x0F in (9, 10, 11, 12, 14):
                value = int(raw[::-1].hex() or "0")
            else:
                value = int.from_bytes(raw, "little", signed=True) if raw else 0
            recs.append(f"{dif:02X}|{bytes(dife).hex().upper()}|{vif:02X}|{bytes(vife).hex().upper()}|{raw.hex().upper()}|{storage}|{tariff}|{subunit}|{value}|{start}")
        return recs

    def telegram(name, wire):
        user = wire[7:-2]
        out.append({"name": name, "kind": "long", "valid": True, "wire": wire.hex().upper(), "control": wire[4], "address": wire[5], "ci": wire[6],
                    "id": int.from_bytes(user[0:4], "little"), "manufacturer": int.from_bytes(user[4:6], "little"), "medium": user[7],
                    "records": records(user[12:])})

    telegram("water meter reference (checksum 0x18)", reference)
    header = bytes.fromhex("01001026") + (((9 << 10) | (15 << 5) | 20)).to_bytes(2, "little") + bytes([1, 4, 7, 0, 0, 0])
    recs = (bytes.fromhex("0406") + (18244).to_bytes(4, "little") + bytes.fromhex("025A") + (685).to_bytes(2, "little")
            + bytes.fromhex("8C1003") + bytes.fromhex("70284105") + bytes.fromhex("046D") + bytes([45, 19, 8 | (2 << 5), 10 | (3 << 4)])
            + bytes.fromhex("426C") + bytes([1 | (2 << 5), 1 | (3 << 4)]) + bytes.fromhex("C40406") + (17102).to_bytes(4, "little")
            + bytes.fromhex("02FD48") + (2305).to_bytes(2, "little") + bytes.fromhex("2F2F"))
    telegram("heat meter with storage, tariff and fillers", mbus_long(0x08, 0x01, 0x72, header + recs))
    telegram("manufacturer data after 0x0F", mbus_long(0x08, 0x05, 0x72, header + bytes.fromhex("0413E8030000 0F AABBCC".replace(" ", ""))))
    for name, wire in (("ack", "E5"), ("snd_nke", "1040014116"), ("req_ud2 fcb", "107B017C16"),
                       ("select secondary", mbus_long(0x53, 0xFD, 0x52, bytes.fromhex("02002026FFFFFFFF")).hex())):
        out.append({"name": name, "kind": "frame", "valid": True, "wire": wire.upper()})
    for name, wire in (("short bad checksum", "105B015D16"), ("long bad checksum", reference.hex()[:-4] + "1916"),
                       ("length mismatch", "68050668"), ("bad stop", "105B015C17")):
        out.append({"name": name, "kind": "frame", "valid": False, "wire": wire.upper()})
    return out


# ---- CANopen (CiA 301) -------------------------------------------------------------------------------------------
# Independent reference: SDO command bytes are built from the bit fields of CiA 301 §7.2.4.3 (ccs/scs in bits 7-5,
# toggle bit 4, n in bits 3-2 or 3-1, e bit 1, s bit 0). Each vector records the 8 data bytes, the direction and the
# decoded fields "kind|index|sub|expedited|size_indicated|size|toggle|last|data|abort".

def canopen_vectors():
    out = []

    def mux(index, sub):
        return index.to_bytes(2, "little") + bytes([sub])

    def sdo(name, from_server, data, kind, index=0, sub=0, expedited=False, size_ind=False, size=0, toggle=False, last=False, payload=b"", abort=0):
        assert len(data) == 8, name
        fields = f"{kind}|{index:04X}|{sub:02X}|{int(expedited)}|{int(size_ind)}|{size}|{int(toggle)}|{int(last)}|{payload.hex().upper()}|{abort:08X}"
        out.append({"name": name, "kind": "sdo", "from_server": from_server, "data": data.hex().upper(), "fields": fields})

    def expedited(ccs, n_bytes):
        return (ccs << 5) | ((4 - n_bytes) << 2) | 0x02 | 0x01

    # Reads
    sdo("upload request 1000:00", False, bytes([2 << 5]) + mux(0x1000, 0) + bytes(4), "InitiateUploadRequest", 0x1000, 0)
    for n, value in ((4, bytes.fromhex("91010F00")), (2, bytes.fromhex("E803")), (1, b"\x05"), (3, bytes.fromhex("010203"))):
        sdo(f"upload response expedited {n} byte(s)", True, bytes([expedited(2, n)]) + mux(0x1017 if n == 2 else 0x1000, 0) + value + bytes(4 - n),
            "InitiateUploadResponse", 0x1017 if n == 2 else 0x1000, 0, expedited=True, size_ind=True, payload=value)
    sdo("upload response segmented, size 21", True, bytes([(2 << 5) | 0x01]) + mux(0x2100, 0) + (21).to_bytes(4, "little"),
        "InitiateUploadResponse", 0x2100, 0, size_ind=True, size=21)
    for t in (0, 1):
        sdo(f"upload segment request t={t}", False, bytes([(3 << 5) | (t << 4)]) + bytes(7), "UploadSegmentRequest", toggle=bool(t))
    chunk = b"Pump sk"
    sdo("upload segment 7 bytes t=0", True, bytes([(0 << 5) | (0 << 4) | (0 << 1)]) + chunk, "UploadSegmentResponse", payload=chunk)
    tail = b"ng"
    sdo("upload last segment 2 bytes t=1", True, bytes([(1 << 4) | ((7 - len(tail)) << 1) | 1]) + tail + bytes(5),
        "UploadSegmentResponse", toggle=True, last=True, payload=tail)

    # Writes
    sdo("download expedited 2 bytes 1017:00", False, bytes([expedited(1, 2)]) + mux(0x1017, 0) + bytes.fromhex("E8030000"),
        "InitiateDownloadRequest", 0x1017, 0, expedited=True, size_ind=True, payload=bytes.fromhex("E803"))
    sdo("download expedited 1 byte 6200:01", False, bytes([expedited(1, 1)]) + mux(0x6200, 1) + bytes.fromhex("01000000"),
        "InitiateDownloadRequest", 0x6200, 1, expedited=True, size_ind=True, payload=b"\x01")
    sdo("download response", True, bytes([3 << 5]) + mux(0x1017, 0) + bytes(4), "InitiateDownloadResponse", 0x1017, 0)
    sdo("download segmented, size 21", False, bytes([(1 << 5) | 0x01]) + mux(0x2100, 0) + (21).to_bytes(4, "little"),
        "InitiateDownloadRequest", 0x2100, 0, size_ind=True, size=21)
    sdo("download segment 7 bytes t=0", False, bytes([0]) + b"Pump sk", "DownloadSegmentRequest", payload=b"Pump sk")
    sdo("download last segment 3 bytes t=1", False, bytes([(1 << 4) | (4 << 1) | 1]) + b"ang" + bytes(4), "DownloadSegmentRequest", toggle=True, last=True, payload=b"ang")
    for t in (0, 1):
        sdo(f"download segment response t={t}", True, bytes([(1 << 5) | (t << 4)]) + bytes(7), "DownloadSegmentResponse", toggle=bool(t))

    # Aborts
    for code, idx, sub in ((0x06020000, 0x9999, 0), (0x06090011, 0x1018, 9), (0x06010002, 0x1000, 0), (0x06070010, 0x6200, 1), (0x05040000, 0x2100, 0)):
        sdo(f"abort 0x{code:08X}", True, bytes([4 << 5]) + mux(idx, sub) + code.to_bytes(4, "little"), "Abort", idx, sub, abort=code)

    # COB-ID classification (function|node|pdo)
    def cob(cob_id, function, node=0, pdo=0):
        out.append({"name": f"cob-id 0x{cob_id:03X}", "kind": "cob", "id": cob_id, "fields": f"{function}|{node}|{pdo}"})

    cob(0x000, "Nmt")
    cob(0x080, "Sync")
    cob(0x100, "Time")
    cob(0x7E5, "Lss")
    for node in (1, 5, 127):
        cob(0x080 + node, "Emergency", node)
        for n in range(1, 5):
            cob(0x180 + (n - 1) * 0x100 + node, "Tpdo", node, n)
            cob(0x200 + (n - 1) * 0x100 + node, "Rpdo", node, n)
        cob(0x580 + node, "SdoResponse", node)
        cob(0x600 + node, "SdoRequest", node)
        cob(0x700 + node, "Heartbeat", node)
    cob(0x7FF, "Other")   # 0x780–0x7FF is not in the predefined connection set (heartbeat ends at 0x77F)
    cob(0x780, "Other")

    # Emergency
    for code, reg, mfr in ((0x4210, 0x09, bytes([1, 2, 3, 4, 5])), (0x0000, 0x00, bytes(5)), (0x8130, 0x11, b"\xAA" * 5)):
        data = code.to_bytes(2, "little") + bytes([reg]) + mfr
        out.append({"name": f"emcy 0x{code:04X}", "kind": "emcy", "data": data.hex().upper(), "fields": f"{code:04X}|{reg:02X}|{mfr.hex().upper()}"})

    # Invalid SDO data (short frames, block transfer specifiers)
    for name, data, from_server in (("short", "40001000", False), ("block upload request (ccs 5)", "A000100000000000", False),
                                    ("block download response (scs 5)", "A000100000000000", True)):
        out.append({"name": f"invalid: {name}", "kind": "sdo-invalid", "from_server": from_server, "data": data})
    return out


# ---- SAE J1939 -----------------------------------------------------------------------------------------------------
# Independent reference built from J1939-21 (identifier layout P|EDP|DP|PF|PS|SA, TP.CM layouts), J1939-81 (NAME bit
# fields) and J1939-73 (DTC with SPN conversion method 0). Fields are "|"-separated strings shared by C# and Rust.

def j1939_vectors():
    out = []

    def ident(priority, edp, dp, pf, ps, sa):
        can_id = (priority << 26) | (edp << 25) | (dp << 24) | (pf << 16) | (ps << 8) | sa
        pgn = (edp << 17) | (dp << 16) | (pf << 8) | (ps if pf >= 240 else 0)
        dest = ps if pf < 240 else 0xFF
        out.append({"name": f"id 0x{can_id:08X}", "kind": "id", "can_id": can_id, "fields": f"{priority}|{pgn}|{dest}|{sa}"})

    ident(3, 0, 0, 0xF0, 0x04, 0x00)     # EEC1 from the engine
    ident(6, 0, 0, 0xFE, 0xF1, 0x00)     # CCVS1
    ident(6, 0, 0, 0xEA, 0x00, 0xF9)     # request to 0x00 from 0xF9
    ident(7, 0, 0, 0xEC, 0xFF, 0x00)     # TP.CM broadcast
    ident(7, 0, 0, 0xEB, 0x21, 0x17)     # TP.DT to 0x21
    ident(6, 0, 0, 0xEE, 0xFF, 0x80)     # address claimed
    ident(7, 0, 1, 0xF0, 0x04, 0x03)     # data page 1
    ident(0, 1, 0, 0x10, 0x20, 0x30)     # extended data page, PDU1

    def name(identity, manufacturer, ecu, func_inst, function, vsys, vsys_inst, industry, aac):
        value = (identity | (manufacturer << 21) | (ecu << 32) | (func_inst << 35) | (function << 40) | (vsys << 49)
                 | (vsys_inst << 56) | (industry << 60) | (aac << 63))
        out.append({"name": f"NAME function {function}", "kind": "name", "data": value.to_bytes(8, "little").hex().upper(),
                    "fields": f"{identity}|{manufacturer}|{ecu}|{func_inst}|{function}|{vsys}|{vsys_inst}|{industry}|{aac}"})

    name(0x0A2B3, 0x146, 0, 0, 0, 0, 0, 1, 0)
    name(0x1D0F5, 0x7FF, 0, 0, 249, 0, 0, 1, 1)
    name(0x1FFFFF, 0x7FF, 7, 31, 255, 127, 15, 7, 1)
    name(1, 1, 0, 0, 30, 0, 0, 2, 1)

    def dtc(spn, fmi, oc):
        data = bytes([spn & 0xFF, (spn >> 8) & 0xFF, ((spn >> 16) << 5) | fmi, oc])
        out.append({"name": f"dtc SPN {spn} FMI {fmi}", "kind": "dtc", "data": data.hex().upper(), "fields": f"{spn}|{fmi}|{oc}"})

    for args in ((100, 1, 1), (110, 0, 2), (190, 2, 0), (520192, 31, 126), (524287, 0, 127), (3226, 16, 5)):
        dtc(*args)

    def tp(control, data, fields):
        out.append({"name": f"tp.cm {control}", "kind": "tp", "data": data.hex().upper(), "fields": fields})

    def pgn3(p):
        return bytes([p & 0xFF, (p >> 8) & 0xFF, p >> 16])

    tp("bam", bytes([32]) + (18).to_bytes(2, "little") + bytes([3, 0xFF]) + pgn3(0xFEEC), f"Broadcast|18|3|0|0|0|{0xFEEC}")
    tp("rts", bytes([16]) + (1785).to_bytes(2, "little") + bytes([255, 0xFF]) + pgn3(0xEF00), f"RequestToSend|1785|255|0|255|0|{0xEF00}")
    tp("cts", bytes([17, 255, 1, 0xFF, 0xFF]) + pgn3(0xEF00), f"ClearToSend|0|255|1|0|0|{0xEF00}")
    tp("ack", bytes([19]) + (1785).to_bytes(2, "little") + bytes([255, 0xFF]) + pgn3(0xEF00), f"EndOfMessageAck|1785|255|0|0|0|{0xEF00}")
    tp("abort", bytes([255, 3, 0xFF, 0xFF, 0xFF]) + pgn3(0xFEEC), f"Abort|0|0|0|0|3|{0xFEEC}")

    def spn(pgn, data, fields):
        out.append({"name": f"spn pgn {pgn}", "kind": "spn", "pgn": pgn, "data": data.hex().upper(), "fields": fields})

    # EEC1: torque mode 1, demand 0 %, actual 15 %, 1500 rpm, controlling device n/a
    spn(0xF004, bytes([0xF1, 125, 140]) + (12000).to_bytes(2, "little") + bytes([0xFF, 0xFF, 0xFF]), "899=1|512=0|513=15|190=1500|1483=na")
    spn(0xFEF1, bytes([0xFF]) + (88 * 256).to_bytes(2, "little") + bytes(5 * [0xFF]), "84=88")
    spn(0xFEEE, bytes([130, 81]) + int((99 + 273) / 0.03125).to_bytes(2, "little") + bytes(4 * [0xFF]), "110=90|174=41|175=99")
    spn(0xFEF7, bytes([0xFF, 0xFF]) + (562).to_bytes(2, "little") + (552).to_bytes(2, "little") + bytes([0xFF, 0xFF]), "167=28.1|168=27.6")
    spn(0xFEE5, (256872).to_bytes(4, "little") + bytes(4 * [0xFF]), "247=12843.6|249=na")
    return out


def iec104_vectors():
    """IEC 60870-5-104 APDUs. The canonical text is built from the inputs, not by parsing the bytes."""
    import struct
    out = []

    def num(v):
        v = float(v)
        return str(int(v)) if v.is_integer() else repr(v)

    def cp56(t):
        y, mo, d, h, mi, sec, ms, iv, su = t
        b = struct.pack("<H", sec * 1000 + ms) + bytes([mi | (0x80 if iv else 0), h | (0x80 if su else 0), d, mo, y % 100])
        text = f"{y:04}-{mo:02}-{d:02} {h:02}:{mi:02}:{sec:02}.{ms:03}" + ("I" if iv else "") + ("S" if su else "")
        return b, text

    def u(name, code):
        out.append({"name": name, "data": bytes([0x68, 4, code, 0, 0, 0]).hex().upper(), "fields": f"U|{code}"})

    for name, code in [("STARTDT act", 0x07), ("STARTDT con", 0x0B), ("STOPDT act", 0x13), ("STOPDT con", 0x23), ("TESTFR act", 0x43), ("TESTFR con", 0x83)]:
        u(name, code)
    for nr in (0, 2, 32767):
        out.append({"name": f"S N(R)={nr}", "data": (bytes([0x68, 4, 1, 0]) + struct.pack("<H", nr << 1)).hex().upper(), "fields": f"S|{nr}"})

    # Each object: (ioa, element bytes, canonical value, quality flags, qualifier, time text)
    def i_frame(name, ns, nr, type_id, cot, ca, objects, sq=False, neg=False, test=False, org=0):
        body = bytes([type_id, (0x80 if sq else 0) | len(objects), cot | (0x40 if neg else 0) | (0x80 if test else 0), org]) + struct.pack("<H", ca)
        for k, (ioa, element, *_rest) in enumerate(objects):
            if not sq or k == 0:
                body += struct.pack("<I", ioa)[:3]
            body += element
        apdu = bytes([0x68, len(body) + 4]) + struct.pack("<HH", ns << 1, nr << 1) + body
        objs = ";".join(f"{ioa}:{value}:{q}:{qual}:{t}" for ioa, _e, value, q, qual, t in objects)
        out.append({"name": name, "data": apdu.hex().upper(),
                    "fields": f"I|{ns}|{nr}|{type_id}|{1 if sq else 0}|{cot}|{1 if neg else 0}|{1 if test else 0}|{org}|{ca}|{objs}"})

    OV, TR, CY, CA, BL, SB, NT, IV = 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80
    T1 = cp56((2026, 10, 9, 6, 30, 15, 250, False, False))
    T2 = cp56((2025, 12, 31, 23, 59, 59, 999, True, True))
    T3 = cp56((2000, 2, 29, 0, 0, 0, 0, False, False))
    f20 = struct.pack("<f", 20.0)
    f01 = struct.pack("<f", 0.1)
    v01 = struct.unpack("<f", f01)[0]

    i_frame("C_IC_NA_1 act QOI 20", 0, 0, 100, 6, 1, [(0, bytes([20]), "0", 0, 20, "-")])
    i_frame("C_IC_NA_1 actcon negative", 4, 1, 100, 7, 1, [(0, bytes([20]), "0", 0, 20, "-")], neg=True)
    i_frame("M_SP_NA_1 two objects", 1, 2, 1, 20, 1, [(1101, bytes([0x01]), "1", 0, 0, "-"), (1102, bytes([0x90]), "0", IV | BL, 0, "-")])
    i_frame("M_DP_NA_1 SQ=1", 2, 2, 3, 20, 1, [(1001, bytes([0x02]), "2", 0, 0, "-"), (1002, bytes([0x01]), "1", 0, 0, "-"), (1003, bytes([0x43]), "3", NT, 0, "-")], sq=True)
    i_frame("M_ST_NA_1 transient", 3, 2, 5, 20, 1, [(2007, bytes([0xFD, 0x80]), "-3", TR | IV, 0, "-")])
    i_frame("M_ST_NA_1 +63", 3, 2, 5, 3, 1, [(2007, bytes([0x3F, 0x00]), "63", 0, 0, "-")])
    i_frame("M_BO_NA_1", 5, 0, 7, 3, 2, [(70000, struct.pack("<I", 0xDEADBEEF) + bytes([0x20]), num(0xDEADBEEF), SB, 0, "-")])
    i_frame("M_ME_NA_1 -0.5 blocked", 6, 0, 9, 3, 1, [(1, struct.pack("<h", -16384) + bytes([0x10]), num(-0.5), BL, 0, "-")])
    i_frame("M_ME_NB_1 -1234", 7, 0, 11, 3, 1, [(1, struct.pack("<h", -1234) + bytes([0]), "-1234", 0, 0, "-")])
    i_frame("M_ME_NC_1 floats", 8, 0, 13, 1, 1, [(2001, f20 + bytes([0]), "20", 0, 0, "-"), (2002, f01 + bytes([0x01]), num(v01), OV, 0, "-")])
    i_frame("M_IT_NA_1 sequence 7 carry adjusted", 9, 0, 15, 37, 1, [(3001, struct.pack("<i", 1281614) + bytes([0x67]), "1281614", CY | CA, 7, "-")])
    i_frame("M_IT_NA_1 negative invalid", 9, 0, 15, 38, 1, [(3002, struct.pack("<i", -5) + bytes([0x9F]), "-5", IV, 31, "-")])
    i_frame("M_ME_ND_1", 10, 0, 21, 2, 1, [(5, struct.pack("<h", 8192), num(0.25), 0, 0, "-")])
    i_frame("M_SP_TB_1 IV SU", 11, 0, 30, 3, 1, [(1101, bytes([0x01]) + T2[0], "1", 0, 0, T2[1])])
    i_frame("M_DP_TB_1 leap day", 11, 0, 31, 11, 1, [(1001, bytes([0x00]) + T3[0], "0", 0, 0, T3[1])])
    i_frame("M_ME_TF_1", 12, 0, 36, 3, 1, [(2001, f20 + bytes([0]) + T1[0], "20", 0, 0, T1[1])])
    i_frame("M_ME_TE_1", 12, 0, 35, 3, 1, [(2006, struct.pack("<h", 71) + bytes([0]) + T1[0], "71", 0, 0, T1[1])])
    i_frame("M_IT_TB_1", 13, 0, 37, 37, 1, [(3001, struct.pack("<i", 42) + bytes([0x01]) + T1[0], "42", 0, 1, T1[1])])
    i_frame("C_SC_NA_1 select on QU 1", 0, 14, 45, 6, 1, [(5005, bytes([0x85]), "1", 0, 0x84, "-")])
    i_frame("C_DC_NA_1 execute close", 1, 14, 46, 6, 1, [(5001, bytes([0x02]), "2", 0, 0, "-")])
    i_frame("C_DC_NA_1 actterm", 20, 2, 46, 10, 1, [(5001, bytes([0x02]), "2", 0, 0, "-")])
    i_frame("C_RC_NA_1 higher", 2, 14, 47, 6, 1, [(5004, bytes([0x02]), "2", 0, 0, "-")])
    i_frame("C_SE_NA_1 0.75", 3, 14, 48, 6, 1, [(6002, struct.pack("<h", 24576) + bytes([0]), num(0.75), 0, 0, "-")])
    i_frame("C_SE_NB_1 select", 3, 14, 49, 6, 1, [(6003, struct.pack("<h", -300) + bytes([0x80]), "-300", 0, 0x80, "-")])
    i_frame("C_SE_NC_1 1.5", 4, 14, 50, 6, 1, [(6001, struct.pack("<f", 1.5) + bytes([0]), "1.5", 0, 0, "-")])
    i_frame("C_BO_NA_1", 4, 14, 51, 6, 1, [(6100, struct.pack("<I", 0x0000FF00), "65280", 0, 0, "-")])
    i_frame("C_DC_TA_1", 5, 14, 59, 6, 1, [(5001, bytes([0x01]) + T1[0], "1", 0, 0, T1[1])])
    i_frame("C_CS_NA_1", 6, 14, 103, 6, 1, [(0, T1[0], "0", 0, 0, T1[1])])
    i_frame("C_CI_NA_1 general", 7, 14, 101, 6, 1, [(0, bytes([0x05]), "0", 0, 5, "-")])
    i_frame("C_RD_NA_1", 8, 14, 102, 5, 1, [(2001, b"", "0", 0, 0, "-")])
    i_frame("M_EI_NA_1", 0, 0, 70, 4, 1, [(0, bytes([0x00]), "0", 0, 0, "-")])
    i_frame("C_TS_TA_1", 9, 14, 107, 6, 1, [(0, struct.pack("<H", 0x55AA) + T1[0], "21930", 0, 0, T1[1])])
    i_frame("C_RP_NA_1", 10, 14, 105, 6, 1, [(0, bytes([0x01]), "0", 0, 1, "-")])
    i_frame("broadcast, test, originator, 24-bit IOA", 32767, 32767, 1, 3, 0xFFFF, [(0xFFFFFF, bytes([0x01]), "1", 0, 0, "-")], test=True, org=7)

    def bad(name, hexdata):
        out.append({"name": name, "data": hexdata, "fields": "error"})

    bad("U frame without function", "680403000000")
    bad("S frame with payload", "68050100000000")
    bad("length byte disagrees", "680507000000")
    bad("I frame without ASDU", "680400000000")
    bad("I frame N(R) low bit set", "680E0000010064010600010000000014")
    bad("unknown type", "680E00000000FA010600010000000000")
    bad("count larger than data", "680E000000000302030001 00E9030002".replace(" ", ""))
    bad("no objects", "680A000000000D0003000100")
    bad("31 February", "6819" "00000000" "240103000100" "D10700" "0000A04100" "923B1E061F021A")
    bad("year 100", "6819" "00000000" "240103000100" "D10700" "0000A04100" "923B1E06090A64")
    return out


def ntp_vectors():
    """NTP packets, timestamps and exchanges (RFC 5905). Expected values are computed from the inputs with integer math."""
    import struct
    import datetime
    out = []
    era0 = datetime.datetime(1900, 1, 1)
    era1 = datetime.datetime(2036, 2, 7, 6, 28, 16)

    def to_text(raw):
        sec, frac = raw >> 32, raw & 0xFFFFFFFF
        base = era1 if sec & 0x80000000 == 0 else era0
        ticks = sec * 10_000_000 + ((frac * 10_000_000 + (1 << 31)) >> 32)
        t = base + datetime.timedelta(microseconds=ticks // 10)
        return t.strftime("%Y-%m-%dT%H:%M:%S") + f".{(ticks % 10_000_000):07d}"

    def ascii_id(i):
        b = i.to_bytes(4, "big").split(b"\0")[0]
        return b.decode("ascii") if all(0x20 <= c <= 0x7E for c in b) else f"0x{i:08X}"

    def packet(name, leap, version, mode, stratum, poll, precision, rdelay, rdisp, refid, ref, orig, recv, xmit, trailer=b""):
        data = bytes([(leap << 6) | (version << 3) | mode, stratum, poll & 0xFF, precision & 0xFF])
        data += struct.pack(">IIIQQQQ", rdelay, rdisp, refid, ref, orig, recv, xmit) + trailer
        ref_text = ascii_id(refid) if stratum <= 1 else ".".join(str(x) for x in refid.to_bytes(4, "big"))
        out.append({"name": name, "kind": "packet", "data": data.hex().upper(),
                    "fields": f"{leap}|{version}|{mode}|{stratum}|{poll}|{precision}|{rdelay}|{rdisp}|{ref_text}|{ref:016X}|{orig:016X}|{recv:016X}|{xmit:016X}|{len(trailer)}"})

    gps = int.from_bytes(b"GPS\0", "big")
    packet("client request v4", 0, 4, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x83AA7E8000000000)
    packet("client request v3 poll 6", 0, 3, 3, 0, 6, -6, 0, 0, 0, 0, 0, 0, 0xE9A1B2C312345678)
    packet("stratum 1 GPS answer", 0, 4, 4, 1, 6, -23, 0x3EB, 0x4000, gps, 0xE9A1B2BB00000000, 0xE9A1B2C312345678, 0xE9A1B2C380000000, 0xE9A1B2C380100000)
    packet("stratum 2 relay", 0, 4, 4, 2, 10, -20, 0x00012000, 0x0000A000, 0xC0000211, 0xE9A1B2BB00000000, 1, 2, 3)
    packet("kiss-o'-death RATE", 3, 4, 4, 0, 0, 0, 0, 0, int.from_bytes(b"RATE", "big"), 0, 0xE9A1B2C312345678, 0, 0)
    packet("leap second pending", 1, 4, 4, 1, 4, -18, 0, 0x10, int.from_bytes(b"PPS\0", "big"), 0, 0, 0, 0xE9A1B2C300000000)
    packet("unsynchronised stratum 16", 3, 4, 4, 16, 3, -20, 0, 0, 0x7F000001, 0, 0, 0, 0)
    packet("broadcast", 0, 4, 5, 2, 6, -20, 0, 0, 0x0A000001, 0, 0, 0, 0xE9A1B2C300000000)
    packet("with MAC (key id + 16-byte digest)", 0, 4, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xE9A1B2C300000000, struct.pack(">I", 7) + bytes(range(16)))
    packet("era 1 transmit time", 0, 4, 4, 1, 6, -20, 0, 0, gps, 0, 0, 0, 0x0000000180000000)
    out.append({"name": "47 octets", "kind": "packet", "data": "23" + "00" * 46, "fields": "error"})
    out.append({"name": "empty", "kind": "packet", "data": "", "fields": "error"})

    for raw in (0x83AA7E8000000000, 0x83AA7E8080000000, 0xE9A1B2C312345678, 0xFFFFFFFFFFFFFFFF, 0x8000000000000000, 0x0000000000000000, 0x7FFFFFFF00000000, 0x0000000180000000):
        out.append({"name": f"timestamp {raw:016X}", "kind": "timestamp", "raw": f"{raw:016X}", "fields": to_text(raw)})

    def signed(x):
        x &= (1 << 64) - 1
        return x - (1 << 64) if x >> 63 else x

    def exchange(name, t1, t2, t3, t4):
        # Ticks of 100 ns, rounded like the C# double path within one tick.
        off = (signed(t2 - t1) + signed(t3 - t4)) / 2
        dly = max(0, signed(t4 - t1) - signed(t3 - t2))
        out.append({"name": name, "kind": "exchange", "t1": f"{t1:016X}", "t2": f"{t2:016X}", "t3": f"{t3:016X}", "t4": f"{t4:016X}",
                    "fields": f"{round(off * 10_000_000 / 2**32)}|{round(dly * 10_000_000 / 2**32)}"})

    base = 0xE9A1B2C300000000
    ms = lambda v: int(v * 2**32 / 1000)
    exchange("server 100 ms ahead, 50 ms each way", base, base + ms(150), base + ms(151), base + ms(101))
    exchange("client 2.5 s ahead", base, base - ms(2480), base - ms(2479), base + ms(42))
    exchange("asymmetric path", base, base + ms(80), base + ms(81), base + ms(91))
    exchange("across the 2036 era boundary", 0xFFFFFFFF80000000, 0x0000000000000000, 0x0000000000100000, 0x0000000080000000)
    exchange("negative delay clamps to zero", base, base + ms(50), base + ms(90), base + ms(10))
    return out


def ndef_vectors():
    """NDEF messages and Type 2 tag data areas. Records are built from fields; the expected text lists them back."""
    out = []

    def record(tnf, rtype=b"", payload=b"", rid=b"", mb=True, me=True, cf=False, short=None):
        short = len(payload) <= 255 if short is None else short
        h = (0x80 if mb else 0) | (0x40 if me else 0) | (0x20 if cf else 0) | (0x10 if short else 0) | (0x08 if rid else 0) | tnf
        b = bytes([h, len(rtype)]) + (bytes([len(payload)]) if short else len(payload).to_bytes(4, "big"))
        if rid:
            b += bytes([len(rid)])
        return b + rtype + rid + payload

    def text(t):
        return f"{t[0]}|{t[1].decode('ascii')}|{t[2].decode('ascii')}|{t[3].hex().upper()}"

    def message(name, records, canonical=True):
        data = b""
        for i, (tnf, rtype, payload, rid) in enumerate(records):
            data += record(tnf, rtype, payload, rid, mb=i == 0, me=i == len(records) - 1)
        out.append({"name": name, "kind": "ndef", "data": data.hex().upper(), "canonical": canonical,
                    "fields": ";".join(text(r) for r in [(t, ty, i, p) for t, ty, p, i in records])})

    lang = b"en"
    message("URI https://www.nxp.com", [(1, b"U", b"\x02nxp.com", b"")])
    message("Text Hello (en)", [(1, b"T", bytes([len(lang)]) + lang + b"Hello", b"")])
    message("Text in Indonesian", [(1, b"T", b"\x02idSelamat datang", b"")])
    message("URI tel:", [(1, b"U", b"\x05+62215550123", b"")])
    message("URI without prefix", [(1, b"U", b"\x00geo:-6.2,106.8", b"")])
    message("MIME vCard", [(2, b"text/vcard", b"BEGIN:VCARD\nVERSION:3.0\nFN:Site Office\nEND:VCARD", b"")])
    message("external type with id", [(4, b"example.com:asset", b"P-0007", b"a1")])
    message("Android application record", [(4, b"android.com:pkg", b"com.example.maintenance", b"")])
    message("absolute URI type", [(3, b"https://example.com/schema/v1", b"", b"")])
    inner = record(1, b"U", b"\x04docs.example.com/p7", mb=True, me=False) + record(1, b"T", b"\x02enPump 7", mb=False, me=True)
    message("Smart Poster", [(1, b"Sp", inner, b"")])
    wsc_cred = (b"\x10\x26\x00\x01\x01" + b"\x10\x45\x00\x05Plant" + b"\x10\x03\x00\x02\x00\x20" + b"\x10\x0F\x00\x02\x00\x08"
                + b"\x10\x27\x00\x04abcd" + b"\x10\x20\x00\x06" + b"\xFF" * 6)
    message("Wi-Fi credential", [(2, b"application/vnd.wfa.wsc", b"\x10\x0E" + len(wsc_cred).to_bytes(2, "big") + wsc_cred, b"")])
    message("three records", [(1, b"T", b"\x02enA", b""), (1, b"U", b"\x03example.com", b""), (2, b"application/octet-stream", bytes(range(16)), b"")])
    message("long record, 4-byte length", [(2, b"application/json", b"x" * 300, b"")])
    message("unknown TNF payload", [(5, b"", b"\x01\x02\x03", b"")])
    out.append({"name": "empty message", "kind": "ndef", "data": "D00000", "canonical": True, "fields": "0|||"})

    # Not what our encoder writes, but valid: a short payload in a long record, and chunked records.
    out.append({"name": "short payload in a long record", "kind": "ndef", "canonical": False,
                "data": record(1, b"T", b"\x02enHi", short=False).hex().upper(), "fields": text((1, b"T", b"", b"\x02enHi"))})
    chunks = record(2, b"text/plain", b"abc", mb=True, me=False, cf=True) + record(6, b"", b"def", mb=False, me=False, cf=True) + record(6, b"", b"gh", mb=False, me=True)
    out.append({"name": "chunked record", "kind": "ndef", "canonical": False, "data": chunks.hex().upper(), "fields": text((2, b"text/plain", b"", b"abcdefgh"))})

    for name, data in [("no MB", record(1, b"T", b"\x02en", mb=False)), ("no ME", record(1, b"T", b"\x02en", me=False)),
                       ("TNF reserved", record(7)), ("unchanged outside a chunk", record(6, b"", b"x")),
                       ("empty record with payload", record(0, b"", b"x")), ("truncated payload", record(1, b"U", b"\x02nxp.com")[:-3]),
                       ("chunk with a type", record(2, b"a", b"1", mb=True, me=False, cf=True) + record(6, b"b", b"2", mb=False)), ("nothing", b"")]:
        out.append({"name": name, "kind": "ndef", "data": data.hex().upper(), "fields": "error"})

    def tlv_ndef(msg):
        return (bytes([0x03, len(msg)]) if len(msg) < 0xFF else bytes([0x03, 0xFF]) + len(msg).to_bytes(2, "big")) + msg

    uri = record(1, b"U", b"\x02nxp.com")
    area = tlv_ndef(uri) + b"\xFE"
    out.append({"name": "Type 2 data area", "kind": "type2", "data": (area + bytes(144 - len(area))).hex().upper(), "fields": text((1, b"U", b"", b"\x02nxp.com"))})
    area = b"\x01\x03\xA0\x10\x44" + b"\x00\x00" + tlv_ndef(uri) + b"\xFE"
    out.append({"name": "lock control TLV and NULL padding first", "kind": "type2", "data": area.hex().upper(), "fields": text((1, b"U", b"", b"\x02nxp.com"))})
    big = record(1, b"T", b"\x02en" + b"z" * 400)
    out.append({"name": "3-byte NDEF TLV length", "kind": "type2", "data": (tlv_ndef(big) + b"\xFE").hex().upper(), "fields": text((1, b"T", b"", b"\x02en" + b"z" * 400))})
    out.append({"name": "blank tag", "kind": "type2", "data": "0300FE" + "00" * 13, "fields": ""})
    out.append({"name": "TLV longer than the area", "kind": "type2", "data": "0340D10101", "fields": "error"})
    return out


def lwm2m_vectors():
    """LwM2M TLV payloads (OMA-TS-LightweightM2M-Core, 7.4.3). The expected text lists every resource value with its type."""
    import struct
    import datetime
    out = []

    def tlv(kind, ident, value):
        n = len(value)
        length_type = 0 if n < 8 else 1 if n <= 0xFF else 2 if n <= 0xFFFF else 3
        h = (kind << 6) | (0x20 if ident > 0xFF else 0) | (length_type << 3) | (n if length_type == 0 else 0)
        b = bytes([h]) + (ident.to_bytes(2, "big") if ident > 0xFF else bytes([ident]))
        if length_type:
            b += n.to_bytes(length_type, "big")
        return b + value

    def integer(v):
        for size in (1, 2, 4, 8):
            if -(1 << (8 * size - 1)) <= v < (1 << (8 * size - 1)):
                return v.to_bytes(size, "big", signed=True)

    def num(v):
        return str(int(v)) if float(v).is_integer() else repr(float(v))

    def vector(name, path, data, fields):
        out.append({"name": name, "path": path, "data": data.hex().upper(), "fields": fields})

    res = lambda ident, value: tlv(3, ident, value)
    multi = lambda ident, items: tlv(2, ident, b"".join(tlv(1, i, v) for i, v in items))

    device = (res(0, b"Open Mobile Alliance") + res(1, b"Lightweight M2M Client") + res(2, b"345000123") + res(3, b"1.0")
              + multi(6, [(0, integer(1)), (1, integer(5))]) + res(9, integer(100)))
    vector("specification Device example", "/3/0", device,
           "/3/0/0=String:Open Mobile Alliance;/3/0/1=String:Lightweight M2M Client;/3/0/2=String:345000123;/3/0/3=String:1.0;"
           "/3/0/6/0=Integer:1;/3/0/6/1=Integer:5;/3/0/9=Integer:100")
    light = res(5706, b"3000K") + res(5805, struct.pack(">f", 12.25)) + res(5850, b"\x01") + res(5851, integer(60))
    vector("16-bit resource ids (Light Control)", "/3311/0", light,
           "/3311/0/5706=String:3000K;/3311/0/5805=Float:12.25;/3311/0/5850=Boolean:true;/3311/0/5851=Integer:60")
    t1 = res(5700, struct.pack(">f", 21.5)) + res(5701, b"Cel")
    t2 = res(5700, struct.pack(">f", -3.75)) + res(5701, b"Cel")
    vector("two object instances", "/3303", tlv(0, 0, t1) + tlv(0, 1, t2),
           "/3303/0/5700=Float:21.5;/3303/0/5701=String:Cel;/3303/1/5700=Float:-3.75;/3303/1/5701=String:Cel")
    vector("single resource", "/3/0/0", res(0, b"IoTCom"), "/3/0/0=String:IoTCom")
    vector("multiple resource", "/3/0/11", multi(11, [(0, integer(0)), (1, integer(2)), (2, integer(-300))]),
           "/3/0/11/0=Integer:0;/3/0/11/1=Integer:2;/3/0/11/2=Integer:-300")
    vector("resource instance", "/3/0/6/1", tlv(1, 1, integer(5)), "/3/0/6/1=Integer:5")
    stamp = 1_760_000_000
    when = datetime.datetime.fromtimestamp(stamp, datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    vector("time", "/3/0/13", res(13, integer(stamp)), f"/3/0/13=Time:{when}")
    vector("64-bit integer", "/1/0/1", res(1, integer(1 << 40)), f"/1/0/1=Integer:{1 << 40}")
    vector("negative 8-bit integer", "/3/0/9", res(9, integer(-1)), "/3/0/9=Integer:-1")
    vector("double precision float", "/6/0/0", res(0, struct.pack(">d", -6.9147)), f"/6/0/0=Float:{num(-6.9147)}")
    vector("16-bit length", "/3/0/0", res(0, b"a" * 300), "/3/0/0=String:" + "a" * 300)
    vector("unknown object as opaque", "/9999/0", res(1, b"\x01\x02\xFF"), "/9999/0/1=Opaque:0102FF")
    vector("empty string", "/3/0/14", res(14, b""), "/3/0/14=String:")
    for name, path, data in [("value past the end", "/3/0", bytes.fromhex("C8001400")),
                             ("resource instance outside a resource", "/3/0", tlv(1, 0, integer(1))),
                             ("object instance below an instance", "/3/0", tlv(0, 0, res(0, b"x"))),
                             ("3-byte integer", "/3/0/9", res(9, b"\x00\x00\x01")),
                             ("boolean 2", "/3311/0/5850", res(5850, b"\x02")),
                             ("truncated header", "/3/0", b"\xE8\x16")]:
        vector(name, path, data, "error")
    return out


def ocpp_vectors():
    """OCPP-J RPC frames (OCPP-J 1.6 section 4, OCPP 2.0.1 part 4 section 4) and typed payloads.

    Frame vectors give the text of one WebSocket message and what a receiver must make of it: the message type, id and
    action, or the error code it answers with ("null" id when no answer is possible because the id is unreadable).
    Payload vectors are schema-valid examples; a receiver must accept them and re-serialise them unchanged.
    """
    out = []

    def compact(obj):
        return json.dumps(obj, separators=(",", ":"))

    def frame(name, version, text, expected, canonical=True):
        out.append({"kind": "frame", "name": name, "version": version, "json": text, "expected": expected, "canonical": canonical})

    def payload(name, version, action, response, body):
        out.append({"kind": "payload", "name": name, "version": version, "action": action, "response": response, "json": compact(body)})

    # Examples printed in the specifications.
    frame("1.6 spec BootNotification CALL", "1.6",
          compact([2, "19223201", "BootNotification", {"chargePointVendor": "VendorX", "chargePointModel": "SingleSocketCharger"}]),
          "call:19223201:BootNotification")
    frame("1.6 spec BootNotification CALLRESULT", "1.6",
          compact([3, "19223201", {"status": "Accepted", "currentTime": "2013-02-01T20:53:32.486Z", "interval": 300}]), "result:19223201")
    frame("2.0.1 spec CALLERROR", "2.0.1", compact([4, "162376037", "NotSupported", "SetDisplayMessageRequest not implemented", {}]),
          "error-frame:162376037:NotSupported")
    frame("CALLERROR without details", "1.6", compact([4, "7", "GenericError", "x"]), "error-frame:7:GenericError", canonical=False)
    frame("36-character id", "2.0.1", compact([2, "a" * 36, "Heartbeat", {}]), "call:" + "a" * 36 + ":Heartbeat")
    frame("whitespace is allowed", "2.0.1", '[ 2 , "1" , "Heartbeat" , { } ]', "call:1:Heartbeat", canonical=False)

    # What a receiver answers with.
    frame("not JSON (1.6)", "1.6", '[2, "1", ', "answer:null:FormationViolation", canonical=False)
    frame("not JSON (2.0.1)", "2.0.1", "hello", "answer:null:RpcFrameworkError", canonical=False)
    frame("object instead of array", "2.0.1", compact({"a": 1}), "answer:null:RpcFrameworkError", canonical=False)
    frame("37-character id", "2.0.1", compact([2, "a" * 37, "Heartbeat", {}]), "answer:null:RpcFrameworkError", canonical=False)
    frame("numeric id", "1.6", compact([2, 5, "Heartbeat", {}]), "answer:null:FormationViolation", canonical=False)
    frame("empty id", "2.0.1", compact([2, "", "Heartbeat", {}]), "answer:null:RpcFrameworkError", canonical=False)
    frame("unknown message type (2.0.1)", "2.0.1", compact([5, "9", "Heartbeat", {}]), "answer:9:MessageTypeNotSupported", canonical=False)
    frame("unknown message type (1.6)", "1.6", compact([7, "9", {}]), "answer:9:FormationViolation", canonical=False)
    frame("CALL without payload", "2.0.1", compact([2, "3", "Heartbeat"]), "answer:3:RpcFrameworkError", canonical=False)
    frame("CALL payload is an array", "1.6", compact([2, "4", "Heartbeat", []]), "answer:4:FormationViolation", canonical=False)
    frame("CALL payload is an array (2.0.1)", "2.0.1", compact([2, "4", "Heartbeat", []]), "answer:4:FormatViolation", canonical=False)
    frame("CALLRESULT payload is a string", "2.0.1", compact([3, "5", "ok"]), "answer:5:FormatViolation", canonical=False)
    frame("CALLERROR code is a number", "1.6", compact([4, "6", 500, "x", {}]), "answer:6:FormationViolation", canonical=False)

    # Schema-valid payloads (OCPP 1.6 JSON schemas / OCPP 2.0.1 JSON schemas).
    payload("1.6 StatusNotification", "1.6", "StatusNotification", False,
            {"connectorId": 1, "errorCode": "NoError", "status": "Charging", "timestamp": "2026-11-15T08:00:00+00:00"})
    payload("1.6 StartTransaction", "1.6", "StartTransaction", False,
            {"connectorId": 2, "idTag": "04A1B2C3", "meterStart": 1250000, "timestamp": "2026-11-15T08:00:00+00:00"})
    payload("1.6 StartTransaction.conf", "1.6", "StartTransaction", True, {"idTagInfo": {"status": "Accepted"}, "transactionId": 1001})
    payload("1.6 MeterValues with contexts", "1.6", "MeterValues", False,
            {"connectorId": 1, "transactionId": 1001, "meterValue": [{"timestamp": "2026-11-15T08:01:00+00:00", "sampledValue": [
                {"value": "1250367", "context": "Sample.Periodic", "measurand": "Energy.Active.Import.Register", "unit": "Wh"},
                {"value": "22000", "context": "Sample.Periodic", "measurand": "Power.Active.Import", "unit": "W"},
                {"value": "41.5", "context": "Transaction.Begin", "measurand": "SoC", "location": "EV", "unit": "Percent"}]}]})
    payload("1.6 StopTransaction", "1.6", "StopTransaction", False,
            {"idTag": "04A1B2C3", "meterStop": 1262000, "timestamp": "2026-11-15T09:00:00+00:00", "transactionId": 1001, "reason": "EVDisconnected"})
    payload("1.6 SetChargingProfile", "1.6", "SetChargingProfile", False,
            {"connectorId": 1, "csChargingProfiles": {"chargingProfileId": 7, "stackLevel": 0, "chargingProfilePurpose": "TxDefaultProfile",
                                                      "chargingProfileKind": "Relative", "chargingSchedule": {"chargingRateUnit": "W",
                                                      "chargingSchedulePeriod": [{"startPeriod": 0, "limit": 11000}]}}})
    payload("1.6 GetConfiguration.conf", "1.6", "GetConfiguration", True,
            {"configurationKey": [{"key": "HeartbeatInterval", "readonly": False, "value": "300"}], "unknownKey": ["Foo"]})
    payload("1.6 RemoteStartTransaction", "1.6", "RemoteStartTransaction", False, {"connectorId": 1, "idTag": "APP-7731"})
    payload("2.0.1 BootNotification", "2.0.1", "BootNotification", False,
            {"chargingStation": {"serialNumber": "GVC-DC-0001", "model": "IoTCom DC-60", "vendorName": "Gravicode", "firmwareVersion": "2.1.0"}, "reason": "PowerUp"})
    payload("2.0.1 TransactionEvent Started", "2.0.1", "TransactionEvent", False,
            {"eventType": "Started", "timestamp": "2026-11-15T08:00:00+00:00", "triggerReason": "Authorized", "seqNo": 0,
             "transactionInfo": {"transactionId": "TX-01-00001", "chargingState": "Charging"},
             "idToken": {"idToken": "04A1B2C3", "type": "ISO14443"}, "evse": {"id": 1, "connectorId": 1},
             "meterValue": [{"timestamp": "2026-11-15T08:00:00+00:00", "sampledValue": [
                 {"value": 1250000, "context": "Transaction.Begin", "measurand": "Energy.Active.Import.Register", "unitOfMeasure": {"unit": "Wh"}}]}]})
    payload("2.0.1 TransactionEvent Ended", "2.0.1", "TransactionEvent", False,
            {"eventType": "Ended", "timestamp": "2026-11-15T09:00:00+00:00", "triggerReason": "EVCommunicationLost", "seqNo": 7,
             "transactionInfo": {"transactionId": "TX-01-00001", "chargingState": "Idle", "stoppedReason": "EVDisconnected"}})
    payload("2.0.1 GetVariables", "2.0.1", "GetVariables", False,
            {"getVariableData": [{"component": {"name": "OCPPCommCtrlr"}, "variable": {"name": "HeartbeatInterval"}}]})
    payload("2.0.1 GetVariables.conf", "2.0.1", "GetVariables", True,
            {"getVariableResult": [{"attributeStatus": "Accepted", "attributeValue": "300", "component": {"name": "OCPPCommCtrlr"}, "variable": {"name": "HeartbeatInterval"}}]})
    payload("2.0.1 RequestStartTransaction", "2.0.1", "RequestStartTransaction", False,
            {"evseId": 1, "remoteStartId": 42, "idToken": {"idToken": "APP-7731", "type": "Central"}})
    payload("2.0.1 SetChargingProfile", "2.0.1", "SetChargingProfile", False,
            {"evseId": 1, "chargingProfile": {"id": 9, "stackLevel": 0, "chargingProfilePurpose": "TxDefaultProfile", "chargingProfileKind": "Relative",
                                              "chargingSchedule": [{"id": 9, "chargingRateUnit": "A", "chargingSchedulePeriod": [{"startPeriod": 0, "limit": 16, "numberPhases": 3}]}]}})
    payload("2.0.1 Reset.conf", "2.0.1", "Reset", True, {"status": "Scheduled"})
    return out


def knx_vectors():
    """KNX datapoint types (KNX Standard 3/7/2), cEMI L_Data frames (3/6/3) and KNXnet/IP frames (3/8/2, 3/8/4, 3/8/5).

    DPT vectors give a value and its encoding ("short" values travel in the 6 low bits of the APCI byte). cEMI vectors
    give the expected message code, addresses, service and value, or "error". KNXnet/IP vectors give the service and the
    connection header fields, or "error".
    """
    import math
    import struct
    out = []

    def rnd(x):
        # round half away from zero
        return int(math.floor(abs(x) + 0.5)) * (1 if x >= 0 else -1)

    def f16(v):
        for e in range(16):
            m = rnd(v * 100 / (1 << e))
            if -2048 <= m <= 2047:
                raw = (0x8000 if m < 0 else 0) | (e << 11) | (m & 0x7FF)
                return raw.to_bytes(2, "big")
        raise ValueError(v)

    def dpt(name, d, value, data, short=False):
        out.append({"kind": "dpt", "name": name, "dpt": d, "value": value, "hex": data.hex().upper(), "short": short})

    dpt("switch on", "1.001", True, b"\x01", True)
    dpt("switch off", "1.001", False, b"\x00", True)
    dpt("dimming increase 25 %", "3.007", [True, 3], b"\x0B", True)
    dpt("blind stop", "3.008", [False, 0], b"\x00", True)
    dpt("percent 100", "5.001", 100, bytes([255]))
    dpt("percent 40", "5.001", 40, bytes([rnd(40 * 255 / 100)]))
    dpt("percent 70 rounds half up", "5.001", 70, bytes([179]))
    dpt("angle 90", "5.003", 90, bytes([rnd(90 * 255 / 360)]))
    dpt("counter 200", "5.010", 200, bytes([200]))
    dpt("signed -100", "6.010", -100, struct.pack(">b", -100))
    dpt("unsigned 16-bit max", "7.001", 65535, struct.pack(">H", 65535))
    dpt("signed 16-bit -2", "8.001", -2, struct.pack(">h", -2))
    for v in (0, 21.5, -30, 0.01, 670433.28, -671088.64, 42000, 1013.25, -0.5):   # 0x7FFF (670760.96) is "invalid data"
        dpt(f"KNX float {v}", "9.001" if abs(v) < 1000 else "9.004", v, f16(v))
    dpt("time 14:30:15", "10.001", "14:30:15", bytes([14, 30, 15]))
    dpt("date 2026-11-15", "11.001", "2026-11-15", bytes([15, 11, 26]))
    dpt("date 1995-01-02", "11.001", "1995-01-02", bytes([2, 1, 95]))
    dpt("counter 123456", "12.001", 123456, struct.pack(">I", 123456))
    dpt("signed 32-bit -5", "13.001", -5, struct.pack(">i", -5))
    dpt("IEEE float power", "14.056", 1234.5, struct.pack(">f", 1234.5))
    dpt("string", "16.000", "KNX is OK", b"KNX is OK".ljust(14, b"\x00"))
    dpt("scene 5", "17.001", 5, bytes([5]))
    dpt("learn scene 12", "18.001", [True, 12], bytes([0x80 | 12]))
    dpt("HVAC comfort", "20.102", 1, bytes([1]))
    dpt("RGB orange", "232.600", [255, 128, 0], bytes([255, 128, 0]))

    def ia(a, l, d):
        return (a << 12) | (l << 8) | d

    def ga(m, mid, s):
        return (m << 11) | (mid << 8) | s

    def cemi(code, src, dst, apci, data=b"", short=None, group=True, ctrl1=0xBC):
        npdu = 1 + len(data)
        a = apci | (short if short is not None else 0)
        return bytes([code, 0, ctrl1, 0xE0 if group else 0x60]) + src.to_bytes(2, "big") + dst.to_bytes(2, "big") + bytes([npdu, (a >> 8) & 3, a & 0xFF]) + data

    def frame(name, data, code, src, dst, apci, value, short):
        out.append({"kind": "cemi", "name": name, "hex": data.hex().upper(), "code": code, "source": src, "destination": dst,
                    "apci": apci, "value": value, "short": short})

    frame("write temperature", cemi(0x29, ia(1, 1, 4), ga(3, 0, 1), 0x080, f16(21.5)), "DataIndication", "1.1.4", "3/0/1", "GroupValueWrite", f16(21.5).hex().upper(), False)
    frame("write switch on (short)", cemi(0x29, ia(1, 1, 1), ga(1, 0, 1), 0x080, short=1), "DataIndication", "1.1.1", "1/0/1", "GroupValueWrite", "01", True)
    frame("read request", cemi(0x11, 0, ga(1, 1, 1), 0x000), "DataRequest", "0.0.0", "1/1/1", "GroupValueRead", "", False)
    frame("response percent", cemi(0x29, ia(1, 1, 1), ga(1, 3, 2), 0x040, b"\xFF"), "DataIndication", "1.1.1", "1/3/2", "GroupValueResponse", "FF", False)
    frame("confirmation", cemi(0x2E, ia(1, 1, 250), ga(2, 1, 3), 0x080, b"\x80"), "DataConfirmation", "1.1.250", "2/1/3", "GroupValueWrite", "80", False)
    frame("response 6-bit zero", cemi(0x29, ia(1, 1, 2), ga(2, 0, 4), 0x040, short=0), "DataIndication", "1.1.2", "2/0/4", "GroupValueResponse", "00", True)
    frame("additional info skipped", bytes([0x29, 2, 0x03, 0x00]) + cemi(0x29, ia(15, 15, 255), ga(31, 7, 255), 0x080, short=1)[2:],
          "DataIndication", "15.15.255", "31/7/255", "GroupValueWrite", "01", True)
    for name, data in [("message code 0x2A", bytes([0x2A]) + cemi(0x29, 1, 1, 0x80)[1:]),
                       ("length too long", cemi(0x29, 1, 1, 0x80, b"\x01")[:-1]),
                       ("extended frame", cemi(0x29, 1, 1, 0x80, ctrl1=0x3C)),
                       ("too short", b"\x29")]:
        out.append({"kind": "cemi", "name": name, "hex": data.hex().upper(), "code": "error"})

    def knxip(service, body):
        return bytes([6, 0x10]) + service.to_bytes(2, "big") + (6 + len(body)).to_bytes(2, "big") + body

    def hpai(ip=(0, 0, 0, 0), port=0):
        return bytes([8, 1]) + bytes(ip) + port.to_bytes(2, "big")

    def ipf(name, data, service, channel=None, sequence=None, status=None):
        out.append({"kind": "knxip", "name": name, "hex": data.hex().upper(), "service": service, "channel": channel, "sequence": sequence, "status": status})

    ipf("search request (NAT)", knxip(0x0201, hpai()), "SEARCH_REQUEST")
    ipf("connect request tunnel link layer", knxip(0x0205, hpai() + hpai() + bytes([4, 4, 2, 0])), "CONNECT_REQUEST")
    ipf("connect response 1.1.250", knxip(0x0206, bytes([1, 0]) + hpai((192, 168, 1, 10), 3671) + bytes([4, 4, 0x11, 0xFA])), "CONNECT_RESPONSE", 1, None, 0)
    ipf("connect response no more connections", knxip(0x0206, bytes([0, 0x24])), "CONNECT_RESPONSE", 0, None, 0x24)
    ipf("connection state request", knxip(0x0207, bytes([1, 0]) + hpai()), "CONNECTIONSTATE_REQUEST", 1)
    ipf("tunnelling request seq 7", knxip(0x0420, bytes([4, 1, 7, 0]) + cemi(0x11, 0, ga(1, 0, 1), 0x080, short=1)), "TUNNELING_REQUEST", 1, 7, 0)
    ipf("tunnelling ack seq 7", knxip(0x0421, bytes([4, 1, 7, 0])), "TUNNELING_ACK", 1, 7, 0)
    ipf("routing indication", knxip(0x0530, cemi(0x29, ia(1, 1, 4), ga(3, 0, 1), 0x080, f16(21.5))), "ROUTING_INDICATION")
    ipf("disconnect request", knxip(0x0209, bytes([3, 0]) + hpai()), "DISCONNECT_REQUEST", 3)
    for name, data in [("version 2.0", bytes([6, 0x20]) + knxip(0x0201, hpai())[2:]),
                       ("total length mismatch", knxip(0x0201, hpai())[:-1]),
                       ("header length 5", bytes([5]) + knxip(0x0201, hpai())[1:]),
                       ("shorter than the header", b"\x06\x10\x02")]:
        ipf(name, data, "error")
    return out


def bacnet_vectors():
    """BACnet application-tag encodings (ASHRAE 135 clause 20.2) and BACnet/IP datagrams (Annex J BVLC + NPDU + APDU).

    Value vectors give a value and its tagged encoding. Packet vectors give a datagram and what it decodes to (the
    APDU description and its service data), or "error".
    """
    import struct
    out = []

    def tag(number, content, context=False):
        n = len(content)
        first = (number << 4) | (0x08 if context else 0)
        if n < 5:
            return bytes([first | n]) + content
        if n <= 253:
            return bytes([first | 5, n]) + content
        return bytes([first | 5, 254]) + n.to_bytes(2, "big") + content

    def uint(v):
        n = 1
        while n < 8 and v >> (8 * n):
            n += 1
        return v.to_bytes(n, "big")

    def sint(v):
        n = 1
        while n < 8 and not (-(1 << (8 * n - 1)) <= v < (1 << (8 * n - 1))):
            n += 1
        return v.to_bytes(n, "big", signed=True)

    def bits(s):
        unused = (8 - len(s) % 8) % 8
        padded = s + "0" * unused
        return bytes([unused]) + bytes(int(padded[i:i + 8], 2) for i in range(0, len(padded), 8))

    def objid(t, i):
        return ((t << 22) | i).to_bytes(4, "big")

    def value(name, t, v, encoded):
        out.append({"kind": "value", "name": name, "tag": t, "value": v, "hex": encoded.hex().upper()})

    value("null", "Null", None, bytes([0x00]))
    value("true", "Boolean", True, bytes([0x11]))
    value("false", "Boolean", False, bytes([0x10]))
    for v in (0, 255, 256, 70000, 4294967295):
        value(f"unsigned {v}", "UnsignedInteger", v, tag(2, uint(v)))
    for v in (-1, 127, 128, -129, -2147483648):
        value(f"signed {v}", "SignedInteger", v, tag(3, sint(v)))
    value("real 21.5", "Real", 21.5, tag(4, struct.pack(">f", 21.5)))
    value("double 1.5", "Double", 1.5, tag(5, struct.pack(">d", 1.5)))
    value("octet string", "OctetString", "0102FF", tag(6, bytes([1, 2, 0xFF])))
    value("character string", "CharacterString", "AHU", tag(7, b"\x00AHU"))
    value("UTF-8 string", "CharacterString", "Lantai 3 °C", tag(7, b"\x00" + "Lantai 3 °C".encode("utf-8")))
    value("300-character string (16-bit length)", "CharacterString", "a" * 300, tag(7, b"\x00" + b"a" * 300))
    value("status flags", "BitString", "0101", tag(8, bits("0101")))
    value("10-bit string", "BitString", "1111111111", tag(8, bits("1111111111")))
    value("enumerated 3", "Enumerated", 3, tag(9, uint(3)))
    value("date 2026-11-15 Sunday", "Date", [2026, 11, 15, 7], tag(10, bytes([2026 - 1900, 11, 15, 7])))
    value("time 14:30:15.50", "Time", [14, 30, 15, 50], tag(11, bytes([14, 30, 15, 50])))
    value("analog input 1", "ObjectIdentifier", [0, 1], tag(12, objid(0, 1)))
    value("device 2604", "ObjectIdentifier", [8, 2604], tag(12, objid(8, 2604)))

    def bvlc(function, body, origin=None):
        extra = b"" if origin is None else bytes(origin[0]) + origin[1].to_bytes(2, "big")
        return bytes([0x81, function]) + (4 + len(extra) + len(body)).to_bytes(2, "big") + extra + body

    def packet(name, data, describe, service_data=b"", canonical=True):
        out.append({"kind": "packet", "name": name, "hex": data.hex().upper(), "describe": describe, "data": service_data.hex().upper(), "canonical": canonical})

    global_npdu = bytes([0x01, 0x20, 0xFF, 0xFF, 0x00, 0xFF])
    local_npdu = bytes([0x01, 0x00])
    reply_npdu = bytes([0x01, 0x04])
    packet("Who-Is, global broadcast", bvlc(0x0B, global_npdu + bytes([0x10, 0x08])), "WhoIs")
    whois_range = tag(0, uint(10), True) + tag(1, uint(20), True)
    packet("Who-Is 10..20", bvlc(0x0B, local_npdu + bytes([0x10, 0x08]) + whois_range), "WhoIs", whois_range)
    iam = tag(12, objid(8, 2604)) + tag(2, uint(1476)) + tag(9, uint(3)) + tag(2, uint(999))
    packet("I-Am device 2604", bvlc(0x0B, local_npdu + bytes([0x10, 0x00]) + iam), "IAm", iam)
    rp = tag(0, objid(0, 1), True) + tag(1, uint(85), True)
    packet("ReadProperty AI1 present value", bvlc(0x0A, reply_npdu + bytes([0x00, 0x05, 0x07, 0x0C]) + rp), "ReadProperty request #7", rp)
    rp_ack = rp + bytes([0x3E]) + tag(4, struct.pack(">f", 21.5)) + bytes([0x3F])
    packet("ReadProperty-ACK 21.5", bvlc(0x0A, local_npdu + bytes([0x30, 0x07, 0x0C]) + rp_ack), "ReadProperty ComplexACK #7", rp_ack)
    packet("WriteProperty SimpleACK", bvlc(0x0A, local_npdu + bytes([0x20, 0x07, 0x0F])), "WriteProperty SimpleACK #7")
    packet("Error unknown-property", bvlc(0x0A, local_npdu + bytes([0x50, 0x07, 0x0C]) + tag(9, uint(2)) + tag(9, uint(32))), "ReadProperty Error #7: Property/UnknownProperty")
    packet("Reject unrecognized service", bvlc(0x0A, local_npdu + bytes([0x60, 0x07, 0x09])), "Reject #7: UnrecognizedService")
    packet("Abort segmentation not supported", bvlc(0x0A, local_npdu + bytes([0x71, 0x07, 0x04])), "Abort #7: SegmentationNotSupported")
    cov = (tag(0, uint(101), True) + tag(1, objid(8, 2604), True) + tag(2, objid(0, 4), True) + tag(3, uint(57), True)
           + bytes([0x4E]) + tag(0, uint(85), True) + bytes([0x2E]) + tag(4, struct.pack(">f", 700.0)) + bytes([0x2F])
           + tag(0, uint(111), True) + bytes([0x2E]) + tag(8, bits("0000")) + bytes([0x2F]) + bytes([0x4F]))
    packet("unconfirmed COV notification", bvlc(0x0A, local_npdu + bytes([0x10, 0x02]) + cov), "UnconfirmedCovNotification", cov)
    packet("Who-Is forwarded by a BBMD", bvlc(0x04, local_npdu + bytes([0x10, 0x08]), ((192, 168, 1, 10), 47808)), "WhoIs")
    packet("BVLC-Result NAK", bytes([0x81, 0x00, 0x00, 0x06, 0x00, 0x30]), "BVLC-Result 0x0030", canonical=False)
    sub = tag(0, uint(101), True) + tag(1, objid(0, 4), True) + tag(2, b"\x00", True) + tag(3, uint(300), True)
    packet("SubscribeCOV unconfirmed, 300 s", bvlc(0x0A, reply_npdu + bytes([0x00, 0x05, 0x08, 0x05]) + sub), "SubscribeCov request #8", sub)
    for name, data in [("BVLC type 0x82", bytes([0x82]) + bvlc(0x0A, local_npdu + bytes([0x10, 0x08]))[1:]),
                       ("length mismatch", bvlc(0x0A, local_npdu + bytes([0x10, 0x08]))[:-1]),
                       ("NPDU version 2", bvlc(0x0A, bytes([0x02, 0x00, 0x10, 0x08]))),
                       ("truncated confirmed request", bvlc(0x0A, reply_npdu + bytes([0x00, 0x05]))),
                       ("APDU type 9", bvlc(0x0A, local_npdu + bytes([0x90, 0x00]))),
                       ("shorter than the BVLC header", bytes([0x81, 0x0A]))]:
        packet(name, data, "error")
    return out


def enip_vectors():
    """EtherNet/IP encapsulation (Vol. 2 ch. 2), Common Packet Format, CIP Message Router requests and replies with EPATHs
    (Vol. 1 appendix C) and the ListIdentity item.

    Each vector gives bytes and a normalised description that the C# codec and the Rust twin must both produce, or "error".
    """
    import struct
    out = []

    def encap(command, data=b"", session=0, status=0, context=b"\x00" * 8):
        return struct.pack("<HHII", command, len(data), session, status) + context + struct.pack("<I", 0) + data

    def cpf(*items):
        b = struct.pack("<H", len(items))
        for t, d in items:
            b += struct.pack("<HH", t, len(d)) + d
        return b

    def rr(cip):
        return struct.pack("<IH", 0, 10) + cpf((0x0000, b""), (0x00B2, cip))

    def seg(kind, value):
        if value <= 0xFF:
            return bytes([kind, value])
        if value <= 0xFFFF:
            return bytes([kind | 1, 0]) + struct.pack("<H", value)
        return bytes([kind | 2, 0]) + struct.pack("<I", value)

    def sym(name):
        n = name.encode()
        return bytes([0x91, len(n)]) + n + (b"\x00" if len(n) % 2 else b"")

    def req(service, path, data=b""):
        return bytes([service, len(path) // 2]) + path + data

    def rsp(service, status=0, additional=(), data=b""):
        return bytes([service | 0x80, 0, status, len(additional)]) + b"".join(struct.pack("<H", a) for a in additional) + data

    def vector(kind, name, data, expected):
        out.append({"kind": kind, "name": name, "hex": data.hex().upper(), "expected": expected})

    # Encapsulation headers.
    vector("encap", "RegisterSession request", encap(0x65, b"\x01\x00\x00\x00"), "command=0x0065 session=0x00000000 status=0 length=4")
    vector("encap", "RegisterSession reply", encap(0x65, b"\x01\x00\x00\x00", session=0x1001), "command=0x0065 session=0x00001001 status=0 length=4")
    vector("encap", "ListIdentity request", encap(0x63), "command=0x0063 session=0x00000000 status=0 length=0")
    vector("encap", "invalid session", encap(0x6F, session=0x1234, status=0x64), "command=0x006F session=0x00001234 status=100 length=0")
    read_tag = req(0x4C, sym("Line_Speed"), b"\x01\x00")
    vector("encap", "SendRRData with Read Tag", encap(0x6F, rr(read_tag), session=0x1001),
           f"command=0x006F session=0x00001001 status=0 length={len(rr(read_tag))} items=0000:0,00B2:{len(read_tag)}")
    vector("encap", "length past the end", encap(0x65, b"\x01\x00\x00\x00")[:-1], "error")
    vector("encap", "shorter than the header", b"\x65\x00\x04", "error")
    vector("encap", "CPF item past the end", encap(0x6F, struct.pack("<IH", 0, 10) + struct.pack("<HHH", 1, 0xB2, 9) + b"\x0E\x03"), "error")

    # CIP requests.
    vector("request", "Get_Attributes_All Identity", req(0x01, seg(0x20, 1) + seg(0x24, 1)), "service=0x01 path=class:1,instance:1 data=")
    vector("request", "Get_Attribute_Single product name", req(0x0E, seg(0x20, 1) + seg(0x24, 1) + seg(0x30, 7)), "service=0x0E path=class:1,instance:1,attribute:7 data=")
    vector("request", "Set_Attribute_Single assembly 150", req(0x10, seg(0x20, 4) + seg(0x24, 150) + seg(0x30, 3), b"\x01\x02"), "service=0x10 path=class:4,instance:150,attribute:3 data=0102")
    vector("request", "16-bit class and instance", req(0x0E, seg(0x20, 0x300) + seg(0x24, 0x1234) + seg(0x30, 1)), "service=0x0E path=class:768,instance:4660,attribute:1 data=")
    vector("request", "32-bit instance", req(0x0E, seg(0x20, 2) + seg(0x24, 0x10000)), "service=0x0E path=class:2,instance:65536 data=")
    vector("request", "Read Tag odd-length name (padded)", req(0x4C, sym("Recipe1"), b"\x01\x00"), "service=0x4C path=symbol:Recipe1 data=0100")
    vector("request", "Read Tag member", req(0x4C, sym("Program:Main") + sym("Motor"), b"\x01\x00"), "service=0x4C path=symbol:Program:Main,symbol:Motor data=0100")
    vector("request", "Read Tag array element", req(0x4C, sym("Zone_Temps") + seg(0x28, 2), b"\x02\x00"), "service=0x4C path=symbol:Zone_Temps,element:2 data=0200")
    vector("request", "Write Tag REAL", req(0x4D, sym("Line_Speed"), struct.pack("<HH", 0xCA, 1) + struct.pack("<f", 90.5)), "service=0x4D path=symbol:Line_Speed data=CA000100" + struct.pack("<f", 90.5).hex().upper())
    vector("request", "path size past the end", bytes([0x0E, 3, 0x20, 0x01]), "error")
    vector("request", "reserved logical format", req(0x0E, bytes([0x23, 0x00])), "error")
    vector("request", "symbol longer than the path", bytes([0x4C, 1, 0x91, 0x05]), "error")

    # CIP replies.
    vector("response", "Read Tag DINT", rsp(0x4C, data=struct.pack("<Hi", 0xC4, 18204)), "service=0x4C status=0x00 additional= data=C4001C470000")
    vector("response", "path destination unknown", rsp(0x4C, 0x05), "service=0x4C status=0x05 additional= data=")
    vector("response", "general error with additional status", rsp(0x4D, 0x04, (0x2105,)), "service=0x4D status=0x04 additional=2105 data=")
    vector("response", "Unconnected_Send connection failure", rsp(0x52, 0x01, (0x0204,)), "service=0x52 status=0x01 additional=0204 data=")
    vector("response", "not a reply", bytes([0x4C, 0, 0, 0]), "error")
    vector("response", "additional status truncated", bytes([0xCC, 0, 0x04, 2, 0x05, 0x21]), "error")

    # ListIdentity item.
    name = b"1756-L83E/B"
    ident = (struct.pack("<H", 1) + struct.pack(">hH", 2, 44818) + bytes([192, 168, 1, 10]) + b"\x00" * 8
             + struct.pack("<HHHBBHI", 1, 0x0E, 166, 33, 11, 0x0060, 0x00C0FFEE) + bytes([len(name)]) + name + b"\x03")
    vector("identity", "ControlLogix identity", ident, "vendor=1 type=14 product=166 rev=33.11 status=0x0060 serial=0x00C0FFEE name=1756-L83E/B state=3 address=192.168.1.10:44818")
    vector("identity", "name past the end", ident[:-4], "error")
    return out


def ethercat_vectors():
    """EtherCAT frames (ETG.1000.4: EtherCAT header, datagrams with ADP/ADO or logical addresses, the more and circulating
    flags, working counters), mailboxes and CoE SDO messages (ETG.1000.6) and SII images (ETG.2010 categories).

    Each vector gives bytes and a normalised description that the C# codec and the Rust twin must both produce, or "error".
    """
    import struct
    out = []

    def dg(cmd, idx, addr, data, wkc=0, irq=0, more=False, circ=False):
        lf = len(data) | (0x4000 if circ else 0) | (0x8000 if more else 0)
        return struct.pack("<BBIHH", cmd, idx, addr, lf, irq) + data + struct.pack("<H", wkc)

    def payload(*datagrams):
        body = b"".join(datagrams)
        return struct.pack("<H", len(body) | 0x1000) + body

    def ethernet(pl, src=b"\x01" * 6):
        f = b"\xFF" * 6 + src + b"\x88\xA4" + pl
        return f + b"\x00" * max(0, 60 - len(f))

    names = {0: "NOP", 1: "APRD", 2: "APWR", 3: "APRW", 4: "FPRD", 5: "FPWR", 6: "FPRW", 7: "BRD", 8: "BWR", 9: "BRW",
             10: "LRD", 11: "LWR", 12: "LRW", 13: "ARMW", 14: "FRMW"}

    def describe(items):
        return ";".join(f"{names[c]} idx={i} addr=0x{a:08X} len={len(d)} irq=0x{q:04X} wkc={w} c={1 if ci else 0} data={d.hex().upper()}"
                        for c, i, a, d, w, q, ci in items)

    def vector(kind, name, data, expected):
        out.append({"kind": kind, "name": name, "hex": data.hex().upper(), "expected": expected})

    # Datagram payloads (EtherCAT header + datagrams); these must also re-encode to the same bytes.
    brd = payload(dg(7, 1, 0x00000000, b"\x00", wkc=4))
    vector("payload", "BRD slave count", brd, describe([(7, 1, 0, b"\x00", 4, 0, False)]))
    apwr = payload(dg(2, 2, 0x0010FFFF, struct.pack("<H", 0x1002), wkc=1))
    vector("payload", "APWR station address to position 1", apwr, describe([(2, 2, 0x0010FFFF, struct.pack("<H", 0x1002), 1, 0, False)]))
    fprd = payload(dg(4, 3, 0x01301001, b"\x08\x00", wkc=1, more=True), dg(4, 4, 0x01341001, b"\x00\x00", wkc=1))
    vector("payload", "two FPRD datagrams (AL status, status code)", fprd,
           describe([(4, 3, 0x01301001, b"\x08\x00", 1, 0, False), (4, 4, 0x01341001, b"\x00\x00", 1, 0, False)]))
    lrw = payload(dg(12, 9, 0x00000000, bytes([0x05, 0x0D, 0x40, 0x01, 0x60, 0x09, 0x62, 0x02, 0x08, 0x07]), wkc=4, irq=0x0001))
    vector("payload", "LRW process image", lrw, describe([(12, 9, 0, bytes([0x05, 0x0D, 0x40, 0x01, 0x60, 0x09, 0x62, 0x02, 0x08, 0x07]), 4, 1, False)]))
    circ = payload(dg(1, 5, 0x0000FFFE, b"\x11", circ=True))
    vector("payload", "circulating APRD", circ, describe([(1, 5, 0x0000FFFE, b"\x11", 0, 0, True)]))
    sii = payload(dg(5, 6, 0x05021001, b"\x00\x01\x08\x00\x00\x00", wkc=1))
    vector("payload", "FPWR SII read command", sii, describe([(5, 6, 0x05021001, b"\x00\x01\x08\x00\x00\x00", 1, 0, False)]))
    vector("payload", "frame type 2 is not commands", struct.pack("<H", 13 | 0x2000) + dg(7, 1, 0, b"\x00"), "error")
    vector("payload", "reserved header bit", struct.pack("<H", 13 | 0x1800) + dg(7, 1, 0, b"\x00"), "error")
    vector("payload", "length past the end", payload(dg(7, 1, 0, b"\x00"))[:-2], "error")
    vector("payload", "datagram data past the announced length", struct.pack("<H", 13 | 0x1000) + struct.pack("<BBIHH", 7, 1, 0, 5, 0) + b"\x00\x00\x00", "error")
    vector("payload", "unknown command 0x1F", payload(dg(0x1F, 1, 0, b"\x00")), "error")
    vector("payload", "reserved length bits", struct.pack("<H", 13 | 0x1000) + struct.pack("<BBIHH", 7, 1, 0, 1 | 0x0800, 0) + b"\x00\x00\x00", "error")
    vector("payload", "more flag on the last datagram", payload(dg(7, 1, 0, b"\x00", more=True)), "error")
    vector("payload", "bytes after the last datagram", struct.pack("<H", 15 | 0x1000) + dg(7, 1, 0, b"\x00") + b"\x00\x00", "error")

    # Ethernet frames: EtherType and the returned flag (bit 1 of the first source byte).
    vector("ethernet", "sent by the master", ethernet(brd), "returned=0 " + describe([(7, 1, 0, b"\x00", 4, 0, False)]))
    vector("ethernet", "returned by the slaves", ethernet(brd, b"\x03" + b"\x01" * 5), "returned=1 " + describe([(7, 1, 0, b"\x00", 4, 0, False)]))
    vector("ethernet", "IPv4 is not EtherCAT", b"\xFF" * 6 + b"\x01" * 6 + b"\x08\x00" + b"\x00" * 46, "error")
    vector("ethernet", "shorter than an Ethernet header", b"\xFF" * 6 + b"\x01" * 6, "error")

    # Mailboxes.
    def mbx(t, counter, pl, address=0, prio=0, size=0):
        b = struct.pack("<HHBB", len(pl), address, prio << 6, t | (counter << 4)) + pl
        return b + b"\x00" * max(0, size - len(b))

    def sdo(service, command, index, sub, data):
        return struct.pack("<HBHB", service << 12, command, index, sub) + data

    up = sdo(2, 0x40, 0x1008, 0, b"\x00" * 4)
    vector("mailbox", "CoE upload request padded to 128", mbx(3, 1, up, size=128), "type=3 counter=1 address=0x0000 priority=0 payload=" + up.hex().upper())
    vector("mailbox", "mailbox error reply", mbx(0, 2, b"\x01\x00\x02\x00", address=0x1004, prio=1), "type=0 counter=2 address=0x1004 priority=1 payload=01000200")
    vector("mailbox", "length past the end", mbx(3, 1, up)[:-1], "error")
    vector("mailbox", "shorter than the header", b"\x0A\x00\x00", "error")

    # CoE SDO messages.
    vector("sdo", "upload request", up, "service=2 command=0x40 index=0x1008 sub=0 data=00000000")
    vector("sdo", "expedited upload response, 4 bytes", sdo(3, 0x43, 0x1018, 4, struct.pack("<I", 1004)), "service=3 command=0x43 index=0x1018 sub=4 data=EC030000")
    vector("sdo", "expedited download, 2 bytes", sdo(2, 0x2B, 0x8000, 0x15, b"\x02\x00\x00\x00"), "service=2 command=0x2B index=0x8000 sub=21 data=0200")
    vector("sdo", "expedited download, 1 byte", sdo(2, 0x2F, 0x6000, 1, b"\x01\x00\x00\x00"), "service=2 command=0x2F index=0x6000 sub=1 data=01")
    name = b"Sim DI8 24V"
    vector("sdo", "normal upload response", sdo(3, 0x41, 0x1008, 0, struct.pack("<I", len(name)) + name), "service=3 command=0x41 index=0x1008 sub=0 data=" + name.hex().upper())
    vector("sdo", "download response", sdo(3, 0x60, 0x8000, 0x15, b"\x00" * 4), "service=3 command=0x60 index=0x8000 sub=21 data=00000000")
    vector("sdo", "abort: object does not exist", sdo(3, 0x80, 0x2000, 0, struct.pack("<I", 0x06020000)), "service=3 command=0x80 index=0x2000 sub=0 data=00000206 abort=0x06020000")
    vector("sdo", "normal transfer past the end", sdo(3, 0x41, 0x1008, 0, struct.pack("<I", 40) + name), "error")
    vector("sdo", "emergency is not an SDO", struct.pack("<H", 1 << 12) + b"\x00" * 8, "error")
    vector("sdo", "too short", sdo(2, 0x40, 0x1008, 0, b""), "error")

    # SII images.
    def sii_image(vendor, product, rev, serial, mbx_cfg, cats):
        words = [0] * 0x40
        def dword(at, v):
            words[at] = v & 0xFFFF
            words[at + 1] = v >> 16
        dword(0x08, vendor); dword(0x0A, product); dword(0x0C, rev); dword(0x0E, serial)
        for i, v in enumerate(mbx_cfg):
            words[0x18 + i] = v
        b = b"".join(struct.pack("<H", w) for w in words)
        for t, d in cats:
            if len(d) % 2:
                d += b"\x00"
            b += struct.pack("<HH", t, len(d) // 2) + d
        return b + b"\xFF\xFF\x00\x00"

    def strings(*ss):
        return bytes([len(ss)]) + b"".join(bytes([len(x)]) + x.encode() for x in ss)

    def general(name_idx, coe):
        g = bytearray(32)
        g[3] = name_idx
        g[5] = 1 if coe else 0
        return bytes(g)

    def syncm(*sms):
        return b"".join(struct.pack("<HHBBBB", s, l, c, 0, 1, t) for s, l, c, t in sms)

    def pdo(index, sm, *entries):
        b = struct.pack("<HBBBBH", index, len(entries), sm, 0, 0, 0)
        for ei, es, bits in entries:
            b += struct.pack("<HBBBBH", ei, es, 0, 0, bits, 0)
        return b

    ai = sii_image(0xE5C, 4, 0x10000, 1004, (0x1000, 128, 0x1080, 128, 4),
                   [(10, strings("Sim AI4 10V")), (30, general(1, True)),
                    (41, syncm((0x1000, 128, 0x26, 1), (0x1080, 128, 0x22, 2), (0x1180, 8, 0x20, 4))),
                    (50, pdo(0x1A00, 3, (0x6000, 0x11, 16), (0x6010, 0x11, 16), (0x6020, 0x11, 16), (0x6030, 0x11, 16)))])
    vector("sii", "analog input terminal", ai, "vendor=0x00000E5C product=0x00000004 revision=0x00010000 serial=1004 name=Sim AI4 10V "
           "mailbox=1000:128,1080:128,0004 sms=1000:128:26:1,1080:128:22:2,1180:8:20:4 out=0 in=64")
    do = sii_image(0xE5C, 3, 0x10000, 1003, (0, 0, 0, 0, 0),
                   [(10, strings("Vendor", "Sim DO8 24V")), (30, general(2, False)),
                    (41, syncm((0x0F00, 1, 0x44, 3))), (51, pdo(0x1600, 0, *[(0x7000, i + 1, 1) for i in range(8)]))])
    vector("sii", "digital output terminal, second string", do, "vendor=0x00000E5C product=0x00000003 revision=0x00010000 serial=1003 name=Sim DO8 24V "
           "mailbox=0000:0,0000:0,0000 sms=0F00:1:44:3 out=8 in=0")
    vector("sii", "coupler without categories", sii_image(2, 0x044C2C52, 0x00120000, 0, (0, 0, 0, 0, 0), []),
           "vendor=0x00000002 product=0x044C2C52 revision=0x00120000 serial=0 name= mailbox=0000:0,0000:0,0000 sms= out=0 in=0")
    vector("sii", "category past the end", ai[:0x80 + 10], "error")
    vector("sii", "shorter than the header", ai[:100], "error")
    return out


def firmware_vectors():
    """Firmware images and debug structures: Intel HEX (records 00-05), the DFU 1.1 suffix (CRC-32 without the final
    inversion, as dfu-util stores it), ST DfuSe files (prefix, targets, elements), DfuSe memory-layout strings and the
    SEGGER RTT control block (32-bit targets).

    Each vector gives input (hex bytes, or text for "ihex"/"layout") and a normalised description, or "error".
    """
    import struct
    import zlib
    out = []

    def crc32(b):
        return zlib.crc32(b) & 0xFFFFFFFF

    def describe_image(segments, entry):
        segs = ",".join(f"{a:08X}:{len(d)}:{crc32(d):08X}" for a, d in segments)
        return f"segments={segs} entry={entry:08X}" if entry is not None else f"segments={segs} entry=-"

    def vector(kind, name, data, expected):
        out.append({"kind": kind, "name": name, "hex": data.hex().upper() if isinstance(data, bytes) else data.encode().hex().upper(), "expected": expected})

    def rec(rtype, offset, data):
        b = bytes([len(data), offset >> 8, offset & 0xFF, rtype]) + data
        return ":" + (b + bytes([(-sum(b)) & 0xFF])).hex().upper() + "\n"

    # Intel HEX.
    d1 = bytes(range(32))
    text = rec(4, 0, b"\x08\x00") + rec(0, 0x0000, d1[:16]) + rec(0, 0x0010, d1[16:]) + rec(5, 0, struct.pack(">I", 0x08000189)) + rec(1, 0, b"")
    vector("ihex", "two records, linear address, start address", text, describe_image([(0x08000000, d1)], 0x08000189))
    gap = rec(4, 0, b"\x08\x00") + rec(0, 0xFFF0, b"\xAA" * 16) + rec(4, 0, b"\x08\x01") + rec(0, 0x0000, b"\xBB" * 8) + rec(0, 0x0100, b"\xCC" * 4) + rec(1, 0, b"")
    vector("ihex", "contiguous across 64 KiB, then a gap", gap, describe_image([(0x0800FFF0, b"\xAA" * 16 + b"\xBB" * 8), (0x08010100, b"\xCC" * 4)], None))
    seg = rec(2, 0, b"\x10\x00") + rec(0, 0x0010, b"\x01\x02") + rec(3, 0, b"\x00\x00\x01\x00") + rec(1, 0, b"")
    vector("ihex", "segment address (I16HEX)", seg, describe_image([(0x00010010, b"\x01\x02")], 0x00000100))
    vector("ihex", "missing end-of-file", rec(0, 0, b"\x01"), "error")
    bad = rec(0, 0, b"\x01\x02")
    vector("ihex", "checksum mismatch", bad[:-3] + ("00" if bad[-3:-1] != "00" else "01") + "\n" + rec(1, 0, b""), "error")
    vector("ihex", "length does not match", ":0300000001FC\n" + rec(1, 0, b""), "error")
    vector("ihex", "data after end-of-file", rec(1, 0, b"") + rec(0, 0, b"\x01"), "error")
    vector("ihex", "overlapping data", rec(0, 0, b"\x01\x02") + rec(0, 1, b"\x03") + rec(1, 0, b""), "error")

    # DFU suffix.
    def suffix(payload, vid, pid, dev=0xFFFF, bcd=0x0100, sig=b"UFD", length=16, crc=None):
        body = payload + struct.pack("<HHHH", dev, pid, vid, bcd) + sig + bytes([length])
        c = (~crc32(body)) & 0xFFFFFFFF if crc is None else crc
        return body + struct.pack("<I", c)

    def describe_dfu(vid, pid, dev, bcd, targets, segments):
        return f"vid={vid:04X} pid={pid:04X} device={dev:04X} dfu={bcd:04X} targets={','.join(targets)} " + describe_image(segments, None)

    fw = b"firmware image\x00\x01\x02"
    vector("dfu", "plain payload", suffix(fw, 0x0483, 0xDF11), describe_dfu(0x0483, 0xDF11, 0xFFFF, 0x0100, [], [(0, fw)]))
    vector("dfu", "CRC mismatch", suffix(fw, 0x0483, 0xDF11, crc=0x12345678), "error")
    vector("dfu", "no UFD signature", suffix(fw, 0x0483, 0xDF11, sig=b"XYZ"), "error")
    vector("dfu", "suffix length not 16", suffix(fw, 0x0483, 0xDF11, length=17), "error")
    vector("dfu", "shorter than a suffix", b"UFD\x10", "error")

    def dfuse(targets):
        body = b""
        for alt, name, elements in targets:
            elems = b"".join(struct.pack("<II", a, len(d)) + d for a, d in elements)
            nm = name.encode().ljust(255, b"\x00")
            body += b"Target" + bytes([alt]) + struct.pack("<I", 1 if name else 0) + nm + struct.pack("<II", len(elems), len(elements)) + elems
        prefix = b"DfuSe" + bytes([1]) + struct.pack("<I", 11 + len(body)) + bytes([len(targets)])
        return prefix + body

    e1, e2 = bytes(range(64)), b"\x5A" * 10
    good = dfuse([(0, "Internal Flash", [(0x08000000, e1), (0x08004000, e2)])])
    vector("dfu", "DfuSe, one target, two elements", suffix(good, 0x0483, 0xDF11, dev=0x2200, bcd=0x011A),
           describe_dfu(0x0483, 0xDF11, 0x2200, 0x011A, ["0:Internal Flash"], [(0x08000000, e1), (0x08004000, e2)]))
    two = dfuse([(0, "Internal Flash", [(0x08000000, e2)]), (1, "Option Bytes", [(0x1FFFC000, b"\xAA\x55")])])
    vector("dfu", "DfuSe, two targets", suffix(two, 0x0483, 0xDF11, bcd=0x011A),
           describe_dfu(0x0483, 0xDF11, 0xFFFF, 0x011A, ["0:Internal Flash", "1:Option Bytes"], [(0x08000000, e2), (0x1FFFC000, b"\xAA\x55")]))
    broken = bytearray(good)
    struct.pack_into("<I", broken, 11 + 274 + 4, 9999)   # first element size past the end
    vector("dfu", "DfuSe element past the end", suffix(bytes(broken), 0x0483, 0xDF11, bcd=0x011A), "error")
    wrong = bytearray(good)
    struct.pack_into("<I", wrong, 6, len(good) + 1)
    vector("dfu", "DfuSe image size mismatch", suffix(bytes(wrong), 0x0483, 0xDF11, bcd=0x011A), "error")

    # DfuSe memory layouts (alternate-setting strings).
    def layout(name, regions):
        return name + ": " + ",".join(f"{a:08X}+{n}x{sz}{'r' if t & 1 else ''}{'e' if t & 2 else ''}{'w' if t & 4 else ''}" for a, n, sz, t in regions)

    vector("layout", "STM32F4 sectors", "@Internal Flash  /0x08000000/04*016Kg,01*064Kg,03*128Kg",
           layout("Internal Flash", [(0x08000000, 4, 16384, 7), (0x08010000, 1, 65536, 7), (0x08020000, 3, 131072, 7)]))
    vector("layout", "option bytes, readable and writable", "@Option Bytes  /0x1FFFC000/01*016 e", layout("Option Bytes", [(0x1FFFC000, 1, 16, 5)]))
    vector("layout", "two areas", "@Flash /0x08000000/128*002Kg/0x1FFF7800/01*512 a", layout("Flash", [(0x08000000, 128, 2048, 7), (0x1FFF7800, 1, 512, 1)]))
    vector("layout", "not a layout", "Internal Flash", "error")
    vector("layout", "bad sector spec", "@Flash /0x08000000/x*002Kg", "error")

    # RTT control blocks.
    def rtt_block(up, down):
        b = b"SEGGER RTT" + b"\x00" * 6 + struct.pack("<ii", len(up), len(down))
        for d in up + down:
            b += struct.pack("<IIIIII", *d)
        return b

    def rtt_desc(d):
        name, buf, size, wr, rd, flags = d
        avail = wr - rd if wr >= rd else size - rd + wr
        ok = buf != 0 and size > 1 and wr < size and rd < size
        return f"{buf:08X}:{size}:w{wr}:r{rd}:f{flags & 3}:a{avail}:{'ok' if ok else 'bad'}"

    up = [(0x20000480, 0x20000800, 1024, 300, 100, 0), (0x20000490, 0x20000C00, 256, 10, 250, 2)]
    down = [(0x20000480, 0x20000D00, 16, 0, 0, 0)]
    vector("rtt", "two up, one down, a wrapped buffer", rtt_block(up, down), f"up={','.join(rtt_desc(d) for d in up)} down={','.join(rtt_desc(d) for d in down)}")
    vector("rtt", "invalid descriptor (offset past size)", rtt_block([(0, 0x20000800, 64, 70, 0, 1)], []), f"up={rtt_desc((0, 0x20000800, 64, 70, 0, 1))} down=")
    vector("rtt", "wrong ID", b"SEGGER RTX" + b"\x00" * 6 + struct.pack("<ii", 1, 1) + b"\x00" * 48, "error")
    vector("rtt", "implausible buffer count", b"SEGGER RTT" + b"\x00" * 6 + struct.pack("<ii", 200, 1), "error")
    vector("rtt", "descriptors truncated", rtt_block(up, down)[:-4], "error")
    return out


def matter_vectors():
    """Matter: TLV (appendix A), onboarding payloads (QR base38 and manual codes with the Verhoeff check digit, §5.1),
    message and protocol headers (§4.4), the compressed fabric id (§4.3.2.2, the spec's own example) and a complete
    SPAKE2+ exchange as PASE uses it (§3.10) with fixed scalars, computed with pure-Python P-256 arithmetic.

    Each vector gives its input and a normalised description that the C# codec (and, for the codecs, the Rust twin)
    must produce, or "error".
    """
    import hashlib
    import hmac
    import struct
    out = []

    def vector(kind, name, data, expected, **extra):
        v = {"kind": kind, "name": name, "hex": data.hex().upper() if isinstance(data, bytes) else data.encode().hex().upper(), "expected": expected}
        v.update(extra)
        out.append(v)

    # ---- TLV
    def tag_bytes(tag):
        if tag is None:
            return 0, b"", ""
        kind = tag[0]
        if kind == "ctx":
            return 1, bytes([tag[1]]), f"{tag[1]}="
        if kind == "common":
            return (2, struct.pack("<H", tag[1]), f"common:{tag[1]}=") if tag[1] <= 0xFFFF else (3, struct.pack("<I", tag[1]), f"common:{tag[1]}=")
        if kind == "implicit":
            return (4, struct.pack("<H", tag[1]), f"implicit:{tag[1]}=") if tag[1] <= 0xFFFF else (5, struct.pack("<I", tag[1]), f"implicit:{tag[1]}=")
        vendor, profile, number = tag[1], tag[2], tag[3]
        if number <= 0xFFFF:
            return 6, struct.pack("<HHH", vendor, profile, number), f"{vendor:04X}:{profile:04X}:{number}="
        return 7, struct.pack("<HHI", vendor, profile, number), f"{vendor:04X}:{profile:04X}:{number}="

    def el(tag, type_code, body):
        control, tb, _ = tag_bytes(tag)
        return bytes([(control << 5) | type_code]) + tb + body

    def uint(tag, v, size=None):
        size = size or (1 if v <= 0xFF else 2 if v <= 0xFFFF else 4 if v <= 0xFFFFFFFF else 8)
        code = {1: 4, 2: 5, 4: 6, 8: 7}[size]
        return el(tag, code, v.to_bytes(size, "little"))

    def sint(tag, v, size):
        code = {1: 0, 2: 1, 4: 2, 8: 3}[size]
        return el(tag, code, v.to_bytes(size, "little", signed=True))

    def utf8(tag, text):
        b = text.encode()
        return el(tag, 0x0C, bytes([len(b)]) + b)

    def octets(tag, b):
        return el(tag, 0x10, bytes([len(b)]) + b)

    def container(tag, code, *members):
        return el(tag, code, b"".join(members) + b"\x18")

    def d(tag):
        return tag_bytes(tag)[2]

    vector("tlv", "unsigned 8, 16, 32 and 64-bit", container(None, 0x16, uint(None, 42), uint(None, 70000), uint(None, 0x1_0000_0000), uint(None, 300, size=2)),
           "[42u,70000u,4294967296u,300u]")
    vector("tlv", "signed and booleans, null", container(None, 0x15, sint(("ctx", 0), -17, 1), sint(("ctx", 1), -40000, 4), el(("ctx", 2), 0x09, b""), el(("ctx", 3), 0x08, b""), el(("ctx", 4), 0x14, b"")),
           "{0=-17,1=-40000,2=true,3=false,4=null}")
    vector("tlv", "strings and octets", container(None, 0x15, utf8(("ctx", 1), "Lampu teras"), octets(("ctx", 2), b"\xDE\xAD\xBE\xEF")),
           '{1="Lampu teras",2=h\'DEADBEEF\'}')
    vector("tlv", "floats", container(None, 0x16, el(None, 0x0A, struct.pack("<f", 1.5)), el(None, 0x0B, struct.pack("<d", -0.25))), "[1.5f,-0.25d]")
    vector("tlv", "nested list with profile tags", container(("common", 7), 0x17, uint(("implicit", 0x12345), 1), container(("fq", 0xFFF1, 0xDEED, 3), 0x15, uint(("ctx", 9), 9))),
           "common:7=(implicit:74565=1u,FFF1:DEED:3={9=9u})")
    vector("tlv", "empty structure", container(None, 0x15), "{}")
    vector("tlv", "two-byte string length", el(None, 0x0D, struct.pack("<H", 3) + b"abc"), '"abc"')
    vector("tlv", "truncated integer", bytes([0x05, 0x01]), "error")
    vector("tlv", "string past the end", bytes([0x0C, 0x05]) + b"ab", "error")
    vector("tlv", "tagged member in an array", container(None, 0x16, uint(("ctx", 0), 1)), "error")
    vector("tlv", "anonymous member in a structure", container(None, 0x15, uint(None, 1)), "error")
    vector("tlv", "reserved element type", bytes([0x19]), "error")
    vector("tlv", "end of container at top level", bytes([0x18]), "error")
    vector("tlv", "missing end of container", bytes([0x15, 0x24, 0x00, 0x01]), "error")
    vector("tlv", "trailing bytes", uint(None, 1) + b"\x00", "error")
    vector("tlv", "invalid UTF-8", bytes([0x0C, 0x02, 0xC3, 0x28]), "error")

    # ---- onboarding payloads
    alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-."

    def base38(data):
        text = ""
        for i in range(0, len(data), 3):
            chunk = data[i:i + 3]
            v = int.from_bytes(chunk, "little")
            for _ in range({3: 5, 2: 4, 1: 2}[len(chunk)]):
                text += alphabet[v % 38]
                v //= 38
        return text

    def qr(vid, pid, flow, disc_caps, discriminator, passcode):
        bits, at = 0, 0
        for value, count in ((0, 3), (vid, 16), (pid, 16), (flow, 2), (disc_caps, 8), (discriminator, 12), (passcode, 27), (0, 4)):
            bits |= value << at
            at += count
        return "MT:" + base38(bits.to_bytes(11, "little"))

    def qr_desc(vid, pid, flow, caps, discriminator, passcode):
        return f"vid={vid:04X} pid={pid:04X} flow={flow} disc={caps} discriminator={discriminator} passcode={passcode}"

    example = qr(0xFFF1, 0x8000, 0, 2, 3840, 20202021)
    assert example == "MT:Y.K9042C00KA0648G00", example   # the CHIP SDK's published example
    vector("qr", "SDK example device", example, qr_desc(0xFFF1, 0x8000, 0, 2, 3840, 20202021))
    vector("qr", "on-network, user intent", qr(0x1234, 0x0042, 1, 4, 0x0ABC, 12345679), qr_desc(0x1234, 0x0042, 1, 4, 0x0ABC, 12345679))
    vector("qr", "custom flow, every capability", qr(0xFFF4, 0xFFFF, 2, 0x0F, 0xFFF, 99999998), qr_desc(0xFFF4, 0xFFFF, 2, 0x0F, 0xFFF, 99999998))
    vector("qr", "wrong prefix", "XX:Y.K9042C00KA0648G00", "error")
    vector("qr", "character outside base38", "MT:Y.K9042C00KA0648G0!", "error")
    vector("qr", "trivial passcode 11111111", qr(0xFFF1, 0x8000, 0, 2, 3840, 11111111), "error")
    vector("qr", "truncated", "MT:Y.K9042C0", "error")

    D5 = [[0, 1, 2, 3, 4, 5, 6, 7, 8, 9], [1, 2, 3, 4, 0, 6, 7, 8, 9, 5], [2, 3, 4, 0, 1, 7, 8, 9, 5, 6], [3, 4, 0, 1, 2, 8, 9, 5, 6, 7], [4, 0, 1, 2, 3, 9, 5, 6, 7, 8],
          [5, 9, 8, 7, 6, 0, 4, 3, 2, 1], [6, 5, 9, 8, 7, 1, 0, 4, 3, 2], [7, 6, 5, 9, 8, 2, 1, 0, 4, 3], [8, 7, 6, 5, 9, 3, 2, 1, 0, 4], [9, 8, 7, 6, 5, 4, 3, 2, 1, 0]]
    P8 = [[0, 1, 2, 3, 4, 5, 6, 7, 8, 9], [1, 5, 7, 6, 2, 8, 3, 0, 9, 4], [5, 8, 0, 3, 7, 9, 6, 1, 4, 2], [8, 9, 1, 6, 0, 4, 3, 5, 2, 7],
          [9, 4, 5, 3, 1, 2, 6, 8, 7, 0], [4, 2, 8, 6, 5, 7, 3, 9, 0, 1], [2, 7, 9, 3, 8, 0, 6, 4, 1, 5], [7, 0, 4, 6, 9, 1, 3, 2, 5, 8]]
    INV = [0, 4, 3, 2, 1, 5, 6, 7, 8, 9]

    def verhoeff(digits):
        c = 0
        for i, ch in enumerate(reversed(digits)):
            c = D5[c][P8[(i + 1) % 8][int(ch)]]
        return str(INV[c])

    def manual(vid, pid, flow, discriminator, passcode):
        vp = flow != 0
        short = discriminator >> 8
        digits = f"{(int(vp) << 2) | (short >> 2)}{((short & 3) << 14) | (passcode & 0x3FFF):05d}{passcode >> 14:04d}"
        if vp:
            digits += f"{vid:05d}{pid:05d}"
        return digits + verhoeff(digits)

    m = manual(0xFFF1, 0x8000, 0, 3840, 20202021)
    assert m == "34970112332", m   # the SDK's published manual code
    vector("manual", "SDK example (11 digits)", m, qr_desc(0, 0, 0, 0, 3840, 20202021))
    m21 = manual(0xFFF1, 0x8001, 1, 0x0A00, 87654320)
    vector("manual", "with vendor and product (21 digits)", m21, qr_desc(0xFFF1, 0x8001, 1, 0, 0x0A00, 87654320))
    vector("manual", "dashes are ignored", m[:4] + "-" + m[4:7] + "-" + m[7:], qr_desc(0, 0, 0, 0, 3840, 20202021))
    vector("manual", "wrong check digit", m[:-1] + str((int(m[-1]) + 1) % 10), "error")
    vector("manual", "wrong length", m[:-2], "error")
    vector("manual", "21 digits without the vendor flag", m[:-1] + "0000000000" + verhoeff(m[:-1] + "0000000000"), "error")

    # ---- messages (unsecured)
    def message(session=0, counter=1, src=None, dst=None, group=None, exchange=1, initiator=True, reliable=True, ack=None, protocol=1, vendor=0, opcode=2, payload=b"", flags_extra=0, security=0):
        flags = (0x04 if src is not None else 0) | (0x01 if dst is not None else 0x02 if group is not None else 0) | flags_extra
        h = bytes([flags]) + struct.pack("<H", session) + bytes([security]) + struct.pack("<I", counter)
        if src is not None:
            h += struct.pack("<Q", src)
        if dst is not None:
            h += struct.pack("<Q", dst)
        elif group is not None:
            h += struct.pack("<H", group)
        ex = (0x01 if initiator else 0) | (0x02 if ack is not None else 0) | (0x04 if reliable else 0) | (0x10 if vendor else 0)
        p = bytes([ex, opcode]) + struct.pack("<H", exchange)
        if vendor:
            p += struct.pack("<H", vendor)
        p += struct.pack("<H", protocol)
        if ack is not None:
            p += struct.pack("<I", ack)
        return h + p + payload

    def mdesc(session=0, counter=1, src=None, dst=None, group=None, exchange=1, initiator=True, reliable=True, ack=None, protocol=1, vendor=0, opcode=2, payload=b""):
        flags = ("I" if initiator else "") + ("R" if reliable else "") + ("A" if ack is not None else "")
        return (f"session={session} counter={counter} src={src:016X} " if src is not None else f"session={session} counter={counter} src=- ") + \
            (f"dst={dst:016X} " if dst is not None else f"dst=g{group:04X} " if group is not None else "dst=- ") + \
            f"exchange={exchange} {flags} ack={ack if ack is not None else '-'} protocol={vendor:04X}:{protocol:04X} opcode=0x{opcode:02X} payload={payload.hex().upper()}"

    pbkdf = bytes.fromhex("15300120") + bytes(32) + bytes.fromhex("25020100240300280418")
    cases = [
        ("PBKDFParamRequest from an ephemeral node", dict(src=0xFFFFFFFE12345678, exchange=0x1234, protocol=0, opcode=0x20, payload=pbkdf)),
        ("response with acknowledgement and destination", dict(counter=7, dst=0xFFFFFFFE12345678, exchange=0x1234, initiator=False, ack=0x01020304, protocol=0, opcode=0x21)),
        ("standalone acknowledgement", dict(counter=9, exchange=77, initiator=False, reliable=False, ack=8, protocol=0, opcode=0x10)),
        ("group destination and vendor protocol", dict(counter=0xFFFFFFFF, src=1, group=0x0101, exchange=2, vendor=0xFFF1, protocol=0x0042, opcode=0x99, payload=b"\x01")),
    ]
    for name, kw in cases:
        vector("message", name, message(**kw), mdesc(**kw))
    vector("message", "message version 1", message(flags_extra=0x10), "error")
    vector("message", "reserved destination size", message(flags_extra=0x03), "error")
    vector("message", "privacy flag", message(security=0x80), "error")
    vector("message", "truncated source node id", message(src=1)[:12], "error")
    vector("message", "truncated protocol header", message()[:11], "error")

    # ---- compressed fabric id (the spec's example)
    def hkdf(ikm, salt, info, length):
        prk = hmac.new(salt if salt else b"\x00" * 32, ikm, hashlib.sha256).digest()
        okm, t, i = b"", b"", 1
        while len(okm) < length:
            t = hmac.new(prk, t + info + bytes([i]), hashlib.sha256).digest()
            okm += t
            i += 1
        return okm[:length]

    root = bytes.fromhex("044A9F42B1CA4840D37292BBC7F6A7E11E22200C976FC900DBC98A7A383A641CB8254A2E56D4E295A847943B4E3897C4A773E930277B4D9FBEDE8A052686BFACFA")
    cfid = hkdf(root[1:], (0x2906C908D115D362).to_bytes(8, "big"), b"CompressedFabric", 8)
    assert cfid.hex().upper() == "87E1B004E235A130"
    vector("cfid", "core spec example", root, cfid.hex().upper(), fabricId="2906C908D115D362")

    # ---- SPAKE2+ (P-256) with fixed scalars
    P = 0xFFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF
    A = P - 3
    B = 0x5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B
    N = 0xFFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551
    G = (0x6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296, 0x4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5)

    def add(p1, p2):
        if p1 is None:
            return p2
        if p2 is None:
            return p1
        if p1[0] == p2[0] and (p1[1] + p2[1]) % P == 0:
            return None
        if p1 == p2:
            l = (3 * p1[0] * p1[0] + A) * pow(2 * p1[1], P - 2, P) % P
        else:
            l = (p2[1] - p1[1]) * pow(p2[0] - p1[0], P - 2, P) % P
        x = (l * l - p1[0] - p2[0]) % P
        return (x, (l * (p1[0] - x) - p1[1]) % P)

    def mul(k, pt):
        r = None
        while k:
            if k & 1:
                r = add(r, pt)
            pt = add(pt, pt)
            k >>= 1
        return r

    def neg(pt):
        return (pt[0], (-pt[1]) % P)

    def decompress(b):
        x = int.from_bytes(b[1:], "big")
        y = pow((x * x * x + A * x + B) % P, (P + 1) // 4, P)
        if y % 2 != b[0] % 2:
            y = P - y
        return (x, y)

    def enc(pt):
        return b"\x04" + pt[0].to_bytes(32, "big") + pt[1].to_bytes(32, "big")

    M = decompress(bytes.fromhex("02886E2F97ACE46E55BA9DD7242579F2993B64E16EF3DCAB95AFD497333D8FA12F"))
    Np = decompress(bytes.fromhex("03D8BBD6C639C62937B04D997F38C3770719C629D7014D49A24B4F98BAA1292B49"))

    def spake(passcode, salt, iterations, context, x, y):
        ws = hashlib.pbkdf2_hmac("sha256", struct.pack("<I", passcode), salt, iterations, 80)
        w0 = int.from_bytes(ws[:40], "big") % N
        w1 = int.from_bytes(ws[40:], "big") % N
        L = mul(w1, G)
        X = add(mul(x, G), mul(w0, M))
        Y = add(mul(y, G), mul(w0, Np))
        Zp = mul(x, add(Y, neg(mul(w0, Np))))
        Vp = mul(w1, add(Y, neg(mul(w0, Np))))
        Zv = mul(y, add(X, neg(mul(w0, M))))
        Vv = mul(y, L)
        assert Zp == Zv and Vp == Vv
        tt = b""
        for item in (context, b"", b"", enc(M), enc(Np), enc(X), enc(Y), enc(Zp), enc(Vp), w0.to_bytes(32, "big")):
            tt += struct.pack("<Q", len(item)) + item
        k = hashlib.sha256(tt).digest()
        ka, ke = k[:16], k[16:]
        kc = hkdf(ka, b"", b"ConfirmationKeys", 32)
        ca = hmac.new(kc[:16], enc(Y), hashlib.sha256).digest()
        cb = hmac.new(kc[16:], enc(X), hashlib.sha256).digest()
        keys = hkdf(ke, b"", b"SessionKeys", 48)
        return (f"w0={w0:064X} L={enc(L).hex().upper()} X={enc(X).hex().upper()} Y={enc(Y).hex().upper()} cA={ca.hex().upper()} cB={cb.hex().upper()} "
                f"Ke={ke.hex().upper()} I2R={keys[:16].hex().upper()} R2I={keys[16:32].hex().upper()} challenge={keys[32:].hex().upper()}")

    context = hashlib.sha256(b"CHIP PAKE V1 Commissioning" + b"request" + b"response").digest()
    salt = bytes(range(0x10, 0x30))
    for name, passcode, iterations, x, y in (("SDK passcode, 1000 iterations", 20202021, 1000, 0x1234567890ABCDEF << 64, 0xFEDCBA0987654321 << 32),
                                              ("another passcode, 2000 iterations", 12345679, 2000, N - 5, 0x42)):
        vector("spake2p", name, context, spake(passcode, salt, iterations, context, x, y),
               passcode=passcode, salt=salt.hex().upper(), iterations=iterations, x=f"{x:064X}", y=f"{y:064X}")
    return out


def main():
    files = {
        "crc.json": crc_vectors(),
        "cobs.json": cobs_vectors(),
        "slip.json": slip_vectors(),
        "modbus.json": modbus_vectors(),
        "coap.json": coap_vectors(),
        "mavlink_messages.json": mavlink_message_table(),
        "mavlink.json": mavlink_frames(),
        "lorawan.json": lorawan_vectors(),
        "dlms.json": dlms_vectors(),
        "mbus.json": mbus_vectors(),
        "canopen.json": canopen_vectors(),
        "j1939.json": j1939_vectors(),
        "iec104.json": iec104_vectors(),
        "ntp.json": ntp_vectors(),
        "ndef.json": ndef_vectors(),
        "lwm2m.json": lwm2m_vectors(),
        "ocpp.json": ocpp_vectors(),
        "knx.json": knx_vectors(),
        "bacnet.json": bacnet_vectors(),
        "enip.json": enip_vectors(),
        "ethercat.json": ethercat_vectors(),
        "firmware.json": firmware_vectors(),
        "matter.json": matter_vectors(),
    }
    for name, data in files.items():
        with open(os.path.join(HERE, name), "w", encoding="utf-8", newline="\n") as f:
            json.dump(data, f, indent=2)
            f.write("\n")
        print(f"wrote {name}: {len(data)} vectors")


if __name__ == "__main__":
    main()
