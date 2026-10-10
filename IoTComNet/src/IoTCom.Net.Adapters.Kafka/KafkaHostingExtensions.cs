using IoTCom.Net.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Adapters.Kafka;

/// <summary>Hosting helpers for the Kafka adapter (kept here so the IoTCom.Net meta-package does not depend on it).</summary>
public static class KafkaHostingExtensions
{
    /// <summary>Registers a Kafka endpoint that connects when the host starts.</summary>
    public static IoTComBuilder AddKafka(this IoTComBuilder builder, string name, Action<KafkaEndpointOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        return builder.AddEndpoint(name, sp => KafkaEndpoint.Create(o =>
        {
            o.Name = name;
            o.Logger = sp.GetService<ILoggerFactory>()?.CreateLogger("IoTCom.Kafka");
            configure(o);
        }));
    }
}
