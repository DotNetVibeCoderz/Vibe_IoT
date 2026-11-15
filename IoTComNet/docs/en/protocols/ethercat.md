---
title: EtherCAT
translation-status: synced
---

# EtherCAT master

**Summary.** EtherCAT is Ethernet for fieldbus I/O: the master sends ordinary frames (EtherType 0x88A4) and every slave
reads and writes its part while the frame passes through, in hardware, then forwards it; the last slave sends it back.
One frame services the whole line, and each datagram's working counter tells the master how many slaves served it.
`IoTCom.Net.Protocols.EtherCat` provides:

- Codecs (sans-I/O): `EcatFrame` (Ethernet + EtherCAT header, datagrams with ADP/ADO or logical addresses, the more and
  circulating flags, working counters, frame lane), `EcatMailbox`, `CoeSdo` (expedited and normal transfers, aborts),
  `EcatSii` (identity, mailbox configuration, strings, general, SyncManager and PDO categories), `EcatAlStatus`.
- `EthercatMaster`: line scan (broadcast count, station addresses 0x1001…, SII identity and process-data sizes), the AL
  state machine INIT → PRE-OP → SAFE-OP → OP with mailbox SyncManagers, process-data SyncManagers and FMMUs mapped into
  one logical image, the cyclic exchange (`ExchangeAsync`, `RunAsync`) with working-counter checks and statistics, CoE
  object access (`SdoReadAsync`/`SdoWriteAsync`), register reads and error acknowledgement. **Read-only by default**:
  without `AllowWrites()` the line stops at SAFE-OP (inputs live, outputs safe) and the exchange is a logical read.
- Links: `RawEthernetLink` — AF_PACKET sockets on Linux, Npcap on Windows, libpcap on macOS — and
  `VirtualEthercatSegment`, a simulated line of ESCs (register memory, AL state machine, SII EEPROM, SyncManagers,
  FMMUs, CoE mailbox) with a cable that can be cut after any slave and a loss rate.
- `EthercatRackSimulator`: a conveyor station with a coupler, an 8-channel digital input, an 8-channel digital output
  and a 4-channel analog input terminal.

Following the design (curation tier 2, Rust candidate `ethercrab`), the master is managed C# with a Rust codec twin, as
for EtherNet/IP: wrapping ethercrab would need an async executor behind the FFI and could not drive the in-memory
segment through the same codec. The Rust crate `iotcom-ethercat` is fuzzed and shares 37 vectors in
`/conformance/ethercat.json` with the C# suite.

## When to use it

- Reading and driving EtherCAT I/O from .NET at a few hundred hertz: test benches, end-of-line testers, lab rigs,
  data acquisition, commissioning tools.
- Inspecting a line: who is on it, in which state, with which identity and objects.
- Teaching and testing without hardware (`VirtualEthercatSegment`, `iotcom ethercat … --sim`).

It is **not** a replacement for a real-time EtherCAT master in a motion controller: cycles run on the .NET scheduler and
the operating system's network stack, so expect jitter in the 0.1–1 ms range (more on Windows, whose timer resolution
is coarse). Distributed clocks are not configured.

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.EtherCat --prerelease    # also part of the IoTCom.Net meta-package
```

Raw Ethernet needs privileges: on Linux `sudo setcap cap_net_raw+ep <executable>` (or root); on Windows install
[Npcap](https://npcap.com) with "WinPcap API-compatible mode" and run as administrator; on macOS run as root or give
access to `/dev/bpf*`. Use a network port dedicated to EtherCAT.

## Quickstart

```csharp
using IoTCom.Net.Protocols.EtherCat;

await using var master = EthercatMaster.Create(o => o.UseInterface("eth1"));   // read-only
foreach (var slave in await master.ScanAsync()) Console.WriteLine(slave);
await master.RequestStateAsync(EcatState.SafeOp);                             // inputs live, outputs safe
await master.ExchangeAsync();
Console.WriteLine(Convert.ToHexString(master.InputsOf(master.Slaves[1])));
var name = await master.SdoReadAsync(master.Slaves[3], 0x1008, 0);            // CoE device name
```

Driving outputs, after opting in:

```csharp
await using var master = EthercatMaster.Create(o => o.UseInterface("eth1").AllowWrites());
await master.ScanAsync();
await master.RequestStateAsync(EcatState.Op);
master.SetOutputBit(master.Slaves[2], 0, true);
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
await master.RunAsync(TimeSpan.FromMilliseconds(4), stop.Token);
Console.WriteLine(master.Statistics);   // cycles, short working counters, lost frames, round trips
```

Hosting: `services.AddIoTCom(b => b.AddEthercatMaster("line", o => o.UseInterface("eth1")))` opens the link with the
host and brings the line back to PRE-OP when it stops.

## Safety

Outputs move machines. The master refuses OP, `SetOutputs`, `SetOutputBit` and SDO downloads until `AllowWrites()`;
the CLI needs `--allow-write` and asks before driving a real line, and the Gallery has a write lock. Never rely on this
library for safety functions: emergency stops, guards and light curtains belong in safety-rated hardware (FSoE/Safety
over EtherCAT is not implemented). Closing the master (or stopping the host) brings the line back to PRE-OP, where the
slaves hold their outputs in a safe state; a slave whose SyncManager watchdog expires does the same on its own.

## Tools

```bash
iotcom ethercat interfaces                                  # AF_PACKET or Npcap/libpcap devices
iotcom ethercat scan --if eth1                              # or --sim
iotcom ethercat watch --sim                                 # SAFE-OP, live inputs and working counters
iotcom ethercat sdo 3 1008:00 --sim                         # CoE upload
iotcom ethercat sdo 3 8000:15 --write 0200 --sim --allow-write
iotcom ethercat set 2 0 on --sim --allow-write              # OP for a few seconds with one output on
iotcom ethercat demo [--allow-write]
iotcom ethercat decode "FFFFFFFFFFFF010101010101 88A4 0D10 0701 00000000 0100 0000 00 0400"
```

Captures write EtherCAT frames as Ethernet (LINKTYPE_ETHERNET), so Wireshark's EtherCAT dissector decodes them; the
VS Code extension decodes `ethercat`. Gallery: *I/O rack over EtherCAT*.

## Testing

Codec tests cover padding and the more flag, malformed frames, mailboxes and SDOs, and SII images; the 37 shared vectors
cover datagram payloads (broadcast, positional, configured, logical, circulating), Ethernet frames and the returned flag,
mailboxes, expedited and normal SDOs with aborts, SII images, and malformed input of each kind; the Rust twin is fuzzed
(`ethercat`, 3 M local runs). Master tests run against the simulated rack: the scan, SAFE-OP read-only with inputs, the
refusals, OP driving the conveyor and the emergency stop, CoE reads, writes and aborts, a cut cable in the working
counter, lost frames with retries, AL errors with status codes and acknowledgement, the traffic tap, and hosting.

## Limitations

Distributed clocks, EoE/FoE/SoE, segmented SDO transfers, SDO information, PDO remapping from CoE (the SII PDOs are
used as they are), redundancy (cable ring), hot connect, aliases and process images larger than one frame
(about 1.4 KB) are not implemented. Process data is byte-aligned per slave.

## Learn more

Notebook `notebooks/industrial/29-ethercat.en.ipynb` · sample `samples/console/EthercatRack` ·
[EtherNet/IP](ethernetip.md) · [CANopen](canopen.md) (CoE uses the same object dictionary model)
