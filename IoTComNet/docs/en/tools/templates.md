---
title: Project templates
translation-status: synced
---

# Project templates

```bash
dotnet new install IoTCom.Net.Templates
```

| Short name | Creates | Options |
|---|---|---|
| `iotcom-console` | a console app that talks to a simulated device until you point it at real hardware | `--protocol modbus|nmea|mqtt`, `--lang en|id` |
| `iotcom-worker` | an edge gateway Worker Service: Modbus → MQTT (SenML), health checks, systemd/Windows Service, Dockerfile | `--docker true|false` |

```bash
dotnet new iotcom-console -n PlcReader --protocol modbus --lang id
dotnet new iotcom-worker -n LineGateway
cd LineGateway && dotnet run
```

Both templates accept `--iotcomVersion` to pin a package version.
