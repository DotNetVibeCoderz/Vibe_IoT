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
    }
    for name, data in files.items():
        with open(os.path.join(HERE, name), "w", encoding="utf-8", newline="\n") as f:
            json.dump(data, f, indent=2)
            f.write("\n")
        print(f"wrote {name}: {len(data)} vectors")


if __name__ == "__main__":
    main()
