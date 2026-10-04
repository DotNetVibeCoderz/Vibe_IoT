using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Hosting;

/// <summary>A named endpoint registration.</summary>
/// <param name="Name">Unique name (used in logs, health checks and <see cref="IoTComEndpoints"/>).</param>
/// <param name="Factory">Creates the endpoint.</param>
/// <param name="AutoStart">Connect/start it when the host starts.</param>
public sealed record EndpointRegistration(string Name, Func<IServiceProvider, IEndpoint> Factory, bool AutoStart = true);

/// <summary>Fluent registration API returned by <see cref="IoTComServiceCollectionExtensions.AddIoTCom"/>.</summary>
public sealed class IoTComBuilder
{
    internal IoTComBuilder(IServiceCollection services) => Services = services;

    /// <summary>The service collection.</summary>
    public IServiceCollection Services { get; }

    internal List<EndpointRegistration> Registrations { get; } = [];

    /// <summary>Registers an endpoint created by <paramref name="factory"/>.</summary>
    public IoTComBuilder AddEndpoint<T>(string name, Func<IServiceProvider, T> factory, bool autoStart = true) where T : class, IEndpoint
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Registrations.Any(r => r.Name == name)) throw new ArgumentException($"An IoTCom endpoint named '{name}' is already registered.", nameof(name));
        Registrations.Add(new EndpointRegistration(name, sp => factory(sp), autoStart));
        Services.AddKeyedSingleton<T>(name, (sp, _) => sp.GetRequiredService<IoTComEndpoints>().GetRequired<T>(name));
        return this;
    }

    /// <summary>Adds one health check per endpoint registered so far (tag <c>iotcom</c>). Call it last.</summary>
    public IoTComBuilder AddHealthChecks()
    {
        var hc = Services.AddHealthChecks();
        foreach (var r in Registrations)
        {
            var name = r.Name;
            hc.Add(new HealthCheckRegistration($"iotcom:{name}", sp => new EndpointHealthCheck(sp.GetRequiredService<IoTComEndpoints>(), name), HealthStatus.Unhealthy, ["iotcom"]));
        }
        return this;
    }
}

/// <summary>DI extensions.</summary>
public static class IoTComServiceCollectionExtensions
{
    /// <summary>
    /// Adds IoTCom.Net: a named endpoint registry, a shared <see cref="RecordingTap"/>, and a hosted service
    /// that starts servers and connects clients when the host starts.
    /// </summary>
    public static IServiceCollection AddIoTCom(this IServiceCollection services, Action<IoTComBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new IoTComBuilder(services);
        configure(builder);
        services.TryAddSingleton(new RecordingTap(2000));
        services.AddSingleton(sp => new IoTComEndpoints(sp, builder.Registrations, sp.GetRequiredService<RecordingTap>()));
        services.AddHostedService<IoTComHostedService>();
        return services;
    }
}

/// <summary>Registry of the named endpoints created by the host.</summary>
public sealed class IoTComEndpoints : IAsyncDisposable
{
    private readonly Dictionary<string, Lazy<IEndpoint>> _endpoints;

    internal IoTComEndpoints(IServiceProvider services, IEnumerable<EndpointRegistration> registrations, RecordingTap tap)
    {
        Registrations = registrations.ToArray();
        Tap = tap;
        _endpoints = Registrations.ToDictionary(r => r.Name, r => new Lazy<IEndpoint>(() =>
        {
            var e = r.Factory(services);
            if (e is EndpointBase b) b.AddTap(tap);
            return e;
        }));
    }

    /// <summary>All registrations.</summary>
    public IReadOnlyList<EndpointRegistration> Registrations { get; }

    /// <summary>Shared tap that sees the traffic of every hosted endpoint.</summary>
    public RecordingTap Tap { get; }

    /// <summary>Names of the registered endpoints.</summary>
    public IEnumerable<string> Names => _endpoints.Keys;

    /// <summary>Gets an endpoint by name.</summary>
    public IEndpoint Get(string name) => _endpoints.TryGetValue(name, out var e) ? e.Value : throw new KeyNotFoundException($"No IoTCom endpoint named '{name}'.");

    /// <summary>Gets a typed endpoint by name.</summary>
    public T GetRequired<T>(string name) where T : class, IEndpoint =>
        Get(name) as T ?? throw new InvalidCastException($"Endpoint '{name}' is {Get(name).GetType().Name}, not {typeof(T).Name}.");

    /// <summary>Snapshot of every endpoint's state (for dashboards).</summary>
    public IReadOnlyDictionary<string, EndpointState> States() => _endpoints.ToDictionary(kv => kv.Key, kv => kv.Value.IsValueCreated ? kv.Value.Value.State : EndpointState.Disconnected);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var e in _endpoints.Values.Where(e => e.IsValueCreated)) await e.Value.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Starts servers and connects clients on host start; disposes everything on stop.</summary>
internal sealed class IoTComHostedService(IoTComEndpoints endpoints, ILogger<IoTComHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var r in endpoints.Registrations.Where(r => r.AutoStart))
        {
            var e = endpoints.Get(r.Name);
            try
            {
                switch (e)
                {
                    case IServerEndpoint s: await s.StartAsync(cancellationToken).ConfigureAwait(false); break;
                    case IClientEndpoint c: await c.ConnectAsync(cancellationToken).ConfigureAwait(false); break;
                }
                logger.LogInformation("IoTCom endpoint {Name} ({Protocol}) is {State}", r.Name, e.Protocol, e.State);
            }
            catch (Exception ex) when (ex is IoTComException)
            {
                // Clients reconnect lazily; a device being offline at boot must not crash the host.
                logger.LogWarning(ex, "IoTCom endpoint {Name} ({Protocol}) failed to start", r.Name, e.Protocol);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => endpoints.DisposeAsync().AsTask();
}

/// <summary>Reports endpoint state as health.</summary>
internal sealed class EndpointHealthCheck(IoTComEndpoints endpoints, string name) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var e = endpoints.Get(name);
        var data = new Dictionary<string, object> { ["protocol"] = e.Protocol, ["state"] = e.State.ToString() };
        return Task.FromResult(e.State switch
        {
            EndpointState.Connected or EndpointState.Listening => HealthCheckResult.Healthy($"{name} is {e.State}", data),
            EndpointState.Connecting => HealthCheckResult.Degraded($"{name} is connecting", data: data),
            _ => HealthCheckResult.Unhealthy($"{name} is {e.State}", data: data),
        });
    }
}
