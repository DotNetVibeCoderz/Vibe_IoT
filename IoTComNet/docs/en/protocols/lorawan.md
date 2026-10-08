---
title: LoRaWAN
translation-status: synced
---

# LoRaWAN

**Summary.** LoRaWAN carries small, infrequent messages from battery-powered sensors over kilometres of unlicensed
spectrum. Devices transmit LoRa frames, any gateway in range forwards them to a network server, and the network server
checks, deduplicates and decrypts them. IoTCom.Net implements LoRaWAN 1.0.x end to end:

- the **PHYPayload codec** with AES-CMAC MICs, payload encryption, OTAA joins and ABP;
- **MAC commands** and **regional parameters** (EU868, US915, AS923-2) with time on air;
- the **Semtech UDP packet-forwarder protocol**, on both the gateway and the server side;
- a **light network server**, a Class A **end-device MAC**, Cayenne LPP, and a simulator with gateways, sensors and a
  radio model.

![LoRaWAN network monitor](../../images/gallery-lorawan.png)

## When to use it

- Running a lab, a campus or a pilot with your own gateways, without operating ChirpStack or The Things Stack yet.
- Testing devices, decoders and dashboards against a network you control, including bad MICs, replays and lost
  uplinks.
- Driving a real network server (ChirpStack, TTS) with simulated gateways and devices for load and integration tests.
- Decoding frames captured from a gateway log or a packet forwarder.

For production fleets with roaming, multicast and Class B/C devices, use a full network server. The codec,
simulator and tools here still work alongside it.

## Roles

| Role | Type |
|---|---|
| Network server | `LoRaWanNetworkServer`: OTAA/ABP devices, `UplinkReceived`, `DeviceJoined`, `FrameRejected`, `EnqueueDownlink`, `EnqueueMacCommand` |
| Gateway (packet forwarder) | `SemtechPacketForwarder`: PUSH_DATA/PULL_DATA/TX_ACK, status reports, `TransmitRequested` for downlinks |
| End device | `LoRaWanEndDevice` (sans-I/O): `CreateJoinRequest`, `CreateUplink`, `HandleDownlink`, MAC answers |
| Codec | `LoRaWanPacket`, `LoRaWanJoinAccept`, `LoRaWanCrypto`, `LoRaWanMacCommands`, `SemtechPacket`, `LoRaWanAnatomy`, `CayenneLpp` |
| Simulator | `LoRaWanSimulator`: gateways and sensors (environment, soil, water meter, GPS tracker) with path loss and shadowing |

## Transports

Gateways talk to the server over UDP (the Semtech protocol, port 1700): `UseUdp(port)`, or `UseInMemory(network)`
on an `InMemoryDatagramNetwork` for tests. The LoRa radio itself belongs to the gateway; the simulator models it.

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.LoRaWan --prerelease     # also part of the IoTCom.Net meta-package
```

## Quickstart: a network server for your gateways

```csharp
using IoTCom.Net.Protocols.LoRaWan;

await using var ns = LoRaWanNetworkServer.Create(o => o.Region = LoRaRegion.AS923Group2);
ns.AddDevice(LoRaWanDeviceRegistration.Otaa(Eui64.Parse("70B3D57ED0000001"), LoRaWanKeys.Parse(appKeyHex), "node-1"));
ns.UplinkReceived += (_, up) =>
    Console.WriteLine($"{up.Device.Name} FCnt {up.FCnt} via {up.Gateways.Count} gateway(s): {Convert.ToHexString(up.Payload)}");
await ns.StartAsync();                    // UDP 1700

ns.EnqueueDownlink(Eui64.Parse("70B3D57ED0000001"), fport: 10, [0x00, 0x3C]);   // sent in RX1 after the next uplink
```

Point each gateway's packet forwarder at this machine: in `global_conf.json` (or `local_conf.json`), set
`server_address` to its IP and `serv_port_up` and `serv_port_down` to 1700.

## Quickstart: decode a frame

```csharp
var frame = LoRaWanPacket.Decode(Convert.FromHexString("40F17DBE4900020001954378762B11FF0D"));
var keys = LoRaWanSessionKeys.FromHex("44024241ED4CE9A68C6A8BC055233FD3", "EC925802AE430CA77FD3DD73CB2CC588");
Console.WriteLine($"{frame}  MIC ok: {frame.VerifyMic(keys.NwkSKey)}");      // Unconfirmed up DevAddr=49BE7DF1 FCnt=2 FPort=1 4 B
Console.WriteLine(Encoding.ASCII.GetString(frame.DecryptPayload(keys)));       // test
```

## What the network server does

| Step | Behaviour |
|---|---|
| Gateways | answers PUSH_DATA with PUSH_ACK and PULL_DATA with PULL_ACK, remembers each gateway's downlink address, keeps its status reports |
| Deduplication | copies of one uplink from several gateways are collected for `DeduplicationWindow` (200 ms); the event lists every gateway, best SNR first |
| Join | checks the DevEUI, the JoinEUI and the MIC, rejects a reused DevNonce, assigns a DevAddr (NwkID from the NetID), derives the session keys and answers after `JoinAcceptDelay` (5 s) |
| Uplink | finds the device by DevAddr and MIC, rebuilds the 32-bit FCnt, rejects replays, decrypts FRMPayload and parses MAC commands |
| Downlink | Class A in RX1 (same channel and data rate in EU868 and AS923; mapped in US915), `tmst` = uplink + RX1 delay; sends an ACK for confirmed uplinks, queued data (with FPending), and MAC answers |
| MAC commands | answers LinkCheckReq (margin from the SNR, gateway count) and DeviceTimeReq; records DevStatusAns (battery, margin) |

Rejected frames raise `FrameRejected` with a precise reason, for example: unknown device, MIC mismatch (wrong AppKey),
DevNonce already used, or FCnt not above the last one (replay).

## Simulating a deployment

```csharp
var options = LoRaWanSimulatorOptions.Demo(new IPEndPoint(IPAddress.Loopback, 1700));   // 2 gateways, 4 sensors
await using var sim = new LoRaWanSimulator(options);
foreach (var r in sim.Registrations) ns.AddDevice(r);
sim.RadioActivity += (_, e) => Console.WriteLine($"{(e.Uplink ? "▲" : "▼")} {e.Device} {e.DataRate} {e.Summary}");
await sim.StartAsync();
```

Each gateway is a real `SemtechPacketForwarder`, so the simulator works with any Semtech-compatible network server.
Each uplink's RSSI comes from a log-distance path-loss model (urban, 868–923 MHz) plus log-normal shadowing. A
gateway hears the uplink only when its SNR is above the demodulation floor for the spreading factor. Devices pick the
fastest data rate their link supports. Downlinks are transmitted at the `tmst` the server asks for.

## Regions and time on air

| Region | Uplink channels | RX2 | Notes |
|---|---|---|---|
| `EU868` | 868.1, 868.3, 868.5 MHz | 869.525 MHz, DR0 | 1 % duty cycle on most sub-bands |
| `US915` | sub-band 2 (903.9–905.3 MHz) | 923.3 MHz, DR8 | RX1 on 923.3 + 0.6 × (channel mod 8) MHz |
| `AS923Group2` | 921.4, 921.6 MHz | 921.4 MHz, DR2 | AS923-2: Indonesia, Vietnam |

`LoRaAirtime.Compute(bytes, sf, bw)` uses the Semtech formula. For example, 12 payload bytes take 61.7 ms at SF7 and
1.48 s at SF12; `iotcom lorawan airtime 12` prints the whole table.

## Tools

```bash
iotcom lorawan server --sim --region AS923                 # network server + simulated gateways and sensors
iotcom lorawan server --devices devices.json --pcap lora.pcapng --frames
iotcom lorawan simulate --server chirpstack.local:1700     # drive another network server
iotcom lorawan decode 40F17DBE4900020001954378762B11FF0D --nwkskey … --appskey …
iotcom lorawan airtime 12 --region AS923
dotnet run --project samples/console/LoRaWanGatewayMonitor
```

pcapng captures use LoRaTap encapsulation, so Wireshark decodes the LoRaWAN layer. The VS Code extension decodes
`lorawan` and `semtech-udp` frames and can monitor `sim:lorawan` or `lorawan:udp:<port>`.

## Testing and interoperability

- **Codec:** C# and the Rust crate `iotcom-lorawan` run the same vectors (`/conformance/lorawan.json`). A
  self-contained Python reference produces them; its AES and AES-CMAC are checked against FIPS-197 and RFC 4493, and
  its output against the frame published with the lora-packet library. The Rust codec is fuzzed
  (`cargo fuzz run lorawan`).
- **Semtech UDP:** round trips and the `txpk` example from Semtech's PROTOCOL.TXT, including the unpadded base64 that
  real packet forwarders send.
- **Network:** OTAA join and confirmed uplinks through two gateways (one event, two receptions), queued downlinks and
  MAC commands, rejected unknown devices, wrong AppKeys, MIC failures and replays, a device out of range, and RX1
  timing over real UDP.

## Security

The network server holds the root and session keys, so treat its host and the device file as secrets. Never log or
commit keys. The Semtech UDP protocol is unauthenticated and unencrypted between gateway and server: keep it on a
trusted network or a VPN. LoRaWAN payloads stay encrypted end to end with the AppSKey.

## Limitations

LoRaWAN 1.0.x only; LoRaWAN 1.1 (separate network keys, FOpts encryption) is decoded structurally but not processed.
Not yet included: Class B and C, ADR decisions on the server, CFList channel plans per device, multicast, FUOTA,
roaming and the Basics Station protocol.

## Learn more

Gallery demo *LoRaWAN network monitor* · notebook `notebooks/lpwan/09-lorawan.en.ipynb` · `iotcom lorawan --help` ·
[Traffic tap and pcapng](../concepts/traffic-tap.md)
