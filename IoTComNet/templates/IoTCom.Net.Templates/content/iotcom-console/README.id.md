# IoTComConsole

Dibuat dari template **IoTCom.Net** `iotcom-console`.

```bash
dotnet run
```

Aplikasi berjalan dengan simulator bawaan, jadi tidak perlu perangkat keras. Untuk terhubung ke perangkat sungguhan,
ganti `UseInMemory(...)` dengan `UseTcp(host, port)` atau `UseSerial(port, baud)` (paket `IoTCom.Net.Transport.Serial`).

Dokumentasi: https://www.nuget.org/packages/IoTCom.Net · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
