---
title: The iotcom CLI
translation-status: synced
---

# The `iotcom` CLI

```bash
dotnet tool install -g IoTCom.Net.Cli --prerelease
iotcom --help
```

![iotcom](../../images/cli.png)

## Commands

| Command | Does |
|---|---|
| `iotcom info` | version, credits, protocol list |
| `iotcom ports` | list serial ports |
| `iotcom crc <hex> [--algorithm name] [--all] [--text ...]` | compute CRCs (23 presets, aliases like `modbus`, `x25`, `mavlink`) |
| `iotcom frame encode|decode slip|cobs|hdlc <hex>` | framing codecs |
| `iotcom modbus read` | read coils, discrete inputs, input or holding registers (`--watch`, `--float`) |
| `iotcom modbus write` | write coils or registers — requires `--allow-write` and a confirmation |
| `iotcom modbus serve` | a Modbus slave; `--simulate` runs the virtual PLC; logs every request |
| `iotcom modbus decode <frame>` | field-by-field frame lane for TCP, RTU or ASCII frames |
| `iotcom mavlink listen` / `simulate` | MAVLink log or live rate/telemetry table (`--stats`); a simulated quadcopter over UDP |
| `iotcom mavlink cmd` / `params` / `decode` | arm, takeoff, land, rtl and parameter writes (`--allow-write`); frame lane of a frame |
| `iotcom sniff tcp` / `udp` | transparent relay to a device (Modbus/TCP, HL7, CoAP, MAVLink, raw), both directions decoded, `--pcap` for Wireshark, `--lanes` for frame lanes |
| `iotcom sniff can` | passive CAN capture to the console and pcapng (SocketCAN link type) |
| `iotcom nmea listen` | live GNSS fix panel (`--raw` prints sentences) |
| `iotcom nmea simulate` | a GPS over NMEA-over-TCP |
| `iotcom can list` | SocketCAN interfaces and serial ports (slcan adapters) |
| `iotcom can dump` / `send` | candump/cansend for any backend (`--can socketcan:can0`, `slcan:COM5`, `sim`) |
| `iotcom can simulate` | engine ECU simulator behind an emulated slcan adapter on TCP |
| `iotcom uds read` / `dtc` / `raw` | UDS identification, DTCs (`--clear --allow-write`), raw requests with the frame lane |
| `iotcom obd live` / `vin` / `dtc` | OBD-II live data (`--watch`), VIN, stored and pending DTCs |
| `iotcom coap get` / `put` / `observe` / `discover` / `ping` | CoAP client on `coap://host/path` URIs (`--accept senml`, `--frames`; writes need `--allow-write`) |
| `iotcom coap serve` | simulated greenhouse node on UDP 5683 |
| `iotcom hl7 listen` | MLLP receiver with auto-ACK and decoded observations (`--raw` prints segments) |
| `iotcom hl7 send` | send an ER7 file (or a sample ORU^R01) and print the ACK |
| `iotcom hl7 simulate` | a synthetic bedside monitor (`--scenario sepsis\|hypoxia\|hypertension\|stable`) |
| `iotcom dicom listen` | DICOM Storage SCP (`--output` saves .dcm + PNG preview) |
| `iotcom dicom send` | C-STORE a file or a synthetic study (`--synthetic ct\|mr\|xray --finding …`) |
| `iotcom dicom echo` | C-ECHO verification |
| `iotcom artnet send|poll|monitor` | send DMX, discover nodes, watch universes |
| `iotcom mqtt pub|sub|broker` | publish, subscribe, run a broker |

## Connection options (Modbus)

`--host`, `--port` (TCP) · `--serial COM3 --baud 19200 --parity even` (RTU) · `--rtu`, `--ascii` · `--unit` ·
`--timeout` · `--native` (use the Rust engine).

## Recipes

```bash
# A virtual PLC in one terminal…
iotcom modbus serve --port 1502 --simulate
# …and a live view in another
iotcom modbus read --port 1502 --table input --count 8 --float --watch 1000

# What is in this frame from a logic analyzer?
iotcom modbus decode "01 03 00 00 00 0A C5 CD" --mode rtu

# Which CRC does my device use?
iotcom crc "01 03 00 00 00 0A" --all

# Watch an Art-Net universe
iotcom artnet monitor --universe 0
```

Exit codes: `0` success, `1` error, `2` invalid frame / no frame, `3` write not allowed, `4` cancelled at confirmation.
Set `IOTCOM_FORCE_ANSI=1` to keep colours when output is redirected.
