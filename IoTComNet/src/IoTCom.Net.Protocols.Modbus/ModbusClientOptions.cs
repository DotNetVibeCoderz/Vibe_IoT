using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Modbus;

/// <summary>Fluent options for <see cref="ModbusClient"/>.</summary>
public sealed class ModbusClientOptions : ITransportBuilder<ModbusClientOptions>
{
    /// <summary>Transport factory (set by <c>UseTcp</c>, <c>UseSerial</c>, <c>UseInMemory</c>...).</summary>
    public TransportFactory? TransportFactory { get; set; }
    /// <summary>Framing (default TCP).</summary>
    public ModbusFramingMode Framing { get; set; } = ModbusFramingMode.Tcp;
    /// <summary>Default unit id (default 1).</summary>
    public byte UnitId { get; set; } = 1;
    /// <summary>Response timeout (default 1 s).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(1);
    /// <summary>Retries after a timeout (default 0).</summary>
    public int Retries { get; set; }
    /// <summary>Block every write function (safety mode for commissioning and monitoring).</summary>
    public bool ReadOnly { get; set; }
    /// <summary>Maximum in-flight requests for Modbus TCP (RTU/ASCII always use 1).</summary>
    public int MaxConcurrentRequests { get; set; } = 16;
    /// <summary>Reconnect behaviour when the link drops.</summary>
    public ReconnectPolicy Reconnect { get; set; } = ReconnectPolicy.Default;
    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }
    /// <summary>Endpoint name used in logs, metrics and the traffic tap.</summary>
    public string? Name { get; set; }
    /// <summary>Optional tap attached at creation.</summary>
    public ITrafficTap? Tap { get; set; }

    /// <inheritdoc />
    public ModbusClientOptions UseTransport(TransportFactory factory) { TransportFactory = factory; return this; }
    /// <summary>Uses RTU framing (serial line or RTU-over-TCP gateways).</summary>
    public ModbusClientOptions UseRtuFraming() { Framing = ModbusFramingMode.Rtu; return this; }
    /// <summary>Uses ASCII framing.</summary>
    public ModbusClientOptions UseAsciiFraming() { Framing = ModbusFramingMode.Ascii; return this; }
    /// <summary>Sets the default unit id.</summary>
    public ModbusClientOptions WithUnitId(byte unitId) { UnitId = unitId; return this; }
    /// <summary>Sets the response timeout.</summary>
    public ModbusClientOptions WithTimeout(TimeSpan timeout) { Timeout = timeout; return this; }
    /// <summary>Sets the retry count after timeouts.</summary>
    public ModbusClientOptions WithRetries(int retries) { Retries = retries; return this; }
    /// <summary>Enables read-only mode: write calls throw <see cref="ReadOnlyModeException"/>.</summary>
    public ModbusClientOptions AsReadOnly() { ReadOnly = true; return this; }
    /// <summary>Sets the reconnect policy.</summary>
    public ModbusClientOptions WithReconnect(ReconnectPolicy policy) { Reconnect = policy; return this; }
    /// <summary>Sets the logger.</summary>
    public ModbusClientOptions WithLogger(ILogger logger) { Logger = logger; return this; }
    /// <summary>Sets the endpoint name.</summary>
    public ModbusClientOptions WithName(string name) { Name = name; return this; }
    /// <summary>Attaches a traffic tap.</summary>
    public ModbusClientOptions WithTap(ITrafficTap tap) { Tap = tap; return this; }
    /// <summary>Sets the maximum number of pipelined TCP requests.</summary>
    public ModbusClientOptions WithMaxConcurrentRequests(int max) { MaxConcurrentRequests = max; return this; }
}
