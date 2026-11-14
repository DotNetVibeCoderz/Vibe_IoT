using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using IoTCom.Net.Protocols.Ocpp;
using IoTCom.Net.Transports;
using V16 = IoTCom.Net.Protocols.Ocpp.V16;
using V201 = IoTCom.Net.Protocols.Ocpp.V201;

namespace IoTCom.Net.Tests.Protocols;

public class OcppFrameTests
{
    [Fact]
    public void Call_result_and_error_round_trip()
    {
        var call = OcppFrame.Call("42", "Heartbeat", []);
        Assert.Equal("""[2,"42","Heartbeat",{}]""", call.ToJson());
        var parsed = OcppFrame.Parse(call.ToJson());
        Assert.Equal((OcppMessageType.Call, "42", "Heartbeat"), (parsed.Type, parsed.MessageId, parsed.Action));

        var error = OcppFrame.Error("42", OcppErrorCode.NotImplemented, "nope");
        Assert.Equal("""[4,"42","NotImplemented","nope",{}]""", error.ToJson());
        Assert.Throws<ArgumentException>(() => OcppFrame.Call(new string('x', 37), "Heartbeat", null));
    }

    [Fact]
    public void Typed_messages_use_ocpp_names()
    {
        var boot = OcppSession.Serialize(new V16.BootNotificationRequest { ChargePointVendor = "V", ChargePointModel = "M" }, typeof(V16.BootNotificationRequest), OcppVersion.Ocpp16);
        Assert.Equal("""{"chargePointVendor":"V","chargePointModel":"M"}""", boot.ToJsonString());

        var meter = new V16.SampledValue { Value = "1", Context = V16.ReadingContext.SamplePeriodic, Unit = "Wh" };
        var mv = OcppSession.Serialize(new V16.MeterValuesRequest { ConnectorId = 1, MeterValue = [new V16.MeterValue { Timestamp = DateTimeOffset.UnixEpoch, SampledValue = [meter] }] }, typeof(V16.MeterValuesRequest), OcppVersion.Ocpp16);
        Assert.Contains("\"context\":\"Sample.Periodic\"", mv.ToJsonString(), StringComparison.Ordinal);

        var token = OcppSession.Serialize(new V201.AuthorizeRequest { IdToken = new V201.IdToken { Value = "04A1", Type = V201.IdTokenType.ISO14443 } }, typeof(V201.AuthorizeRequest), OcppVersion.Ocpp201);
        Assert.Equal("""{"idToken":{"idToken":"04A1","type":"ISO14443"}}""", token.ToJsonString());

        // The action name and the command flag are not part of the payload.
        IOcppRequest<V16.ResetResponse> reset = new V16.ResetRequest { Type = V16.ResetType.Soft };
        Assert.Equal(("Reset", true), (reset.Action, reset.IsCommand));
        Assert.DoesNotContain("action", OcppSession.Serialize(reset, reset.GetType(), OcppVersion.Ocpp16).ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_required_fields_are_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => OcppCatalog.Normalize(OcppVersion.Ocpp16, "StartTransaction", false, JsonNode.Parse("""{"connectorId":1}""")!.AsObject()));
        Assert.ThrowsAny<JsonException>(() => OcppCatalog.Normalize(OcppVersion.Ocpp201, "Reset", false, JsonNode.Parse("""{"type":"Sometimes"}""")!.AsObject()));
    }

    [Fact]
    public void Fields_carry_utf8_byte_offsets()
    {
        const string json = """[2,"7","Heartbeat",{}]""";
        var fields = OcppFrame.DescribeFields(json);
        var action = fields.Single(f => f.Name == "Action");
        Assert.Equal("\"Heartbeat\"", json.Substring(action.Offset, action.Length));
        Assert.Equal(FrameFieldKind.Error, OcppFrame.DescribeFields("[9]").Single().Kind);
    }
}

public class OcppConformanceTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("ocpp.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Shared_vectors(string json)
    {
        var v = Conformance.Parse(json);
        var version = v.GetProperty("version").GetString() == "1.6" ? OcppVersion.Ocpp16 : OcppVersion.Ocpp201;
        var text = v.GetProperty("json").GetString()!;
        if (v.GetProperty("kind").GetString() == "payload")
        {
            var normalized = OcppCatalog.Normalize(version, v.GetProperty("action").GetString()!, v.GetProperty("response").GetBoolean(), JsonNode.Parse(text)!.AsObject());
            Assert.Equal(text, OcppFrame.Format(normalized));
            return;
        }

        var expected = v.GetProperty("expected").GetString()!;
        var ok = OcppFrame.TryParse(text, version, out var frame, out var error);
        var actual = ok
            ? frame!.Type switch
            {
                OcppMessageType.Call => $"call:{frame.MessageId}:{frame.Action}",
                OcppMessageType.CallResult => $"result:{frame.MessageId}",
                _ => $"error-frame:{frame.MessageId}:{frame.ErrorCode}",
            }
            : $"answer:{error!.MessageId ?? "null"}:{error.ErrorCode}";
        Assert.Equal(expected, actual);
        if (ok && v.GetProperty("canonical").GetBoolean()) Assert.Equal(text, frame!.ToJson());
    }
}

public sealed class OcppSessionTests : IAsyncLifetime
{
    private readonly InMemoryTransportListener _listener = new("csms");
    private OcppCsms _csms = null!;

    public async Task InitializeAsync()
    {
        _csms = OcppCsms.Create(o =>
        {
            o.ListenInMemory(_listener);
            o.AuthorizedTags = new HashSet<string> { "04A1B2C3", "APP-7731" };
            o.CallTimeout = TimeSpan.FromSeconds(5);
        });
        await _csms.StartAsync();
    }

    public async Task DisposeAsync() => await _csms.DisposeAsync();

    private OcppChargePointSimulator Simulator(OcppVersion version, string identity = "CP-1") => OcppChargePointSimulator.Create(o =>
    {
        o.Identity = identity;
        o.Version = version;
        o.MeterValueInterval = TimeSpan.FromSeconds(60);
        o.AutoReconnect = false;
        o.UseTransport(_listener.Connect);
    });

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    [Theory]
    [InlineData(OcppVersion.Ocpp16)]
    [InlineData(OcppVersion.Ocpp201)]
    public async Task Boot_registers_the_station(OcppVersion version)
    {
        await using var sim = Simulator(version);
        await sim.StartAsync();
        Assert.True(sim.Registered);
        var station = _csms.Stations["CP-1"];
        Assert.Equal((version, "Gravicode", "IoTCom EVSE-22", true), (station.Version, station.Vendor, station.Model, station.IsOnline));
        await Until(() => station.Connectors.TryGetValue(2, out var c) && c.Status == "Available");
    }

    [Theory]
    [InlineData(OcppVersion.Ocpp16)]
    [InlineData(OcppVersion.Ocpp201)]
    public async Task A_card_starts_and_unplugging_stops_a_transaction(OcppVersion version)
    {
        await using var sim = Simulator(version);
        OcppTransaction? stopped = null;
        _csms.TransactionStopped += (_, tx) => stopped = tx;
        await sim.StartAsync();
        await sim.PlugInAsync(1, socPercent: 30, batteryKWh: 60, evMaxKW: 11);
        Assert.True(await sim.PresentCardAsync(1, "04A1B2C3"));
        var station = _csms.Stations["CP-1"];
        await Until(() => station.Connectors[1].Transaction is not null);

        await sim.StepAsync(TimeSpan.FromMinutes(30));         // 11 kW for half an hour
        await Until(() => station.Connectors[1].EnergyWh >= sim.Connectors[0].MeterWh - 1);
        Assert.InRange(station.Connectors[1].Transaction!.EnergyWh, 5400, 5600);
        Assert.Equal(11_000, station.Connectors[1].PowerW);
        Assert.InRange(sim.Connectors[0].SoC, 38, 40);

        await sim.UnplugAsync(1);
        await Until(() => stopped is not null);
        Assert.Equal("EVDisconnected", stopped!.StopReason);
        Assert.Null(station.Connectors[1].Transaction);
    }

    [Fact]
    public async Task Unknown_tags_are_refused()
    {
        await using var sim = Simulator(OcppVersion.Ocpp16);
        await sim.StartAsync();
        await sim.PlugInAsync(1);
        Assert.False(await sim.PresentCardAsync(1, "DEADBEEF"));
        Assert.Null(sim.Connectors[0].TransactionId);
    }

    [Theory]
    [InlineData(OcppVersion.Ocpp16)]
    [InlineData(OcppVersion.Ocpp201)]
    public async Task Commands_need_the_write_lock_open(OcppVersion version)
    {
        await using var sim = Simulator(version);
        await sim.StartAsync();
        await sim.PlugInAsync(2, evMaxKW: 22);
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => _csms.RemoteStartAsync("CP-1", 2, "APP-7731"));
        if (version == OcppVersion.Ocpp16)
            await Assert.ThrowsAsync<ReadOnlyModeException>(() => _csms.Session("CP-1").CallAsync(new V16.ResetRequest { Type = V16.ResetType.Soft }));
        else
            await Assert.ThrowsAsync<ReadOnlyModeException>(() => _csms.Session("CP-1").CallAsync(new V201.ResetRequest { Type = V201.ResetType.OnIdle }));

        _csms.ReadOnly = false;
        Assert.True(await _csms.RemoteStartAsync("CP-1", 2, "APP-7731"));
        var station = _csms.Stations["CP-1"];
        await Until(() => station.Connectors[2].Transaction is not null);

        Assert.Equal("Accepted", await _csms.SetPowerLimitAsync("CP-1", 2, 7400));
        await sim.StepAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(7400, sim.Connectors[1].PowerW);

        Assert.True(await _csms.RemoteStopAsync("CP-1", station.Connectors[2].Transaction!.Id));
        await Until(() => station.Connectors[2].Transaction is null);
    }

    [Fact]
    public async Task Reads_are_allowed_in_read_only_mode()
    {
        await using var sim16 = Simulator(OcppVersion.Ocpp16, "AC-1");
        await using var sim201 = Simulator(OcppVersion.Ocpp201, "DC-1");
        await sim16.StartAsync();
        await sim201.StartAsync();

        var config16 = await _csms.ReadConfigurationAsync("AC-1");
        Assert.Contains(config16, c => c.Key == "NumberOfConnectors" && c.Value == "2" && c.ReadOnly);
        var config201 = await _csms.ReadConfigurationAsync("DC-1");
        Assert.Contains(config201, c => c.Key == "OCPPCommCtrlr/HeartbeatInterval" && c.Value == "300");
        Assert.Equal("Accepted", await _csms.TriggerAsync("AC-1", "Heartbeat"));
        await Until(() => _csms.Stations["AC-1"].LastHeartbeat is not null);

        await Assert.ThrowsAsync<ReadOnlyModeException>(() => _csms.WriteConfigurationAsync("AC-1", "HeartbeatInterval", "60"));
        _csms.ReadOnly = false;
        Assert.Equal("Accepted", await _csms.WriteConfigurationAsync("AC-1", "HeartbeatInterval", "60"));
        Assert.Equal("Rejected", await _csms.WriteConfigurationAsync("AC-1", "NumberOfConnectors", "4"));
        Assert.Equal("Accepted", await _csms.WriteConfigurationAsync("DC-1", "OCPPCommCtrlr/HeartbeatInterval", "90"));
        Assert.Equal("90", sim201.Configuration["OCPPCommCtrlr/HeartbeatInterval"].Value);
    }

    [Fact]
    public async Task Unknown_actions_and_bad_payloads_get_rpc_errors()
    {
        await using var sim = Simulator(OcppVersion.Ocpp16);
        await sim.StartAsync();
        var session = _csms.Session("CP-1");
        var e1 = await Assert.ThrowsAsync<OcppCallException>(() => session.CallAsync("GetDiagnostics", []));
        Assert.Equal(OcppErrorCode.NotImplemented, e1.ErrorCode);
        var e2 = await Assert.ThrowsAsync<OcppCallException>(() => session.CallAsync("Reset", new JsonObject { ["type"] = "Sometimes" }));
        Assert.Equal(OcppErrorCode.FormationViolation, e2.ErrorCode);
        var e3 = await Assert.ThrowsAsync<OcppCallException>(() => session.CallAsync("UnlockConnector", []));
        Assert.Equal(OcppErrorCode.OccurenceConstraintViolation, e3.ErrorCode);
    }

    [Fact]
    public async Task A_reset_reboots_and_reconnects()
    {
        await using var sim = Simulator(OcppVersion.Ocpp16);
        var reset = new TaskCompletionSource<string>();
        sim.Reset += k => reset.TrySetResult(k);
        await sim.StartAsync();
        _csms.ReadOnly = false;
        Assert.Equal("Accepted", await _csms.ResetAsync("CP-1", hard: true));
        Assert.Equal("HardReset", await reset.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await Until(() => _csms.Stations["CP-1"].IsOnline && _csms.Stations["CP-1"].Booted);
    }

    [Fact]
    public async Task A_dropped_link_reconnects()
    {
        await using var sim = OcppChargePointSimulator.Create(o =>
        {
            o.Identity = "CP-9";
            o.ReconnectDelay = TimeSpan.FromMilliseconds(50);
            o.UseTransport(_listener.Connect);
        });
        var disconnected = new TaskCompletionSource();
        _csms.StationDisconnected += s => { if (s.Identity == "CP-9") disconnected.TrySetResult(); };
        await sim.StartAsync();
        await sim.DropConnectionAsync();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => sim.Registered && _csms.Stations["CP-9"].IsOnline);
    }
}

public class OcppWebSocketTests
{
    [Fact]
    public async Task ClientWebSocket_talks_to_the_csms_over_tcp_with_basic_auth()
    {
        await using var csms = OcppCsms.Create(o =>
        {
            o.UseTcp(IPAddress.Loopback, 0);
            o.ValidatePassword = (identity, password) => identity == "CP-TCP" && password == "s3cret-pass";
        });
        await csms.StartAsync();
        var port = ((IPEndPoint)IPEndPoint.Parse(csms.LocalAddress!)).Port;

        await using var good = OcppChargePoint.Create(o =>
        {
            o.UseCsms($"ws://127.0.0.1:{port}/ocpp");
            o.Identity = "CP-TCP";
            o.Version = OcppVersion.Ocpp201;
            o.Password = "s3cret-pass";
        });
        await good.ConnectAsync();
        var boot = await good.CallAsync(new V201.BootNotificationRequest
        {
            Reason = V201.BootReason.PowerUp,
            ChargingStation = new V201.ChargingStation { VendorName = "Gravicode", Model = "DC-60" },
        });
        Assert.Equal(V201.RegistrationStatus.Accepted, boot.Status);
        Assert.Equal(OcppVersion.Ocpp201, csms.Stations["CP-TCP"].Version);

        await using var bad = OcppChargePoint.Create(o =>
        {
            o.UseCsms($"ws://127.0.0.1:{port}/ocpp/");
            o.Identity = "CP-TCP";
            o.Password = "wrong";
        });
        await Assert.ThrowsAnyAsync<IoTComException>(async () => await bad.ConnectAsync());
    }

    [Fact]
    public async Task Versions_must_overlap()
    {
        var listener = new InMemoryTransportListener("csms16");
        await using var csms = OcppCsms.Create(o =>
        {
            o.ListenInMemory(listener);
            o.SupportedVersions.Remove(OcppVersion.Ocpp201);
        });
        await csms.StartAsync();
        await using var cp = OcppChargePoint.Create(o =>
        {
            o.UseTransport(listener.Connect);
            o.Version = OcppVersion.Ocpp201;
        });
        var ex = await Assert.ThrowsAnyAsync<IoTComException>(async () => await cp.ConnectAsync());
        Assert.Contains("400", ex.Message, StringComparison.Ordinal);
    }
}
