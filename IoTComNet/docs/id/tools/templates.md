---
title: Template proyek
translation-status: synced
---

# Template proyek

```bash
dotnet new install IoTCom.Net.Templates
```

| Nama pendek | Membuat | Opsi |
|---|---|---|
| `iotcom-console` | aplikasi console yang berbicara dengan perangkat simulasi sampai Anda mengarahkannya ke perangkat keras sungguhan | `--protocol modbus|nmea|mqtt`, `--lang en|id` |
| `iotcom-worker` | Worker Service edge gateway: Modbus → MQTT (SenML), health check, systemd/Windows Service, Dockerfile | `--docker true|false` |

```bash
dotnet new iotcom-console -n PlcReader --protocol modbus --lang id
dotnet new iotcom-worker -n LineGateway
cd LineGateway && dotnet run
```

Kedua template menerima `--iotcomVersion` untuk mengunci versi paket.
