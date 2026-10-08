using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IoTCom.Net.Protocols.Coap;
using IoTCom.Net.Protocols.Dlms;
using IoTCom.Net.Protocols.LoRaWan;
using IoTCom.Net.Protocols.MBus;
using IoTCom.Net.Protocols.Mdns;
using IoTCom.Net.Protocols.Sparkplug;
using IoTCom.Net.Protocols.Mavlink;
using IoTCom.Net.Protocols.Mavlink.Common;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Protocols.Uds;
using IoTCom.Net.Serialization.MessagePack;
using IoTCom.Net.Serialization.Protobuf;
using IoTCom.Net.Serialization.Tlv;
using IoTCom.Net.Transport.Can;
using IoTCom.Net.Transports;
using Spectre.Console.Cli;
using CanFrame = IoTCom.Net.Transport.Can.CanFrame; // MAVLink's common dialect also has a CAN_FRAME message

namespace IoTCom.Net.Cli.Commands;

/// <summary>
/// <c>iotcom rpc</c>: JSON-RPC 2.0 over stdio (one JSON object per line) for editors and tools — the VS Code extension
/// "IoTCom.Net Tools" uses it so decoding and simulation logic is never duplicated in TypeScript.
/// </summary>
/// <remarks>
/// Methods: <c>initialize</c>, <c>decode</c> {protocol, hex}, <c>devices</c>, <c>monitor.start</c> {source},
/// <c>monitor.stop</c> {id}, <c>monitor.save</c> {id, path}, <c>shutdown</c>. Notification: <c>frame</c>
/// {monitor, time, protocol, direction, hex, summary, fields}.
/// </remarks>
internal sealed class RpcCommand : AsyncCommand<RpcCommand.Settings>
{
    public sealed class Settings : CommandSettings;

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    private readonly SemaphoreSlim _out = new(1, 1);
    private readonly ConcurrentDictionary<string, Monitor> _monitors = new();
    private TextWriter _stdout = Console.Out;
    private int _nextMonitor;

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken ct)
    {
        _stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await stdin.ReadLineAsync(ct);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? id = null;
                try
                {
                    var request = JsonNode.Parse(line)!.AsObject();
                    id = request["id"]?.DeepClone();
                    var method = request["method"]?.GetValue<string>() ?? throw new RpcError(-32600, "method is required");
                    if (method == "shutdown")
                    {
                        await ReplyAsync(id, JsonValue.Create(true));
                        break;
                    }
                    var result = await DispatchAsync(method, request["params"] as JsonObject ?? [], ct);
                    if (id is not null) await ReplyAsync(id, result);
                }
                catch (RpcError e)
                {
                    await ErrorAsync(id, e.Code, e.Message);
                }
                catch (JsonException e)
                {
                    await ErrorAsync(id, -32700, "parse error: " + e.Message);
                }
                catch (Exception e) when (e is IoTComException or ArgumentException or FormatException or InvalidOperationException or IOException)
                {
                    await ErrorAsync(id, -32000, e.Message);
                }
            }
        }
        catch (OperationCanceledException) { }
        foreach (var m in _monitors.Values) await m.DisposeAsync();
        return 0;
    }

    private sealed class RpcError(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private Task<JsonNode?> DispatchAsync(string method, JsonObject p, CancellationToken ct) => method switch
    {
        "initialize" => Task.FromResult<JsonNode?>(Initialize()),
        "decode" => Task.FromResult<JsonNode?>(Decode(Required(p, "protocol"), Required(p, "hex"), p["direction"]?.GetValue<string>())),
        "devices" => Task.FromResult<JsonNode?>(Devices()),
        "monitor.start" => StartMonitorAsync(Required(p, "source"), ct),
        "monitor.stop" => StopMonitorAsync(Required(p, "id")),
        "monitor.save" => Task.FromResult<JsonNode?>(SaveMonitor(Required(p, "id"), Required(p, "path"))),
        _ => throw new RpcError(-32601, $"unknown method {method}"),
    };

    private static string Required(JsonObject p, string name) =>
        p[name]?.GetValue<string>() is { Length: > 0 } v ? v : throw new RpcError(-32602, $"parameter '{name}' is required");

    private async Task WriteAsync(JsonObject message)
    {
        var text = message.ToJsonString(Compact);
        await _out.WaitAsync();
        try
        {
            await _stdout.WriteLineAsync(text);
        }
        finally
        {
            _out.Release();
        }
    }

    private Task ReplyAsync(JsonNode? id, JsonNode? result) => WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });

    private Task ErrorAsync(JsonNode? id, int code, string message) =>
        WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

    // ---- initialize -----------------------------------------------------------------------------------------------

    private static readonly (string Id, string Name, string Package, string Roles, string Docs, string? Notebook, string? Sample, string[] Decoders)[] Protocols =
    [
        ("modbus", "Modbus TCP / RTU / ASCII", "IoTCom.Net.Protocols.Modbus", "master · slave · simulator", "protocols/modbus.md", "industrial/01-modbus", "ModbusMaster", ["modbus-tcp", "modbus-rtu", "modbus-ascii"]),
        ("can", "CAN / CAN FD", "IoTCom.Net.Transport.Can", "SocketCAN · slcan · virtual bus", "protocols/can.md", "automotive/06-can-uds", null, ["can"]),
        ("uds", "ISO-TP · UDS · OBD-II", "IoTCom.Net.Protocols.Uds", "tester · scan tool · ECU simulator", "protocols/uds.md", "automotive/06-can-uds", "UdsTester", ["uds"]),
        ("coap", "CoAP", "IoTCom.Net.Protocols.Coap", "client · server · observe · block-wise", "protocols/coap.md", "messaging/07-coap", "CoapObserve", ["coap"]),
        ("mavlink", "MAVLink v1 / v2", "IoTCom.Net.Protocols.Mavlink", "link · ground station · simulator · generator", "protocols/mavlink.md", "navigation/08-mavlink", "MavlinkTelemetry", ["mavlink"]),
        ("lorawan", "LoRaWAN 1.0.x", "IoTCom.Net.Protocols.LoRaWan", "network server · Semtech UDP · end device · simulator", "protocols/lorawan.md", "lpwan/09-lorawan", "LoRaWanGatewayMonitor", ["lorawan", "semtech-udp"]),
        ("dlms", "DLMS/COSEM", "IoTCom.Net.Protocols.Dlms", "meter reader · meter simulator · HDLC · wrapper", "protocols/dlms.md", "metering/10-dlms-mbus", "DlmsMeterReader", ["dlms"]),
        ("mbus", "M-Bus (wired)", "IoTCom.Net.Protocols.MBus", "master · scan · secondary addressing · simulator", "protocols/mbus.md", "metering/10-dlms-mbus", "MBusScanner", ["mbus"]),
        ("nmea", "NMEA 0183", "IoTCom.Net.Protocols.Nmea", "reader · server · simulator", "protocols/nmea.md", "navigation/03-nmea", "NmeaGpsReader", []),
        ("dmx", "Art-Net 4 · sACN", "IoTCom.Net.Protocols.Dmx", "send · receive · discovery", "protocols/dmx.md", null, "ArtNetPlayer", []),
        ("hl7", "HL7 v2 over MLLP", "IoTCom.Net.Protocols.Hl7", "sender · receiver · monitor simulator", "protocols/hl7.md", "medical/05-hl7-dicom", "Hl7MllpListener", []),
        ("dicom", "DICOM", "IoTCom.Net.Adapters.Dicom", "storage SCP/SCU · rendering", "protocols/dicom.md", "medical/05-hl7-dicom", null, []),
        ("mqtt", "MQTT 3.1.1 / 5.0", "IoTCom.Net.Adapters.Mqtt", "publish · subscribe · broker", "protocols/mqtt.md", "messaging/04-mqtt-senml", "MqttSenMLBridge", []),
        ("ble", "Bluetooth LE", "IoTCom.Net.Transport.Ble", "central · GATT · beacons · virtual radio", "protocols/ble.md", "devices/14-ble", "BleHeartRate", ["ble-adv"]),
        ("opcua", "OPC UA", "IoTCom.Net.Adapters.OpcUa", "client · plant simulator server", "protocols/opcua.md", "industrial/13-opcua", "OpcUaBrowser", []),
        ("sparkplug", "Sparkplug B", "IoTCom.Net.Protocols.Sparkplug", "edge node · host application · line simulator", "protocols/sparkplug.md", "messaging/12-mdns-sparkplug", "SparkplugEdgeNode", ["sparkplug"]),
        ("mdns", "mDNS / DNS-SD", "IoTCom.Net.Protocols.Mdns", "responder · browser", "protocols/mdns.md", "messaging/12-mdns-sparkplug", "MdnsDiscovery", ["dns"]),
        ("payloads", "Protobuf · MessagePack · TLV", "IoTCom.Net.Serialization.Protobuf", "codecs · schema-less inspection", "protocols/payload-codecs.md", "messaging/12-mdns-sparkplug", null, ["protobuf", "msgpack", "ber-tlv"]),
        ("framing", "CRC · SLIP · COBS · HDLC", "IoTCom.Net.Framing", "codec", "protocols/framing.md", "transport/02-framing-crc", null, []),
        ("senml", "SenML", "IoTCom.Net.Serialization.SenML", "JSON · CBOR codec", "protocols/senml.md", "messaging/04-mqtt-senml", null, []),
    ];

    private static readonly string[] Sources = ["sim:modbus", "sim:can", "sim:coap", "sim:mavlink", "sim:lorawan", "sim:dlms", "sim:mbus", "can:<uri>", "mavlink:udp:<port>", "lorawan:udp:<port>"];

    private static JsonObject Initialize() => new()
    {
        ["name"] = "iotcom",
        ["version"] = IoTComInfo.Version,
        ["credit"] = new JsonObject { ["en"] = IoTComInfo.CreditEn, ["id"] = IoTComInfo.CreditId },
        ["docsBase"] = "https://github.com/DotNetVibeCoderz/Vibe_IoT/blob/main/IoTComNet/docs/",
        ["protocols"] = new JsonArray(Protocols.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id, ["name"] = p.Name, ["package"] = p.Package, ["roles"] = p.Roles, ["docs"] = p.Docs,
            ["notebook"] = p.Notebook, ["sample"] = p.Sample, ["decoders"] = new JsonArray(p.Decoders.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
        }).ToArray()),
        ["monitorSources"] = new JsonArray(Sources.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
    };

    // ---- decode -----------------------------------------------------------------------------------------------------

    private static JsonObject Decode(string protocol, string hex, string? direction)
    {
        var bytes = Convert.FromHexString(new string(hex.Where(Uri.IsHexDigit).ToArray()));
        var request = direction is null or "request" or "out";
        IReadOnlyList<FrameField> fields;
        string summary;
        switch (protocol)
        {
            case "modbus-tcp" or "modbus-rtu" or "modbus-ascii":
                var mode = protocol switch { "modbus-rtu" => ModbusFramingMode.Rtu, "modbus-ascii" => ModbusFramingMode.Ascii, _ => ModbusFramingMode.Tcp };
                fields = ModbusAnatomy.Describe(bytes, mode, request);
                var fn = fields.FirstOrDefault(f => f.Kind is FrameFieldKind.Function or FrameFieldKind.Error);
                summary = fn.Value ?? fn.Name ?? "Modbus";
                break;
            case "coap":
                fields = CoapAnatomy.Describe(bytes);
                summary = CoapMessage.TryDecode(bytes, out var m, out var error) ? m.ToString() : "invalid: " + error;
                break;
            case "mavlink":
                fields = MavlinkAnatomy.Describe(bytes, CommonDialect.Instance);
                var parser = new MavlinkParser(CommonDialect.Instance);
                parser.Feed(bytes);
                summary = parser.TryRead(out var pk) ? pk.ToString() : $"not a valid frame (CRC errors {parser.CrcErrors})";
                break;
            case "can":
                // Accepts the SocketCAN tap layout (8-byte header + data) or candump text bytes.
                var text = Encoding.ASCII.GetString(bytes);
                var frame = CanFrame.TryParse(text, out var parsed) ? parsed : bytes.Length >= 8 ? SocketCanBus.Decode(Pad(bytes)) : throw new FormatException("CAN: expected candump text or the 8-byte SocketCAN header + data");
                var tap = CanBusBase.ToTapBytes(frame);
                return Result(tap, CanFields(tap), frame.ToString());
            case "lorawan":
                fields = LoRaWanAnatomy.Describe(bytes);
                summary = LoRaWanPacket.TryDecode(bytes, out var lw, out var lwError) ? lw!.ToString() : "invalid: " + lwError;
                break;
            case "semtech-udp":
                fields = SemtechAnatomy.Describe(bytes);
                summary = SemtechPacket.TryDecode(bytes, out var gw, out var gwError) ? gw!.ToString() : "invalid: " + gwError;
                break;
            case "dlms":
                fields = DlmsAnatomy.Describe(bytes);
                summary = fields.FirstOrDefault(f => f.Kind == FrameFieldKind.Function).Value ?? "DLMS";
                break;
            case "mbus":
                fields = MBusAnatomy.Describe(bytes);
                summary = MBusFrame.TryRead(bytes, out var mb, out _, out var mbError) == MBusFrame.ReadStatus.Frame ? mb!.ToString() : "invalid: " + mbError;
                break;
            case "sparkplug":
                fields = SparkplugPayload.Describe(bytes);
                summary = TrySummary(() => SparkplugPayload.Decode(bytes).ToString());
                break;
            case "dns":
                fields = DnsMessage.Describe(bytes);
                summary = DnsMessage.TryDecode(bytes, out var dns, out var dnsError)
                    ? $"{(dns!.IsResponse ? "response" : "query")} {string.Join(", ", dns.Questions.Select(q => $"{q.Name} {q.Type}").Concat(dns.Answers.Select(a => a.ToString())).Take(3))}"
                    : "invalid: " + dnsError;
                break;
            case "protobuf":
                fields = ProtobufWire.Describe(bytes);
                summary = ProtobufWire.TryInspect(bytes, out var pf) ? $"{pf.Count} fields" : "not Protobuf";
                break;
            case "msgpack":
                fields = MessagePackView.Describe(bytes);
                summary = MessagePackView.ToJson(bytes) ?? "not MessagePack";
                break;
            case "ber-tlv":
                fields = BerTlv.Describe(bytes);
                summary = TrySummary(() => string.Join(", ", BerTlv.Decode(bytes)));
                break;
            case "ble-adv":
                fields = IoTCom.Net.Transport.Ble.AdvertisingData.Describe(bytes);
                summary = TrySummary(() => IoTCom.Net.Transport.Ble.AdvertisingData.Parse("adv", bytes).ToString());
                break;
            case "uds":
                fields = UdsAnatomy.Describe(bytes);
                summary = bytes.Length == 0 ? "empty" : UdsService.Name(bytes[0]);
                break;
            default:
                throw new RpcError(-32602, $"no decoder for '{protocol}' (modbus-tcp, modbus-rtu, modbus-ascii, coap, mavlink, lorawan, semtech-udp, dlms, mbus, sparkplug, dns, protobuf, msgpack, ber-tlv, ble-adv, can, uds)");
        }
        return Result(bytes, fields, summary);
    }

    private static string TrySummary(Func<string> summary)
    {
        try
        {
            return summary();
        }
        catch (Exception ex) when (ex is ProtocolException or FormatException)
        {
            return "invalid: " + ex.Message;
        }
    }

    /// <summary>Fields of the SocketCAN tap layout: 4-byte identifier (with flag bits), length, flags/reserved, data.</summary>
    private static IReadOnlyList<FrameField> CanFields(ReadOnlySpan<byte> tap)
    {
        if (tap.Length < 8) return [new FrameField("Data", 0, tap.Length, FrameFieldKind.Data)];
        var raw = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(tap);
        var id = (raw & 0x8000_0000) != 0 ? $"{raw & 0x1FFF_FFFF:X8}" : $"{raw & 0x7FF:X3}";
        return
        [
            new("ID", 0, 4, FrameFieldKind.Address, id),
            new("Len", 4, 1, FrameFieldKind.Length, tap[4].ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("Flags", 5, 3, FrameFieldKind.Header, (tap[5] & 4) != 0 ? "CAN FD" : "classic"),
            new("Data", 8, tap.Length - 8, FrameFieldKind.Data),
        ];
    }

    private static byte[] Pad(byte[] tap) => tap.Length is 16 or 72 ? tap : [.. tap, .. new byte[Math.Max(0, (tap.Length > 16 ? 72 : 16) - tap.Length)]];

    private static JsonObject Result(ReadOnlySpan<byte> bytes, IReadOnlyList<FrameField> fields, string summary) => new()
    {
        ["hex"] = Convert.ToHexString(bytes),
        ["summary"] = summary,
        ["fields"] = Fields(fields),
    };

    private static JsonArray Fields(IReadOnlyList<FrameField> fields) => new(fields.Select(f => (JsonNode)new JsonObject
    {
        ["name"] = f.Name, ["offset"] = f.Offset, ["length"] = f.Length, ["kind"] = f.Kind.ToString().ToLowerInvariant(), ["value"] = f.Value,
    }).ToArray());

    // ---- devices ----------------------------------------------------------------------------------------------------

    private static JsonObject Devices() => new()
    {
        ["serialPorts"] = new JsonArray(Transport.Serial.SerialTransport.GetPortNames().Select(p => (JsonNode)JsonValue.Create(p)!).ToArray()),
        ["canInterfaces"] = new JsonArray(SocketCanBus.ListInterfaces().Select(i => (JsonNode)JsonValue.Create("socketcan:" + i)!).ToArray()),
        ["simulators"] = new JsonArray(Sources.Where(s => s.StartsWith("sim:", StringComparison.Ordinal)).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
    };

    // ---- monitors ---------------------------------------------------------------------------------------------------

    private sealed class Monitor(string id) : IAsyncDisposable
    {
        public string Id { get; } = id;
        public RecordingTap Recording { get; } = new(20_000);
        public List<IAsyncDisposable> Resources { get; } = [];
        public CancellationTokenSource Cts { get; } = new();

        public async ValueTask DisposeAsync()
        {
            await Cts.CancelAsync();
            for (var i = Resources.Count - 1; i >= 0; i--)
            {
                try
                {
                    await Resources[i].DisposeAsync();
                }
                catch (Exception ex) when (ex is IoTComException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
            }
            Cts.Dispose();
        }
    }

    private async Task<JsonNode?> StartMonitorAsync(string source, CancellationToken ct)
    {
        var id = "m" + Interlocked.Increment(ref _nextMonitor).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var monitor = new Monitor(id);
        var tap = new DelegateTap(f =>
        {
            monitor.Recording.OnFrame(f);
            _ = NotifyFrameAsync(id, f);
        });
        try
        {
            await StartSourceAsync(source, monitor, tap, monitor.Cts.Token);
        }
        catch
        {
            await monitor.DisposeAsync();
            throw;
        }
        _monitors[id] = monitor;
        return new JsonObject { ["id"] = id, ["source"] = source };
    }

    private Task NotifyFrameAsync(string monitor, TrafficFrame f)
    {
        var data = f.Data.Span;
        IReadOnlyList<FrameField> fields = f.Protocol switch
        {
            "modbus-tcp" or "modbus-tcp-native" => ModbusAnatomy.Describe(data, ModbusFramingMode.Tcp, f.Direction == FrameDirection.Outbound),
            "coap" => CoapAnatomy.Describe(data),
            "mavlink" => MavlinkAnatomy.Describe(data, CommonDialect.Instance),
            "uds" or "obd2" or "uds-ecu" => UdsAnatomy.Describe(data),
            "can" or "can-slcan" => CanFields(data),
            "lorawan" or "semtech-udp" => LoRaWanAnatomy.Describe(data),
            "dlms" => DlmsAnatomy.Describe(data),
            "mbus" => MBusAnatomy.Describe(data),
            "mdns" => DnsMessage.Describe(data),
            _ => [new FrameField("Data", 0, data.Length, FrameFieldKind.Data)],
        };
        return WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "frame",
            ["params"] = new JsonObject
            {
                ["monitor"] = monitor, ["time"] = f.Timestamp.ToString("O"), ["protocol"] = f.Protocol,
                ["direction"] = f.Direction == FrameDirection.Outbound ? "out" : "in", ["endpoint"] = f.Endpoint,
                ["hex"] = Convert.ToHexString(data), ["summary"] = f.Summary, ["fields"] = Fields(fields),
            },
        });
    }

    private static async Task StartSourceAsync(string source, Monitor m, ITrafficTap tap, CancellationToken ct)
    {
        switch (source)
        {
            case "sim:modbus":
            {
                var listener = new InMemoryTransportListener("monitor");
                var store = new ModbusDataStore();
                var server = ModbusServer.Create(o => o.ListenInMemory(listener).WithStore(store));
                var sim = ModbusSimulator.CreateVirtualPlc(store, TimeSpan.FromMilliseconds(200));
                var client = ModbusClient.Create(o => o.UseInMemory(listener).WithUnitId(1).WithTap(tap).WithName("plc"));
                m.Resources.AddRange([server, sim, client]);
                await server.StartAsync(ct);
                sim.Start();
                await client.ConnectAsync(ct);
                _ = Loop(ct, TimeSpan.FromMilliseconds(500), async () =>
                {
                    await client.ReadInputRegistersAsync(0, 8, ct: ct);
                    await client.ReadCoilsAsync(0, 2, ct: ct);
                });
                break;
            }
            case "sim:can":
            {
                var net = new VirtualCanNetwork("monitor");
                var ecu = EcuSimulator.Create(net.CreateNode());
                var bus = net.CreateNode();
                var obd = ObdClient.Create(bus);
                m.Resources.AddRange([ecu, obd, bus]);
                bus.AddTap(tap);
                await ecu.StartAsync(ct);
                await bus.ConnectAsync(ct);
                await obd.ConnectAsync(ct);
                _ = Loop(ct, TimeSpan.FromSeconds(1), async () =>
                {
                    await obd.ReadPidAsync(ObdPids.EngineRpm, ct);
                    await obd.ReadPidAsync(ObdPids.VehicleSpeed, ct);
                    await obd.ReadVinAsync(ct);
                });
                break;
            }
            case "sim:coap":
            {
                var net = new InMemoryDatagramNetwork();
                var node = new IPEndPoint(IPAddress.Parse("10.0.0.40"), 5683);
                var server = CoapServer.Create(o => o.UseInMemory(net, node));
                var device = new CoapDeviceSimulator(server);
                var client = CoapClient.Create(o => o.UseInMemory(net).UseServer(node));
                m.Resources.AddRange([server, device, client]);
                client.AddTap(tap);
                await server.StartAsync(ct);
                device.Start();
                await client.ConnectAsync(ct);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var _ in client.ObserveAsync("/sensors/temperature", ct: ct)) { }
                    }
                    catch (OperationCanceledException) { }
                }, ct);
                _ = Loop(ct, TimeSpan.FromSeconds(3), async () => await client.GetAsync("/sensors/soil", ct: ct));
                break;
            }
            case "sim:mavlink":
            {
                var net = new InMemoryDatagramNetwork();
                var gcsAddress = new IPEndPoint(IPAddress.Loopback, 14550);
                var vehicle = MavlinkConnection.Create(o => { o.UseInMemory(net).SendTo(gcsAddress); (o.SystemId, o.ComponentId) = (1, 1); });
                var sim = new MavlinkVehicleSimulator(vehicle);
                var gcs = MavlinkConnection.Create(o => o.UseInMemory(net, gcsAddress));
                m.Resources.AddRange([sim, vehicle, gcs]);
                gcs.AddTap(tap);
                await vehicle.ConnectAsync(ct);
                await gcs.ConnectAsync(ct);
                sim.Start();
                break;
            }
            case "sim:dlms":
            {
                var listener = new InMemoryTransportListener("monitor-dlms");
                var server = DlmsServer.Create(o => o.ListenInMemory(listener));
                var meter = new DlmsMeterSimulator(server);
                var client = DlmsClient.Create(o => o.UseInMemory(listener));
                m.Resources.AddRange([server, meter, client]);
                client.AddTap(tap);
                await server.StartAsync(ct);
                meter.Start();
                await client.ConnectAsync(ct);
                _ = Loop(ct, TimeSpan.FromSeconds(2), async () =>
                {
                    await client.ReadRegisterAsync(ObisCode.Parse("1.0.1.7.0.255"), ct);
                    await client.ReadRegisterAsync(ObisCode.Parse("1.0.32.7.0.255"), ct);
                });
                break;
            }
            case "sim:mbus":
            {
                var listener = new InMemoryTransportListener("monitor-mbus");
                var segment = MBusSlaveSimulator.Create(o => o.ListenInMemory(listener)).AddDefaultDevices();
                var master = MBusMaster.Create(o => o.UseInMemory(listener));
                m.Resources.AddRange([segment, master]);
                master.AddTap(tap);
                await segment.StartAsync(ct);
                await master.ConnectAsync(ct);
                byte next = 0;
                _ = Loop(ct, TimeSpan.FromSeconds(2), async () => await master.ReadAsync((byte)(1 + (next++ % 3)), ct));
                break;
            }
            case "sim:lorawan":
            {
                var net = new InMemoryDatagramNetwork();
                var serverAddress = new IPEndPoint(IPAddress.Parse("10.0.0.50"), 1700);
                var server = LoRaWanNetworkServer.Create(o => { o.UseInMemory(net, serverAddress); o.Region = LoRaRegion.AS923Group2; });
                var options = LoRaWanSimulatorOptions.Demo(serverAddress);
                options.GatewayTransportFactory = () => net.Bind();
                var sim = new LoRaWanSimulator(options);
                foreach (var r in sim.Registrations) server.AddDevice(r);
                m.Resources.AddRange([server, sim]);
                server.AddTap(tap);
                await server.StartAsync(ct);
                await sim.StartAsync(ct);
                break;
            }
            default:
                if (source.StartsWith("lorawan:udp:", StringComparison.Ordinal))
                {
                    var port = int.Parse(source["lorawan:udp:".Length..], System.Globalization.CultureInfo.InvariantCulture);
                    var server = LoRaWanNetworkServer.Create(o => o.Port = port);
                    m.Resources.Add(server);
                    server.AddTap(tap);
                    await server.StartAsync(ct);
                    break;
                }
                if (source.StartsWith("can:", StringComparison.Ordinal))
                {
                    var bus = await CanBus.OpenAsync(source[4..], o => o.ListenOnly = true, ct);
                    m.Resources.Add(bus);
                    if (bus is EndpointBase endpoint) endpoint.AddTap(tap); // every CAN backend derives from CanBusBase
                    break;
                }
                if (source.StartsWith("mavlink:udp:", StringComparison.Ordinal))
                {
                    var port = int.Parse(source["mavlink:udp:".Length..], System.Globalization.CultureInfo.InvariantCulture);
                    var link = MavlinkConnection.Create(o => { o.UseUdp(port); o.SendHeartbeats = false; });
                    m.Resources.Add(link);
                    link.AddTap(tap);
                    await link.ConnectAsync(ct);
                    break;
                }
                throw new RpcError(-32602, $"unknown source '{source}' ({string.Join(", ", Sources)})");
        }
    }

    private static async Task Loop(CancellationToken ct, TimeSpan period, Func<Task> body)
    {
        using var timer = new PeriodicTimer(period);
        try
        {
            do
            {
                try
                {
                    await body();
                }
                catch (IoTComException) { }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }
    }

    private async Task<JsonNode?> StopMonitorAsync(string id)
    {
        if (!_monitors.TryRemove(id, out var m)) throw new RpcError(-32602, $"no monitor {id}");
        var frames = m.Recording.TotalFrames;
        await m.DisposeAsync();
        return new JsonObject { ["id"] = id, ["frames"] = frames };
    }

    private JsonObject SaveMonitor(string id, string path)
    {
        if (!_monitors.TryGetValue(id, out var m)) throw new RpcError(-32602, $"no monitor {id}");
        var frames = m.Recording.Snapshot();
        using (var pcap = PcapngTap.Create(path))
            foreach (var f in frames) pcap.OnFrame(f);
        return new JsonObject { ["path"] = Path.GetFullPath(path), ["frames"] = frames.Count };
    }
}
