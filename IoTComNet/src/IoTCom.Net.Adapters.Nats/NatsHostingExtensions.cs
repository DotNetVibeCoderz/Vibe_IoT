using IoTCom.Net.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Adapters.Nats;

/// <summary>Hosting helpers for the Nats adapter (kept here so the IoTCom.Net meta-package does not depend on it).</summary>
public static class NatsHostingExtensions
{
    /// <summary>Registers a Nats endpoint that connects when the host starts.</summary>
    public static IoTComBuilder AddNats(this IoTComBuilder builder, string name, Action<NatsEndpointOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        return builder.AddEndpoint(name, sp => NatsEndpoint.Create(o =>
        {
            o.Name = name;
            o.Logger = sp.GetService<ILoggerFactory>()?.CreateLogger("IoTCom.Nats");
            configure(o);
        }));
    }
}
