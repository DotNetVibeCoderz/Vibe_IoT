---
title: Hosting and dependency injection
translation-status: synced
---

# Hosting and dependency injection

`IoTCom.Net.Hosting` plugs endpoints into `Microsoft.Extensions.Hosting`: register them by name, and the host starts
servers, connects clients, shares a traffic tap and reports health.

```csharp
var builder = Host.CreateApplicationBuilder(args);   // or WebApplication.CreateBuilder
builder.Services.AddIoTCom(iot => iot
    .AddModbusClient("plc1", o => o.UseTcp("10.0.0.5", 502).WithUnitId(1))
    .AddModbusServer("sim", o => o.UseTcp(IPAddress.Any, 1502))
    .AddMqtt("cloud", o => o.UseBroker("broker.example.com", 8883).WithTls().WithCredentials(user, password))
    .AddNmeaReader("gps", o => o.UseSerial("/dev/ttyUSB0", 9600))
    .AddHealthChecks());                                // call last
```

## Using endpoints

```csharp
public sealed class Poller(IoTComEndpoints endpoints) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var plc = endpoints.GetRequired<ModbusClient>("plc1");
        // ...
    }
}

// or keyed services
app.MapGet("/temp", ([FromKeyedServices("plc1")] ModbusClient plc) => plc.ReadInputRegistersAsync(0, 1));
```

| API | Description |
|---|---|
| `AddEndpoint(name, factory, autoStart)` | any endpoint; the protocol-specific helpers wrap it |
| `IoTComEndpoints.GetRequired<T>(name)` | typed lookup |
| `IoTComEndpoints.States()` | state of every endpoint (dashboards) |
| `IoTComEndpoints.Tap` | a `RecordingTap` attached to every hosted endpoint |
| `AddHealthChecks()` | one check per endpoint, tag `iotcom`: Healthy when connected/listening, Degraded while connecting |

## Startup behaviour

On start the hosted service starts servers and connects clients in registration order. A device that is offline at
boot is logged as a warning and does **not** crash the host — clients reconnect on their next request. On stop every
endpoint is disposed.

Endpoints receive an `ILogger` from the container (categories `IoTCom.Modbus`, `IoTCom.Mqtt`, …).

## Health endpoint

```csharp
app.MapHealthChecks("/health");
```

See the [gateway sample](gateway.md) for a complete web application.
