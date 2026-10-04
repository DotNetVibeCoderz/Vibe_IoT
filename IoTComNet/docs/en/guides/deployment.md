---
title: Deployment
translation-status: synced
---

# Deployment

## Edge worker

Start from the template:

```bash
dotnet new install IoTCom.Net.Templates
dotnet new iotcom-worker -n LineGateway
```

It already calls `AddSystemd()` and `AddWindowsService()`.

### Linux (systemd)

```bash
dotnet publish -c Release -r linux-arm64 --self-contained -o /opt/line-gateway
sudo useradd -r -G dialout linegw
```

```ini
# /etc/systemd/system/line-gateway.service
[Service]
Type=notify
ExecStart=/opt/line-gateway/LineGateway
User=linegw
Restart=always
# Ports below 1024 without root:
AmbientCapabilities=CAP_NET_BIND_SERVICE

[Install]
WantedBy=multi-user.target
```

### Windows Service

```powershell
dotnet publish -c Release -r win-x64 --self-contained -o C:\LineGateway
sc.exe create LineGateway binPath= "C:\LineGateway\LineGateway.exe" start= auto
```

### Docker

The template's `Dockerfile` builds a runtime image. Pass serial devices with `--device=/dev/ttyUSB0`, and use host
networking (`--network host`) for UDP broadcast/multicast protocols such as Art-Net and sACN.

## Trimming and NativeAOT

All library packages are marked `IsTrimmable` and `IsAotCompatible` and are analysed for both in the build. Use
source-generated JSON contexts with the MQTT JSON helpers. `NativeModbusClient` uses source-generated `LibraryImport`
and ships per-RID native binaries.

```bash
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
```

## Native libraries

When you publish for a RID, the matching `runtimes/{rid}/native/` file from `IoTCom.Net.Native.Modbus` is copied next
to your app. For RIDs without a packaged binary, build it with `build/build-native.sh <rust-target>` and set
`IOTCOM_NATIVE_PATH`, or simply use the managed `ModbusClient`.
