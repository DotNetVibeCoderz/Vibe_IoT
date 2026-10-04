---
title: Deployment
translation-status: synced
---

# Deployment

## Edge worker

Mulai dari template:

```bash
dotnet new install IoTCom.Net.Templates
dotnet new iotcom-worker -n LineGateway
```

Template sudah memanggil `AddSystemd()` dan `AddWindowsService()`.

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
# Port di bawah 1024 tanpa root:
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

`Dockerfile` dari template membangun image runtime. Teruskan perangkat serial dengan `--device=/dev/ttyUSB0`, dan
gunakan host networking (`--network host`) untuk protokol broadcast/multicast UDP seperti Art-Net dan sACN.

## Trimming dan NativeAOT

Semua paket library ditandai `IsTrimmable` dan `IsAotCompatible` dan dianalisis untuk keduanya saat build. Gunakan
JSON context hasil source generator dengan helper JSON MQTT. `NativeModbusClient` memakai `LibraryImport` hasil source
generator dan membawa binary native per RID.

```bash
dotnet publish -c Release -r linux-x64 -p:PublishAot=true
```

## Library native

Saat Anda mem-publish untuk sebuah RID, file `runtimes/{rid}/native/` yang sesuai dari `IoTCom.Net.Native.Modbus`
disalin ke samping aplikasi. Untuk RID tanpa binary terpaket, build dengan `build/build-native.sh <rust-target>` dan
atur `IOTCOM_NATIVE_PATH`, atau cukup gunakan `ModbusClient` managed.
