using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using IoTCom.Net;
using IoTCom.Net.Protocols.Modbus;

namespace IoTCom.Gateway;

/// <summary>Gateway settings (appsettings.json "Gateway" section).</summary>
public sealed class GatewayOptions
{
    public string LineName { get; set; } = "Line 1";
    public bool Simulate { get; set; } = true;
    public PlcOptions Plc { get; set; } = new();
    public int PollIntervalMs { get; set; } = 500;
    public bool AllowWrites { get; set; } = true;
    public MqttOptions Mqtt { get; set; } = new();

    public sealed class PlcOptions
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 1502;
        public byte UnitId { get; set; } = 1;
    }

    public sealed class MqttOptions
    {
        public bool EmbeddedBroker { get; set; } = true;
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 1883;
        public string Topic { get; set; } = "iotcom/line1/plc";
    }
}

/// <summary>One poll of the virtual PLC (register map: see ModbusSimulator.CreateVirtualPlc).</summary>
public sealed record PlantSnapshot(
    DateTimeOffset Time,
    bool Online,
    double TemperatureC,
    double SetpointC,
    double HumidityPct,
    int PressureHpa,
    int MotorRpm,
    double PowerKw,
    double EnergyKwh,
    int Counter,
    bool MotorRun,
    bool Pump,
    bool DoorClosed,
    bool EStopOk,
    bool HighTemp,
    string? Error);

/// <summary>A decoded frame for the dashboard's frame lane.</summary>
public sealed record FrameDto(DateTimeOffset Time, string Direction, string Protocol, string? Summary, string Hex, IReadOnlyList<FieldDto> Fields);

/// <summary>A frame field.</summary>
public sealed record FieldDto(string Name, int Offset, int Length, string Kind, string? Value);

/// <summary>Endpoint status for the nameplate lamps.</summary>
public sealed record EndpointDto(string Name, string Protocol, string State);

/// <summary>Write requests.</summary>
public sealed record MotorRequest(bool Run);

/// <summary>Setpoint request.</summary>
public sealed record SetpointRequest(double Celsius);

/// <summary>Static info for the dashboard header.</summary>
public sealed record InfoDto(string Line, string Product, string Version, string CreditEn, string CreditId, string PlcAddress, string MqttTopic, bool AllowWrites, bool Simulated);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(PlantSnapshot))]
[JsonSerializable(typeof(IReadOnlyList<PlantSnapshot>))]
[JsonSerializable(typeof(FrameDto))]
[JsonSerializable(typeof(IReadOnlyList<FrameDto>))]
[JsonSerializable(typeof(IReadOnlyList<EndpointDto>))]
[JsonSerializable(typeof(MotorRequest))]
[JsonSerializable(typeof(SetpointRequest))]
[JsonSerializable(typeof(InfoDto))]
[JsonSerializable(typeof(ProblemDto))]
public sealed partial class GatewayJson : JsonSerializerContext;

/// <summary>Error body.</summary>
public sealed record ProblemDto(string Title, string Detail);

/// <summary>Live plant state: latest snapshot, trend history and fan-out to SSE clients.</summary>
public sealed class PlantState
{
    private const int HistoryLength = 600; // 5 min at 500 ms
    private readonly Lock _gate = new();
    private readonly LinkedList<PlantSnapshot> _history = new();
    private readonly List<Channel<SseItem<string>>> _clients = [];

    public PlantSnapshot? Current { get; private set; }

    public IReadOnlyList<PlantSnapshot> History()
    {
        lock (_gate) return [.. _history];
    }

    public void Publish(PlantSnapshot snapshot)
    {
        lock (_gate)
        {
            Current = snapshot;
            _history.AddLast(snapshot);
            while (_history.Count > HistoryLength) _history.RemoveFirst();
        }
        Broadcast("snapshot", System.Text.Json.JsonSerializer.Serialize(snapshot, GatewayJson.Default.PlantSnapshot));
    }

    public void PublishFrame(FrameDto frame) => Broadcast("frame", System.Text.Json.JsonSerializer.Serialize(frame, GatewayJson.Default.FrameDto));

    private void Broadcast(string type, string json)
    {
        Channel<SseItem<string>>[] clients;
        lock (_gate) clients = [.. _clients];
        foreach (var c in clients) c.Writer.TryWrite(new SseItem<string>(json, type));
    }

    public async IAsyncEnumerable<SseItem<string>> Subscribe([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var ch = Channel.CreateBounded<SseItem<string>>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate) _clients.Add(ch);
        try
        {
            await foreach (var item in ch.Reader.ReadAllAsync(ct)) yield return item;
        }
        finally
        {
            lock (_gate) _clients.Remove(ch);
        }
    }

    public static FrameDto ToDto(TrafficFrame f)
    {
        var bytes = f.Data.Span;
        IReadOnlyList<FrameField> fields = f.Protocol switch
        {
            "modbus-tcp" => ModbusAnatomy.Describe(bytes, ModbusFramingMode.Tcp, isRequest: f.Direction == FrameDirection.Outbound),
            "modbus-rtu" => ModbusAnatomy.Describe(bytes, ModbusFramingMode.Rtu, isRequest: f.Direction == FrameDirection.Outbound),
            _ => [new FrameField("Payload", 0, bytes.Length, FrameFieldKind.Data)],
        };
        var shownLength = Math.Min(bytes.Length, 64);
        return new FrameDto(f.Timestamp, f.Direction == FrameDirection.Outbound ? "out" : "in", f.Protocol, f.Summary, HexDump.ToHex(bytes[..shownLength]),
            fields.Where(x => x.Offset < shownLength).Select(x => new FieldDto(x.Name, x.Offset, Math.Min(x.Length, shownLength - x.Offset), x.Kind.ToString().ToLowerInvariant(), x.Value)).ToArray());
    }
}
