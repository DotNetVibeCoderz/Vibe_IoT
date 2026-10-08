// DlmsMeterReader — read a smart meter over DLMS/COSEM: clock, energy registers, instantaneous values, load profile.
//
//   dotnet run                         # a simulated three-phase meter in this process (HDLC, public client)
//   dotnet run -- COM3                 # a real meter through an optical probe (IEC 62056-21 head, 9600 baud)
//   dotnet run -- 10.0.0.30            # a meter or gateway on TCP 4059 (wrapper)
//
// The public client (SAP 16) reads without a password and cannot change anything.
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using IoTCom.Net;
using IoTCom.Net.Protocols.Dlms;
using IoTCom.Net.Transport.Serial;
using IoTCom.Net.Transports;

var target = args.FirstOrDefault();
// Without arguments, host a simulated meter ourselves on an in-memory link.
var listener = target is null ? new InMemoryTransportListener("meter") : null;
await using var simulated = listener is null ? null : DlmsServer.Create(o => o.ListenInMemory(listener));
await using var meter = simulated is null ? null : new DlmsMeterSimulator(simulated);
if (simulated is not null) await simulated.StartAsync();

await using var client = DlmsClient.Create(o =>
{
    if (listener is not null) o.UseInMemory(listener);
    else if (target!.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || target.StartsWith('/')) o.UseSerial(target, 9600);
    else
    {
        o.UseTcp(target, DlmsWrapper.Port);
        o.Framing = DlmsFraming.Wrapper;
    }
});
await client.ConnectAsync();
Console.WriteLine($"IoTCom.Net {IoTComInfo.Version} · DLMS/COSEM · meter clock {await client.ReadClockAsync():yyyy-MM-dd HH:mm:ss zzz}\n");

foreach (var code in new[] { "1.0.1.8.0.255", "1.0.1.8.1.255", "1.0.1.8.2.255", "1.0.2.8.0.255", "1.0.1.7.0.255", "1.0.32.7.0.255", "1.0.31.7.0.255", "1.0.14.7.0.255" })
{
    var obis = ObisCode.Parse(code);
    var value = await client.ReadRegisterAsync(obis);
    Console.WriteLine($"  {obis.ToDisplayString(),-16} {obis.Description,-36} {value}");
}

var now = await client.ReadClockAsync();
var profile = await client.ReadProfileAsync(ObisCode.Parse("1.0.99.1.0.255"), now.AddHours(-2), now);
Console.WriteLine($"\nLoad profile, last 2 hours ({profile.Rows.Count} rows of {string.Join(", ", profile.Columns.Select(c => c.LogicalName.Description ?? c.LogicalName.ToString()))}):");
for (var i = 1; i < profile.Rows.Count; i++)
{
    var kwh = (profile.Rows[i][1].AsDouble() - profile.Rows[i - 1][1].AsDouble()) / 1000;
    Console.WriteLine($"  {profile.Rows[i][0].AsDateTime():HH:mm}  import {kwh,6:0.000} kWh  {new string('#', (int)Math.Round(kwh * 20))}");
}

Console.WriteLine($"\n{IoTComInfo.CreditEn}");
