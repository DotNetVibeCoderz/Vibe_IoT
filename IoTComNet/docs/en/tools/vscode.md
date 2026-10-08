---
title: VS Code extension
translation-status: synced
---

# VS Code extension: IoTCom.Net Tools

Decode frames, watch live traffic and save it for Wireshark without leaving the editor. The extension talks to the
`iotcom` CLI over JSON-RPC, so it decodes exactly as the library does and never duplicates protocol logic.

![Frame viewer](../../images/vscode-frame-viewer.png)

## Install

The extension is built in the repository (`tools/vscode-iotcom`) and published by CI as `iotcom-net-tools.vsix`:

```bash
cd tools/vscode-iotcom && npm install && npm run package
code --install-extension iotcom-net-tools.vsix
```

It needs the CLI: `dotnet tool install -g IoTCom.Net.Cli --prerelease`. In a clone of this repository it uses the
most recently built `tools/iotcom-cli` instead. Set **iotcom.cliPath** to use another command, for example
`dotnet /path/to/iotcom.dll`.

## Features

| Feature | How to use it |
|---|---|
| **Frame viewer** | select bytes in any file (hex, `0x`-prefixed or a candump line), then right-click **IoTCom: Decode selected bytes**; or run **IoTCom: Decode a frame…** and paste. Fields appear as a colored frame lane with a field table; hover a row to highlight its bytes. |
| **Traffic monitor** | **IoTCom: Start a traffic monitor…**, or click a simulator or CAN interface in the *Devices & simulators* view. Filter the frames, click one to expand its lane, and **Save .pcapng** for Wireshark. |
| **Protocols view** | every protocol with its NuGet package (click to copy `dotnet add package …`), documentation in English or Indonesian, notebook, sample and decoders |
| **Snippets** | type `iotcom-` in a C# file: Modbus client and simulator, CAN, UDS, CoAP, MAVLink, MQTT, LoRaWAN network server, pcapng capture |
| **Languages** | English and Bahasa Indonesia, following the VS Code display language |

Decoders: `modbus-tcp`, `modbus-rtu`, `modbus-ascii`, `can`, `uds`, `coap`, `mavlink`, `lorawan`, `semtech-udp`.
Monitor sources: `sim:modbus`, `sim:can`, `sim:coap`, `sim:mavlink`, `sim:lorawan` (built-in simulators), `can:<uri>`
(for example `can:socketcan:can0`, listen only), `mavlink:udp:<port>` and `lorawan:udp:<port>` (a light network
server that shows what your gateways forward).

![Traffic monitor](../../images/vscode-monitor.png)

## The JSON-RPC interface

Other editors and tools can use the same interface. `iotcom rpc` reads one JSON-RPC 2.0 request per line on stdin and
writes responses and notifications to stdout:

| Method | Parameters | Result |
|---|---|---|
| `initialize` | — | version, credit, protocols (package, docs, notebook, sample, decoders), monitor sources |
| `decode` | `protocol`, `hex`, optional `direction` (`request`/`response`) | `summary`, `hex`, `fields` [{name, offset, length, kind, value}] |
| `devices` | — | serial ports, SocketCAN interfaces, simulators |
| `monitor.start` / `monitor.stop` | `source` / `id` | monitor id / frame count |
| `monitor.save` | `id`, `path` | pcapng written |
| `shutdown` | — | the process exits |

While a monitor runs, the CLI sends `frame` notifications: monitor, time, protocol, direction, hex, summary and
fields.

```bash
echo '{"jsonrpc":"2.0","id":1,"method":"decode","params":{"protocol":"modbus-tcp","hex":"00010000000601030000000A"}}' | iotcom rpc
```

## Development

`npm test` compiles the extension and tests its RPC client end to end against the real CLI. `node test/preview.mjs`
renders the webviews outside VS Code with real data, which is how the screenshots on this page are made. Press F5 in
VS Code with `tools/vscode-iotcom` open to start an Extension Development Host.
