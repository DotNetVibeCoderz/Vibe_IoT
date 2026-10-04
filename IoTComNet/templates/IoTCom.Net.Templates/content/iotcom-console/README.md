# IoTComConsole

Created from the **IoTCom.Net** `iotcom-console` template.

```bash
dotnet run
```

The app runs against a built-in simulator, so no hardware is needed. To talk to a real device, replace
`UseInMemory(...)` with `UseTcp(host, port)` or `UseSerial(port, baud)` (package `IoTCom.Net.Transport.Serial`).

Docs: https://www.nuget.org/packages/IoTCom.Net · Built by Gravicode Studios, led by Kang Fadhil.
