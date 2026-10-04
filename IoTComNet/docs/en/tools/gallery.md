---
title: IoTCom.Net Gallery
translation-status: synced
---

# IoTCom.Net Gallery

A desktop app (Avalonia — Windows, Linux, macOS) for learning by running: pick a use case, start it, read its code,
and watch its bytes. Every demo talks to in-process simulators, so nothing needs hardware.

```bash
dotnet run --project gallery/IoTCom.Net.Gallery
```

![Gallery — smart factory](../../images/gallery-modbus.png)

## Each demo has four tabs

| Tab | Shows |
|---|---|
| **Run** | the interactive panel |
| **Code** | the demo's source — the exact file compiled into the app, syntax highlighted |
| **Docs** | a short explanation in English or Bahasa Indonesia, with the matching docs page |
| **Traffic** | the Protocol Inspector: every frame as a coloured *frame lane*, plus a hex dump of the selected frame |

The status bar shows the run lamp, the demo status and the latest frame on the wire.

![Traffic tab](../../images/gallery-traffic.png)

## Demos

| Category | Demo | Protocols |
|---|---|---|
| Industrial | Smart factory PLC — read a virtual PLC, start the motor, move the setpoint; switch between the C# and **Rust** engines | Modbus TCP |
| Navigation & marine | GNSS vehicle tracker — live track, speed, satellites' signal strength | NMEA 0183 |
| Smart building & stage | Stage lighting over Art-Net — faders, master, chase; fixtures show what the receiver decoded | Art-Net 4, DMX512 |
| Messaging | MQTT publish & subscribe — embedded broker, SenML sensor, wildcard subscriptions | MQTT 5, SenML |
| Protocol workbench | Frame & checksum workbench — decode Modbus frames field by field, 23 CRCs, SLIP/COBS/HDLC live | Modbus, CRC, framing |

![NMEA](../../images/gallery-nmea.png)
![Lighting](../../images/gallery-lighting.png)
![Workbench](../../images/gallery-workbench.png)

## Language and theme

The rail's **BAHASA / ENGLISH** button switches every label, demo text and status line at runtime; **DARK / LIGHT**
switches the theme.

![Dark, Bahasa Indonesia](../../images/gallery-dark-id.png)

## Adding a demo

Derive from `GalleryDemo` (logic in `XxxDemo.cs`, view in `XxxDemo.View.cs`) and add it to the list in
`MainViewModel`. Implement `OnStartAsync`/`OnStopAsync`, attach `Tap` to your endpoints, and provide the `Title`,
`Summary` and `Docs` texts in both languages.

## Screenshots

`gallery/IoTCom.Net.Gallery.Screenshots` renders the real window off-screen with Avalonia.Headless and Skia:

```bash
dotnet run --project gallery/IoTCom.Net.Gallery.Screenshots -- docs/images
```
