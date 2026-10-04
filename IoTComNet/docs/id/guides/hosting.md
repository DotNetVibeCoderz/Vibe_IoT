---
title: Hosting dan dependency injection
translation-status: synced
---

# Hosting dan dependency injection

`IoTCom.Net.Hosting` menghubungkan endpoint ke `Microsoft.Extensions.Hosting`: daftarkan berdasarkan nama, lalu host
menyalakan server, menyambungkan client, berbagi traffic tap, dan melaporkan kesehatan.

```csharp
var builder = Host.CreateApplicationBuilder(args);   // atau WebApplication.CreateBuilder
builder.Services.AddIoTCom(iot => iot
    .AddModbusClient("plc1", o => o.UseTcp("10.0.0.5", 502).WithUnitId(1))
    .AddModbusServer("sim", o => o.UseTcp(IPAddress.Any, 1502))
    .AddMqtt("cloud", o => o.UseBroker("broker.example.com", 8883).WithTls().WithCredentials(user, password))
    .AddNmeaReader("gps", o => o.UseSerial("/dev/ttyUSB0", 9600))
    .AddHealthChecks());                                // panggil paling akhir
```

## Memakai endpoint

```csharp
public sealed class Poller(IoTComEndpoints endpoints) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var plc = endpoints.GetRequired<ModbusClient>("plc1");
        // ...
    }
}

// atau keyed service
app.MapGet("/temp", ([FromKeyedServices("plc1")] ModbusClient plc) => plc.ReadInputRegistersAsync(0, 1));
```

| API | Deskripsi |
|---|---|
| `AddEndpoint(name, factory, autoStart)` | endpoint apa pun; helper per protokol membungkusnya |
| `IoTComEndpoints.GetRequired<T>(name)` | pencarian bertipe |
| `IoTComEndpoints.States()` | state setiap endpoint (untuk dashboard) |
| `IoTComEndpoints.Tap` | `RecordingTap` yang terpasang ke setiap endpoint ter-host |
| `AddHealthChecks()` | satu check per endpoint, tag `iotcom`: Healthy saat terhubung/listening, Degraded saat menyambung |

## Perilaku saat start

Saat start, hosted service menyalakan server dan menyambungkan client sesuai urutan pendaftaran. Perangkat yang
offline saat boot dicatat sebagai warning dan **tidak** membuat host crash — client menyambung ulang pada request
berikutnya. Saat stop, setiap endpoint di-dispose.

Endpoint menerima `ILogger` dari container (kategori `IoTCom.Modbus`, `IoTCom.Mqtt`, …).

## Endpoint kesehatan

```csharp
app.MapHealthChecks("/health");
```

Lihat [sampel gateway](gateway.md) untuk aplikasi web lengkap.
