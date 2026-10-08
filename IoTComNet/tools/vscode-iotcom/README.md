# IoTCom.Net Tools for VS Code

Decode industrial, automotive and IoT frames field by field, watch live traffic, and save it for Wireshark, without
leaving the editor. Everything runs in the `iotcom` CLI over JSON-RPC, so the extension and the library decode
exactly the same way.

Built by Gravicode Studios, led by Kang Fadhil · [Bahasa Indonesia](README.id.md)

![Frame viewer](https://raw.githubusercontent.com/DotNetVibeCoderz/Vibe_IoT/main/IoTComNet/docs/images/vscode-frame-viewer.png)

## Features

- **Frame viewer:** paste a frame or select bytes in any file, then run **IoTCom: Decode selected bytes**. The frame
  appears as a *frame lane* with every field coloured, named and decoded. Hover a field in the table to highlight its
  bytes. Decoders: Modbus TCP/RTU/ASCII, CAN (candump notation), UDS/OBD-II, CoAP and MAVLink.
- **Traffic monitor:** watch a live source as a scrolling list of decoded frames. Filter it, click a frame to expand
  its lane, and **Save .pcapng** to open the capture in Wireshark. Sources:
  - the built-in simulators: Modbus PLC, CAN ECU, CoAP greenhouse and MAVLink drone;
  - a CAN interface (`socketcan:can0`, `slcan:COM5`);
  - a MAVLink UDP port.
- **Protocols view:** each protocol links to its NuGet package (click to copy `dotnet add package …`), its
  documentation in English or Bahasa Indonesia, its notebook, its sample, and its decoders.
- **Devices view:** serial ports, SocketCAN interfaces and simulators. Click one to start monitoring it.
- **Snippets:** type `iotcom-` in a C# file for Modbus, CAN, UDS, CoAP, MAVLink, MQTT and pcapng capture snippets.
- **English and Bahasa Indonesia:** the extension follows the VS Code display language.

## Requirements

The `iotcom` CLI (.NET 10):

```bash
dotnet tool install -g IoTCom.Net.Cli --prerelease
```

In a clone of the IoTCom.Net repository, the extension uses the CLI built in the workspace
(`dotnet build tools/iotcom-cli`). Otherwise, set **iotcom.cliPath**, for example `dotnet /path/to/iotcom.dll`.

## Settings

| Setting | Default | |
|---|---|---|
| `iotcom.cliPath` | (empty) | command that runs the CLI; empty means the workspace build, then the global tool |
| `iotcom.docsLanguage` | `auto` | `en`, `id`, or follow the VS Code display language |

## Development

```bash
npm install
npm test            # compiles, then tests the RPC client end to end against the real CLI
npm run package     # builds iotcom-net-tools.vsix
```

Press F5 in VS Code with this folder open to start an Extension Development Host.
