using System.Net;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Modbus;

public class ModbusFramingTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("modbus.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Encode_and_decode_conformance(string json)
    {
        var v = Conformance.Parse(json);
        var mode = v.GetProperty("mode").GetString() switch { "tcp" => ModbusFramingMode.Tcp, "rtu" => ModbusFramingMode.Rtu, _ => ModbusFramingMode.Ascii };
        var framing = ModbusFraming.For(mode);
        var tid = (ushort)v.GetProperty("transactionId").GetInt32();
        var unit = (byte)v.GetProperty("unitId").GetInt32();
        var pdu = v.Hex("pdu");
        var adu = v.Hex("adu");

        Assert.Equal(adu, framing.Encode(tid, unit, pdu));
        Assert.True(framing.TryDecode(adu, v.GetProperty("direction").GetString() == "request", out var decoded));
        Assert.Equal(unit, decoded.UnitId);
        Assert.Equal(tid, decoded.TransactionId);
        Assert.Equal(pdu, decoded.Pdu);
    }

    [Fact]
    public void Rtu_resynchronises_after_line_noise()
    {
        var good = ModbusFraming.Rtu.Encode(0, 1, ModbusPdu.Read(ModbusFunctionCode.ReadHoldingRegisters, 0, 2));
        byte[] wire = [0x55, 0xAA, .. good];
        var seq = new System.Buffers.ReadOnlySequence<byte>(wire);
        ModbusAdu adu;
        IoTCom.Net.Framing.FrameDecodeStatus status;
        do status = ModbusFraming.Rtu.TryRead(ref seq, expectRequest: true, out adu);
        while (status == IoTCom.Net.Framing.FrameDecodeStatus.Invalid);
        Assert.Equal(IoTCom.Net.Framing.FrameDecodeStatus.Frame, status);
        Assert.Equal(1, adu.UnitId);
    }
}

public class ModbusConvertTests
{
    [Theory]
    [InlineData(ModbusWordOrder.BigEndian, 0x4049, 0x0FDB)]
    [InlineData(ModbusWordOrder.WordSwap, 0x0FDB, 0x4049)]
    [InlineData(ModbusWordOrder.ByteSwap, 0x4940, 0xDB0F)]
    [InlineData(ModbusWordOrder.LittleEndian, 0xDB0F, 0x4940)]
    public void Float_word_orders(ModbusWordOrder order, int r0, int r1)
    {
        var regs = new ushort[2];
        ModbusConvert.FromSingle(MathF.PI, regs, order);
        Assert.Equal(new[] { (ushort)r0, (ushort)r1 }, regs);
        Assert.Equal(MathF.PI, ModbusConvert.ToSingle(regs, order));
    }

    [Fact]
    public void Double_and_string_roundtrip()
    {
        var regs = new ushort[4];
        ModbusConvert.FromDouble(-1234.5678, regs, ModbusWordOrder.WordSwap);
        Assert.Equal(-1234.5678, ModbusConvert.ToDouble(regs, ModbusWordOrder.WordSwap));
        Assert.Equal("IoT", ModbusConvert.ToAsciiString([0x496F, 0x5400]));
    }
}

public sealed class ModbusClientServerTests
{
    private static (ModbusServer Server, ModbusClient Client) CreateInMemory(ModbusFramingMode mode = ModbusFramingMode.Tcp, Action<ModbusClientOptions>? client = null, Action<ModbusServerOptions>? server = null)
    {
        var listener = new InMemoryTransportListener();
        var srv = ModbusServer.Create(o =>
        {
            o.ListenInMemory(listener).Framing = mode;
            server?.Invoke(o);
        });
        var cli = ModbusClient.Create(o =>
        {
            o.UseInMemory(listener).WithTimeout(TimeSpan.FromSeconds(2)).Framing = mode;
            client?.Invoke(o);
        });
        return (srv, cli);
    }

    [Theory]
    [InlineData(ModbusFramingMode.Tcp)]
    [InlineData(ModbusFramingMode.Rtu)]
    [InlineData(ModbusFramingMode.Ascii)]
    public async Task All_functions_roundtrip(ModbusFramingMode mode)
    {
        var (server, client) = CreateInMemory(mode);
        await using var _ = server;
        await using var __ = client;
        await server.StartAsync();

        await client.WriteSingleRegisterAsync(10, 1234);
        await client.WriteMultipleRegistersAsync(20, new ushort[] { 1, 2, 3, 65535 });
        await client.WriteSingleCoilAsync(5, true);
        await client.WriteMultipleCoilsAsync(100, new[] { true, false, true, true, false, false, false, false, true });
        server.Store.InputRegisters.Write(0, [11, 22]);
        server.Store.DiscreteInputs[3] = true;

        Assert.Equal(new ushort[] { 1234 }, await client.ReadHoldingRegistersAsync(10, 1));
        Assert.Equal(new ushort[] { 1, 2, 3, 65535 }, await client.ReadHoldingRegistersAsync(20, 4));
        Assert.Equal(new ushort[] { 11, 22 }, await client.ReadInputRegistersAsync(0, 2));
        Assert.True((await client.ReadCoilsAsync(5, 1))[0]);
        Assert.Equal(new[] { true, false, true, true, false, false, false, false, true }, await client.ReadCoilsAsync(100, 9));
        Assert.Equal(new[] { false, true }, await client.ReadDiscreteInputsAsync(2, 2));

        await client.MaskWriteRegisterAsync(10, 0x00F2, 0x0025);
        Assert.Equal((ushort)((1234 & 0x00F2) | (0x0025 & ~0x00F2)), server.Store.HoldingRegisters[10]);

        var rw = await client.ReadWriteMultipleRegistersAsync(20, 2, 20, new ushort[] { 9, 8 });
        Assert.Equal(new ushort[] { 9, 8 }, rw);

        var id = await client.ReadDeviceIdentificationAsync();
        Assert.Equal("Gravicode Studios", id.VendorName);
    }

    [Fact]
    public async Task Exception_responses_surface_as_ModbusException()
    {
        var (server, client) = CreateInMemory(server: o => o.Store = new ModbusDataStore(holdingRegisters: 100));
        await using var _ = server;
        await using var __ = client;
        await server.StartAsync();

        var ex = await Assert.ThrowsAsync<ModbusException>(async () => await client.ReadHoldingRegistersAsync(99, 5));
        Assert.Equal(ModbusExceptionCode.IllegalDataAddress, ex.ExceptionCode);
        Assert.Equal(ModbusFunctionCode.ReadHoldingRegisters, ex.Function);

        var ex2 = await Assert.ThrowsAsync<ModbusException>(async () => await client.SendAsync(new byte[] { 0x41 }));
        Assert.Equal(ModbusExceptionCode.IllegalFunction, ex2.ExceptionCode);
    }

    [Fact]
    public async Task Read_only_client_blocks_writes_without_touching_the_wire()
    {
        var (server, client) = CreateInMemory(client: o => o.AsReadOnly());
        await using var _ = server;
        await using var __ = client;
        await server.StartAsync();
        await Assert.ThrowsAsync<ReadOnlyModeException>(async () => await client.WriteSingleRegisterAsync(0, 1));
        Assert.Equal(0, server.RequestCount);
        await client.ReadHoldingRegistersAsync(0, 1);
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public async Task Pipelined_tcp_requests_complete_out_of_order_safely()
    {
        var (server, client) = CreateInMemory();
        await using var _ = server;
        await using var __ = client;
        await server.StartAsync();
        for (ushort i = 0; i < 200; i++) server.Store.HoldingRegisters[i] = (ushort)(i * 3);
        var tasks = Enumerable.Range(0, 200).Select(async i => (i, (await client.ReadHoldingRegistersAsync((ushort)i, 1))[0])).ToArray();
        foreach (var (i, value) in await Task.WhenAll(tasks)) Assert.Equal((ushort)(i * 3), value);
    }

    [Fact]
    public async Task Unknown_unit_times_out_on_rtu_and_gets_gateway_error_on_tcp()
    {
        var (server, client) = CreateInMemory(ModbusFramingMode.Rtu, c => c.WithTimeout(TimeSpan.FromMilliseconds(150)), s => s.WithUnitIds(1));
        await using var _ = server;
        await using var __ = client;
        await server.StartAsync();
        await Assert.ThrowsAsync<IoTComTimeoutException>(async () => await client.ReadHoldingRegistersAsync(0, 1, unitId: 7));
        Assert.Single(await client.ReadHoldingRegistersAsync(0, 1, unitId: 1)); // bus still usable

        var (s2, c2) = CreateInMemory(ModbusFramingMode.Tcp, server: s => s.WithUnitIds(1));
        await using var ___ = s2;
        await using var ____ = c2;
        await s2.StartAsync();
        var ex = await Assert.ThrowsAsync<ModbusException>(async () => await c2.ReadHoldingRegistersAsync(0, 1, unitId: 7));
        Assert.Equal(ModbusExceptionCode.GatewayTargetFailedToRespond, ex.ExceptionCode);
    }

    [Fact]
    public async Task Real_tcp_sockets_and_traffic_tap()
    {
        await using var server = ModbusServer.Create(o => o.UseTcp(IPAddress.Loopback, 0));
        await server.StartAsync();
        var port = int.Parse(server.LocalAddress!.Split(':')[^1], System.Globalization.CultureInfo.InvariantCulture);
        var tap = new RecordingTap();
        await using var client = ModbusClient.Create(o => o.UseTcp("127.0.0.1", port).WithTap(tap));
        server.Store.HoldingRegisters[0] = 42;
        Assert.Equal(42, (await client.ReadHoldingRegistersAsync(0, 1))[0]);
        Assert.Equal(EndpointState.Connected, client.State);
        var frames = tap.Snapshot();
        Assert.Equal(2, frames.Count);
        Assert.Equal(FrameDirection.Outbound, frames[0].Direction);
        Assert.Contains("ReadHoldingRegisters", frames[0].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_reconnects_after_server_restart()
    {
        var port = GetFreePort();
        await using var client = ModbusClient.Create(o => o
            .UseTcp("127.0.0.1", port)
            .WithReconnect(ReconnectPolicy.Default with { InitialDelay = TimeSpan.FromMilliseconds(10), Jitter = 0 }));

        var server = ModbusServer.Create(o => o.UseTcp(IPAddress.Loopback, port));
        await server.StartAsync();
        await client.ReadHoldingRegistersAsync(0, 1);
        await server.DisposeAsync();

        var server2 = ModbusServer.Create(o => o.UseTcp(IPAddress.Loopback, port));
        await using var _ = server2;
        await server2.StartAsync();
        server2.Store.HoldingRegisters[0] = 7;

        ushort value = 0;
        for (var i = 0; i < 50 && value != 7; i++)
        {
            try { value = (await client.ReadHoldingRegistersAsync(0, 1))[0]; }
            catch (IoTComException) { await Task.Delay(50); }
        }
        Assert.Equal(7, value);
    }

    [Fact]
    public void Virtual_plc_simulator_is_deterministic_and_reacts_to_coils()
    {
        var store = new ModbusDataStore();
        var sim = ModbusSimulator.CreateVirtualPlc(store);
        for (var i = 0; i < 40; i++) sim.Tick(0.25);
        Assert.InRange(store.InputRegisters[0] / 10.0, 20, 30);
        Assert.True(store.InputRegisters[3] > 1000);
        Assert.True(store.HoldingRegisters[1] >= 15);
        Assert.Equal("IoTCom.Net Virtual PLC", ModbusConvert.ToAsciiString(store.HoldingRegisters.Read(10, 12)));

        store.Coils[0] = false; // stop the motor
        for (var i = 0; i < 40; i++) sim.Tick(0.25);
        Assert.True(store.InputRegisters[3] < 50);
    }

    private static int GetFreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
