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


def main():
    files = {
        "crc.json": crc_vectors(),
        "cobs.json": cobs_vectors(),
        "slip.json": slip_vectors(),
        "modbus.json": modbus_vectors(),
        "coap.json": coap_vectors(),
    }
    for name, data in files.items():
        with open(os.path.join(HERE, name), "w", encoding="utf-8", newline="\n") as f:
            json.dump(data, f, indent=2)
            f.write("\n")
        print(f"wrote {name}: {len(data)} vectors")


if __name__ == "__main__":
    main()
