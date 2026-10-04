---
title: Endpoint dan transport
translation-status: synced
---

# Endpoint dan transport

## Peran

| Interface | Peran | Diimplementasikan oleh |
|---|---|---|
| `IClientEndpoint` | terhubung ke peer (master, tester, pembaca) | `ModbusClient`, `NativeModbusClient`, `NmeaReader`, `MqttEndpoint` |
| `IServerEndpoint` | melayani peer (slave, perangkat, simulator) | `ModbusServer`, `NmeaServer`, `ArtNetNode`, `SacnNode`, `MqttBroker` |
| `IPublisher<T>` | menerbitkan ke topik | `MqttEndpoint`, `NmeaServer`, node DMX |
| `ISubscriber<T>` | subscription `IAsyncEnumerable` | `MqttEndpoint`, `NmeaReader`, node DMX |

Protokol request/response (Modbus) mengekspos API bertipe sendiri (`ReadHoldingRegistersAsync`, …) dan hanya
mengimplementasikan peran client/server. Protokol berbentuk pub/sub mengimplementasikan kontrak publisher/subscriber.

## Siklus hidup

Setiap endpoint memiliki `State` (`Disconnected → Connecting → Connected/Listening → Stopping`, atau `Faulted`) dan
memicu `StateChanged`. Endpoint adalah `IAsyncDisposable`: gunakan `await using`.

Client terhubung secara lazy — request pertama akan menyambung bila Anda belum memanggil `ConnectAsync`. Setelah link
terputus, request berikutnya menyambung ulang sesuai `ReconnectPolicy` (exponential backoff dengan jitter;
`ReconnectPolicy.None` menonaktifkannya).

## Builder dan transport

Setiap endpoint dibuat dengan builder fluent. Ekstensi transport dipakai bersama oleh semua protokol:

```csharp
ModbusClient.Create(o => o.UseTcp("10.0.0.5", 502));          // Core
ModbusClient.Create(o => o.UseSerial("/dev/ttyUSB0", 19200));  // Transport.Serial
ModbusClient.Create(o => o.UseInMemory(listener));             // Core: pengujian, simulator

ModbusServer.Create(o => o.UseTcp(IPAddress.Any, 1502));       // listener
ModbusServer.Create(o => o.ServeSerial("COM5", 9600));         // slave di RS-485
ModbusServer.Create(o => o.ListenInMemory(listener));
```

`ITransport` mengekspos `IDuplexPipe` (`System.IO.Pipelines`): kode protokol membaca `ReadOnlySequence<byte>` tanpa
menyalin dan menulis lewat `PipeWriter`. Untuk mendukung link baru (TLS, WebSocket, keunikan USB-CDC),
implementasikan `ITransport` — atau turunkan dari `StreamTransport` dan kembalikan `Stream` — lalu panggil
`UseTransport(() => new MyTransport())`.

`InMemoryTransportListener` menghubungkan client dan server dalam satu proses. Galeri, notebook, dan sebagian besar
pengujian memakainya, karena itu tidak ada yang butuh perangkat keras.

## Model error

| Exception | Arti |
|---|---|
| `IoTComException` | tipe dasar |
| `TransportException` | link gagal (ditolak, dicabut, ditutup) |
| `ProtocolException` | byte rusak, checksum salah, response tak terduga |
| `IoTComTimeoutException` | tidak ada jawaban tepat waktu |
| `DeviceException` (`ModbusException`) | perangkat menjawab dengan error; kode vendor dipertahankan (`ExceptionCode`) |
| `ReadOnlyModeException` | ada percobaan menulis pada endpoint read-only |

Setiap operasi menerima `CancellationToken`; tidak ada pemanggilan blocking tersembunyi.

## Threading

Endpoint aman dipanggil secara bersamaan. Modbus TCP mem-pipeline request berdasarkan transaction id (hingga
`MaxConcurrentRequests`); RTU dan ASCII menserialisasinya sesuai kebutuhan bus. Event (`MessageReceived`,
`RequestHandled`, `DmxReceived`, …) dipicu di thread I/O — pindahkan ke thread UI sebelum menyentuh kontrol.
