using IoTCom.Net.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Adapters.Amqp;

/// <summary>Hosting helpers for the Amqp adapter (kept here so the IoTCom.Net meta-package does not depend on it).</summary>
public static class AmqpHostingExtensions
{
    /// <summary>Registers a Amqp endpoint that connects when the host starts.</summary>
    public static IoTComBuilder AddAmqp(this IoTComBuilder builder, string name, Action<AmqpEndpointOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        return builder.AddEndpoint(name, sp => AmqpEndpoint.Create(o =>
        {
            o.Name = name;
            o.Logger = sp.GetService<ILoggerFactory>()?.CreateLogger("IoTCom.Amqp");
            configure(o);
        }));
    }
}
