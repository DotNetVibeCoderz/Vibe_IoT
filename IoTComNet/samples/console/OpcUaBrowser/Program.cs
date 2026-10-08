// OpcUaBrowser — browse an OPC UA server, read every variable, and follow a few tags with one subscription.
//
//   dotnet run                                       # starts the plant simulator in this process
//   dotnet run -- opc.tcp://plc.local:4840           # a real server (its certificate must be trusted, see docs)
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using IoTCom.Net.Adapters.OpcUa;

await using var simulator = args.Length == 0 ? OpcUaPlantServer.Create(o => o.Port = 48400) : null;
if (simulator is not null) await simulator.StartAsync();
var endpoint = simulator?.EndpointUrl ?? args[0];

await using var ua = OpcUaClient.Create(o =>
{
    o.UseEndpoint(endpoint);
    o.ReadOnly = true;                                 // this sample never changes the device
    o.AcceptUntrustedCertificates = simulator is not null;
});
await ua.ConnectAsync();
Console.WriteLine($"Connected to {endpoint} ({ua.SecurityPolicy}, {ua.SecurityMode})");

// Walk the address space below Objects, skipping the standard namespace-0 nodes.
var variables = new List<string>();
async Task Walk(string? nodeId, string indent)
{
    foreach (var node in await ua.BrowseAsync(nodeId))
    {
        if (node.NodeId.StartsWith("i=", StringComparison.Ordinal)) continue;
        if (node.NodeClass == "Variable")
        {
            var value = await ua.ReadAsync(node.NodeId);
            Console.WriteLine($"{indent}{node.DisplayName,-16} = {value.Text,-22} {value.Status}");
            variables.Add(node.NodeId);
        }
        else
        {
            Console.WriteLine($"{indent}{node.DisplayName}{(node.NodeClass == "Method" ? "()" : "/")}");
            if (node.IsContainer) await Walk(node.NodeId, indent + "  ");
        }
    }
}

await Walk(null, "");

// One subscription for every variable; stop after ten notifications (or Ctrl+C with a real server).
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(simulator is null ? 3600 : 15));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var count = 0;
try
{
    await foreach (var change in ua.SubscribeAsync(variables, TimeSpan.FromMilliseconds(500), cts.Token))
    {
        Console.WriteLine($"  {change.SourceTimestamp?.ToLocalTime():HH:mm:ss.fff} {change.NodeId} = {change.Text}");
        if (++count == 10 && simulator is not null) break;
    }
}
catch (OperationCanceledException)
{
}
