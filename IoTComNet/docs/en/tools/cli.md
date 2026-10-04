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
| `iotcom nmea listen` | live GNSS fix panel (`--raw` prints sentences) |
| `iotcom nmea simulate` | a GPS over NMEA-over-TCP |
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
