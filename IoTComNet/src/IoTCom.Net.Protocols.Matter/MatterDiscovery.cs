using System.Globalization;
using System.Net;
using IoTCom.Net.Protocols.Mdns;

namespace IoTCom.Net.Protocols.Matter;

/// <summary>A commissionable device found over DNS-SD.</summary>
/// <param name="Instance">Instance name.</param>
/// <param name="Address">Address and port to commission.</param>
/// <param name="Discriminator">Long discriminator (TXT D).</param>
/// <param name="VendorId">Vendor id (TXT VP).</param>
/// <param name="ProductId">Product id (TXT VP).</param>
/// <param name="Name">Device name (TXT DN).</param>
/// <param name="CommissioningMode">TXT CM (1 = open window, 2 = enhanced).</param>
public sealed record CommissionableDevice(string Instance, IPEndPoint Address, ushort Discriminator, ushort VendorId, ushort ProductId, string Name, int CommissioningMode);

/// <summary>
/// Matter DNS-SD (core spec §4.3): commissionable nodes advertise <c>_matterc._udp</c> with the discriminator, vendor and
/// product in TXT records; operational nodes advertise <c>_matter._tcp</c> as <c>&lt;compressed fabric id&gt;-&lt;node id&gt;</c>.
/// Uses IoTCom's mDNS responder and browser.
/// </summary>
public static class MatterDiscovery
{
    /// <summary>Commissionable service type.</summary>
    public const string CommissionableType = "_matterc._udp";

    /// <summary>Operational service type.</summary>
    public const string OperationalType = "_matter._tcp";

    /// <summary>The commissionable service of a device.</summary>
    public static MdnsService Commissionable(MatterSetupPayload payload, string deviceName, ushort port, IReadOnlyList<IPAddress> addresses, string instance) => new()
    {
        Instance = instance, Type = CommissionableType, Port = port, Addresses = addresses,
        Properties = new Dictionary<string, string>
        {
            ["D"] = payload.Discriminator.ToString(CultureInfo.InvariantCulture),
            ["CM"] = "1",
            ["VP"] = string.Create(CultureInfo.InvariantCulture, $"{payload.VendorId}+{payload.ProductId}"),
            ["DN"] = deviceName,
            ["PH"] = "33",
        },
    };

    /// <summary>The operational instance name for a node on a fabric.</summary>
    public static string OperationalInstance(string compressedFabricId, ulong nodeId) => $"{compressedFabricId.ToUpperInvariant()}-{nodeId:X16}";

    /// <summary>The operational service of a node.</summary>
    public static MdnsService Operational(string compressedFabricId, ulong nodeId, ushort port, IReadOnlyList<IPAddress> addresses) => new()
    {
        Instance = OperationalInstance(compressedFabricId, nodeId), Type = OperationalType, Port = port, Addresses = addresses,
        Properties = new Dictionary<string, string> { ["SII"] = "5000", ["SAI"] = "300" },
    };

    /// <summary>Browses for commissionable devices (optionally one discriminator).</summary>
    public static async Task<IReadOnlyList<CommissionableDevice>> FindCommissionableAsync(MdnsBrowser browser, TimeSpan duration, ushort? discriminator = null, CancellationToken ct = default)
    {
        var found = new List<CommissionableDevice>();
        foreach (var s in await browser.BrowseAsync(CommissionableType, duration, ct).ConfigureAwait(false))
        {
            if (s.Address is not { } a || !s.Properties.TryGetValue("D", out var d) || !ushort.TryParse(d, CultureInfo.InvariantCulture, out var disc)) continue;
            if (discriminator is { } want && want != disc) continue;
            ushort vid = 0, pid = 0;
            if (s.Properties.TryGetValue("VP", out var vp))
            {
                var parts = vp.Split('+');
                _ = ushort.TryParse(parts[0], CultureInfo.InvariantCulture, out vid);
                if (parts.Length > 1) _ = ushort.TryParse(parts[1], CultureInfo.InvariantCulture, out pid);
            }

            found.Add(new CommissionableDevice(s.Instance, new IPEndPoint(a, s.Port), disc, vid, pid, s.Properties.GetValueOrDefault("DN", ""),
                int.TryParse(s.Properties.GetValueOrDefault("CM", "0"), CultureInfo.InvariantCulture, out var cm) ? cm : 0));
        }

        return found;
    }

    /// <summary>Finds the address of an operational node on a controller's fabric.</summary>
    public static async Task<IPEndPoint?> FindOperationalAsync(MdnsBrowser browser, MatterController controller, ulong nodeId, TimeSpan duration, CancellationToken ct = default)
    {
        var name = OperationalInstance(controller.CompressedFabricId, nodeId);
        var service = (await browser.BrowseAsync(OperationalType, duration, ct).ConfigureAwait(false)).FirstOrDefault(s => string.Equals(s.Instance, name, StringComparison.OrdinalIgnoreCase));
        return service?.Address is { } a ? new IPEndPoint(a, service.Port) : null;
    }
}
