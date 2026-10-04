---
title: Edge gateway sample
translation-status: synced
---

# Edge gateway sample (IoTCom.Gateway)

`samples/web/IoTCom.Gateway` is a complete ASP.NET Core application: it polls a PLC over Modbus TCP, publishes SenML
telemetry to MQTT, exposes a REST + Server-Sent Events API, and serves a live HMI dashboard. With the default settings
it brings its own virtual PLC and MQTT broker, so it runs anywhere.

```bash
dotnet run --project samples/web/IoTCom.Gateway --urls http://localhost:5080
```

![Gateway dashboard](../../images/gateway-dashboard.png)

## What you are looking at

- **Nameplate** — one signal lamp per hosted endpoint (`plc-sim`, `plc`, `broker`, `uplink`), green when healthy.
- **Mimic panel** — temperature against its setpoint scale (the hatched band is the alarm zone), motor speed with a
  run/stop switch, process values and interlock lamps. The setpoint stepper and the switch write to the PLC.
- **Trend** — five minutes of temperature, setpoint and power.
- **Frames on the wire** — every Modbus request and response and every MQTT publish, decoded field by field.

The page follows the system's light/dark preference (toggle in the header), switches between English and Bahasa
Indonesia, and works down to phone width.

![Dark mode](../../images/gateway-dashboard-dark.png)

## Configuration (`appsettings.json`)

```json
"Gateway": {
  "LineName": "Line 1",
  "Simulate": true,                         // false: no virtual PLC, connect to Plc.Host
  "Plc": { "Host": "127.0.0.1", "Port": 1502, "UnitId": 1 },
  "PollIntervalMs": 500,
  "AllowWrites": true,                      // false: the dashboard becomes read-only
  "Mqtt": { "EmbeddedBroker": true, "Host": "127.0.0.1", "Port": 1883, "Topic": "iotcom/line1/plc" }
}
```

## API

| Method | Path | Description |
|---|---|---|
| GET | `/api/info` | line name, version, credits, PLC address, MQTT topic |
| GET | `/api/snapshot` | latest plant values |
| GET | `/api/history` | last 5 minutes |
| GET | `/api/frames` | last decoded frames |
| GET | `/api/endpoints` | endpoint states |
| GET | `/api/stream` | SSE: `snapshot` and `frame` events |
| POST | `/api/motor` | `{ "run": true }` → coil 0 |
| POST | `/api/setpoint` | `{ "celsius": 26.5 }` → holding register 0 (5–60 °C) |
| GET | `/health` | health checks |

Watch the MQTT side with `iotcom mqtt sub "iotcom/#"`.

## How it is built

`Program.cs` registers the endpoints with `AddIoTCom`; `GatewayWorker` polls the PLC, builds a SenML pack and
publishes it, and pushes snapshots and decoded frames (from the shared hosting tap) to SSE clients. JSON uses a
source-generated context, so the app is trim-friendly. The dashboard is plain HTML/CSS/JS in `wwwroot` with no build step.
