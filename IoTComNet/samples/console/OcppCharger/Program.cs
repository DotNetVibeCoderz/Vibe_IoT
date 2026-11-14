// OcppCharger — an EV charger speaking OCPP, plus a small CSMS that manages it.
//
//   dotnet run                                              # CSMS and charger in-process: boot, card, charging, limit, stop
//   dotnet run -- ws://localhost:8180/steve/websocket/CentralSystemService/ CP-001 1.6
//                                                           # only the charger, against a CSMS you run (SteVe, CitrineOS, …)
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using IoTCom.Net;
using IoTCom.Net.Protocols.Ocpp;
using IoTCom.Net.Transports;

if (args.Length > 0)
{
    await using var remote = OcppChargePointSimulator.Create(o =>
    {
        o.UseCsms(args[0]);
        if (args.Length > 1) o.Identity = args[1];
        o.Version = args.Length > 2 && args[2].StartsWith('2') ? OcppVersion.Ocpp201 : OcppVersion.Ocpp16;
        o.AutoRun = true;
        o.TimeScale = 60;                                      // one simulated minute per second
    });
    remote.ChargePoint.Message += e => Console.WriteLine($"{(e.Direction == FrameDirection.Inbound ? "<-" : "->")} {e.Json}");
    await remote.StartAsync();
    Console.WriteLine($"{remote.Identity} booted: {remote.Registered}. Plugging in an EV and presenting tag 04A1B2C3. Ctrl+C to stop.");
    await remote.PlugInAsync(1, socPercent: 25);
    await remote.PresentCardAsync(1, "04A1B2C3");
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    try
    {
        await Task.Delay(Timeout.Infinite, stop.Token);
    }
    catch (OperationCanceledException)
    {
    }

    await remote.UnplugAsync(1);
    return;
}

// In-process: the CSMS accepts two tags and is allowed to send commands.
var listener = new InMemoryTransportListener("csms");
await using var csms = OcppCsms.Create(o =>
{
    o.ListenInMemory(listener);
    o.AuthorizedTags = new HashSet<string> { "04A1B2C3", "APP-7731" };
    o.AllowCommands();
});
csms.TransactionStarted += (st, tx) => Console.WriteLine($"CSMS: {st.Identity} started {tx.Id} on connector {tx.ConnectorId} for {tx.IdTag}");
csms.TransactionStopped += (st, tx) => Console.WriteLine($"CSMS: {st.Identity} {tx}");
await csms.StartAsync();

await using var charger = OcppChargePointSimulator.Create(o =>
{
    o.Identity = "SPKLU-KM57-01";
    o.Version = OcppVersion.Ocpp201;                           // or OcppVersion.Ocpp16
    o.UseTransport(listener.Connect);
});
await charger.StartAsync();
var station = csms.Stations["SPKLU-KM57-01"];
Console.WriteLine($"Booted: {station}");

await charger.PlugInAsync(1, socPercent: 20, batteryKWh: 60, evMaxKW: 11);
Console.WriteLine($"Card accepted: {await charger.PresentCardAsync(1, "04A1B2C3")}");
await charger.StepAsync(TimeSpan.FromMinutes(20));
await Task.Delay(100);
Console.WriteLine($"After 20 min: {station.Connectors[1].PowerW / 1000:0.0} kW, {station.Connectors[1].Transaction?.EnergyWh / 1000:0.00} kWh, SoC {station.Connectors[1].SoC:0} %");

Console.WriteLine($"Limit to 7.4 kW: {await csms.SetPowerLimitAsync(station.Identity, 1, 7400)}");
await charger.StepAsync(TimeSpan.FromMinutes(20));
await Task.Delay(100);
Console.WriteLine($"After 20 more min: {station.Connectors[1].PowerW / 1000:0.0} kW, SoC {station.Connectors[1].SoC:0} %");

foreach (var c in await csms.ReadConfigurationAsync(station.Identity)) Console.WriteLine($"  {c.Key} = {c.Value}");
Console.WriteLine($"Remote stop: {await csms.RemoteStopAsync(station.Identity, station.Connectors[1].Transaction!.Id)}");
await Task.Delay(200);
await charger.UnplugAsync(1);
