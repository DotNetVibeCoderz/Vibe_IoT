// LiteCircuit C# automation example (dotnet-script / csi):
// exports Gerber bundles for every project through the REST API.
//
//   dotnet tool install -g dotnet-script
//   dotnet script BatchExport.csx -- http://localhost:5210

using System.Net.Http;
using System.Net.Http.Json;

var baseUrl = Args.Count > 0 ? Args[0] : "http://localhost:5210";
using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

record ProjectRow(Guid Id, string Name);

var projects = await http.GetFromJsonAsync<List<ProjectRow>>("/api/projects");
Console.WriteLine($"{projects!.Count} project(s) found");

Directory.CreateDirectory("gerber-out");
foreach (var p in projects)
{
    var bytes = await http.GetByteArrayAsync($"/api/projects/{p.Id}/gerber.zip");
    var path = Path.Combine("gerber-out", $"{p.Name.Replace(' ', '_')}.zip");
    await File.WriteAllBytesAsync(path, bytes);
    Console.WriteLine($"  {p.Name} -> {path} ({bytes.Length} bytes)");
}
Console.WriteLine("Done.");
