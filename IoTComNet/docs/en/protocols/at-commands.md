---
title: AT commands
translation-status: synced
---

# AT commands (cellular and GNSS modules)

**Summary.** NB-IoT, LTE-M, LTE Cat-1 and GNSS modules are driven over a serial port with AT commands
(3GPP TS 27.007 and 27.005, ITU-T V.250). A command ends with `OK`, `ERROR`, `+CME ERROR` or `+CMS ERROR`; the
module also sends **unsolicited result codes** (URCs) at any time, even in the middle of a response. IoTCom.Net
provides:

- `AtParser`, a sans-I/O parser that strips the echo, collects information lines, recognises every final result and
  the SMS prompt, and separates URCs;
- `AtModem`, a client with per-command timeouts, a URC stream, SMS in text mode, identity, signal and registration
  helpers, and a read-only mode;
- `AtModemSimulator`, an LTE-M module with a SIM PIN, network registration (+CEREG URCs), drifting signal, and SMS.

## When to use it

- Bringing up a cellular module on a board or a USB dongle, and scripting its configuration.
- Monitoring signal and registration in the field.
- Sending and receiving SMS for alarms and remote commands.

## Installation

```bash
dotnet add package IoTCom.Net.Protocols.AtCommand --prerelease     # also part of the IoTCom.Net meta-package
```

## Quickstart

```csharp
using IoTCom.Net.Protocols.AtCommand;
using IoTCom.Net.Transport.Serial;

await using var modem = AtModem.Create(o => o.UseSerial("COM7", 115200));
await modem.ConnectAsync();                                 // ATE0, AT+CMEE=2
var info = await modem.GetInfoAsync();
Console.WriteLine($"{info.Model} {info.Registration} {info.Operator} {info.AccessTechnology} {info.Signal}");

modem.UrcReceived += (_, urc) => Console.WriteLine($"URC {urc.Line}");   // +CEREG: 1 · +CMTI: "ME",3 · RING
var r = await modem.SendAsync("AT+QCFG=\"nwscanmode\"");               // vendor commands pass through
```

`SendAsync` returns an `AtResponse` (lines, result, error code or text) and never throws on `ERROR`;
`SendCheckedAsync` throws `DeviceException` with the decoded error. `Values("+CSQ")` splits a line into values,
respecting quotes.

## Safety

With `ReadOnly = true`, commands that change the module or cost money (AT+CFUN, dialling, SMS, SIM locks, operator
selection, saving settings) throw `ReadOnlyModeException`. The CLI requires `--allow-write` for them.

## Tools

```bash
iotcom at info --sim                        # simulated module
iotcom at send --serial COM7 ATI AT+CSQ "AT+COPS?"
iotcom at sms --serial COM7 +6281234567890 "pump on" --allow-write
iotcom at simulate --port 2000              # a module on TCP for other tools
```

## Testing

Parser tests cover echo, responses split across reads, URCs in the middle of a response, the command's own prefix
not being taken for a URC, every final result code and the prompt. Endpoint tests run against the simulator:
identity, registration URC, signal, SMS out and in (+CMTI, AT+CMGR), the SIM PIN, and read-only mode.

## Limitations

Not yet included: PDU-mode SMS, multiplexing (CMUX), data mode (PPP), and vendor socket stacks (+QIOPEN, +USOCR)
beyond passing the commands through.

## Learn more

Notebook `notebooks/devices/11-at-astm.en.ipynb` · `iotcom at --help`
