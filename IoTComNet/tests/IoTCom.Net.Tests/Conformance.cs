using System.Text.Json;

namespace IoTCom.Net.Tests;

/// <summary>Loads the shared vectors in /conformance (also consumed by the Rust test suite).</summary>
internal static class Conformance
{
    private static readonly Lazy<string> Root = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "conformance");
            if (File.Exists(Path.Combine(candidate, "crc.json"))) return candidate;
        }
        throw new DirectoryNotFoundException("conformance/ directory not found");
    });

    public static JsonElement[] Load(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root.Value, file)));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    public static IEnumerable<object[]> Cases(string file) => Load(file).Select(e => new object[] { e.GetRawText() });

    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    public static byte[] Hex(this JsonElement e, string property) => Convert.FromHexString(e.GetProperty(property).GetString()!);
}
