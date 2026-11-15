using IoTCom.Net.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Adapters.Bacnet;

/// <summary>Hosting helpers for the BACnet/IP client.</summary>
public static class BacnetHostingExtensions
{
    /// <summary>Registers a BACnet/IP client (read-only unless <c>AllowWrites()</c>).</summary>
    public static IoTComBuilder AddBacnetClient(this IoTComBuilder builder, string name, Action<BacnetIpClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpoint(name, sp => BacnetIpClient.Create(o =>
        {
            o.Name = name;
            o.Logger = sp.GetService<ILoggerFactory>()?.CreateLogger("IoTCom.Bacnet");
            configure?.Invoke(o);
        }));
    }
}
