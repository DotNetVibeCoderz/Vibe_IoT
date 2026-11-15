using IoTCom.Net.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Matter;

/// <summary>Hosting helpers for Matter (this package is not part of the IoTCom.Net meta-package).</summary>
public static class MatterHostingExtensions
{
    /// <summary>Registers a Matter controller (read-only unless <c>AllowWrites()</c>; set <c>FabricFile</c> to keep the fabric).</summary>
    public static IoTComBuilder AddMatterController(this IoTComBuilder builder, string name, Action<MatterControllerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpoint(name, sp => MatterController.Create(o =>
        {
            o.Name = name;
            o.Logger = sp.GetService<ILoggerFactory>()?.CreateLogger("IoTCom.Matter");
            configure(o);
        }));
    }

    /// <summary>Registers a Matter device; add endpoints to the resolved device before the host starts it.</summary>
    public static IoTComBuilder AddMatterDevice(this IoTComBuilder builder, string name, Action<MatterDeviceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpoint(name, sp => MatterDevice.Create(o =>
        {
            o.Name = name;
            o.Logger = sp.GetService<ILoggerFactory>()?.CreateLogger("IoTCom.Matter");
            configure(o);
        }));
    }
}
