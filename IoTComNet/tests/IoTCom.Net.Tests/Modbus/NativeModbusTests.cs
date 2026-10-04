using IoTCom.Net.Native.Modbus;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Modbus;

/// <summary>Runs only when the Rust library is available (cargo build --release, or a packaged runtime).</summary>
public sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute()
    {
        if (!NativeModbusMaster.IsSupported) Skip = "iotcom_modbus native library not built (run: cargo build --release in rust/).";
    }
}

public sealed class NativeModbusTests
{
    [NativeFact]
    public void Abi_version_matches_binding() => Assert.Equal(1u, NativeModbusMaster.AbiVersion);

    [NativeFact]
    public void Rust_and_csharp_produce_identical_frames()
    {
        // Cross-language conformance: the Rust machine's wire bytes must equal the managed framing.
        foreach (var mode in new[] { ModbusFramingMode.Tcp, ModbusFramingMode.Rtu, ModbusFramingMode.Ascii })
        {
            using var master = new NativeModbusMaster(mode, TimeSpan.FromSeconds(1));
            byte[][] pdus =
            [
                ModbusPdu.Read(ModbusFunctionCode.ReadHoldingRegisters, 0x6B, 3),
                ModbusPdu.WriteMultipleCoils(0x13, [true, false, true, true, false, false, true, true, true, false]),
                ModbusPdu.WriteMultipleRegisters(1, [10, 258]),
            ];
            var buf = new byte[1024];
            for (var i = 0; i < pdus.Length; i++)
            {
                master.Submit(0, 0x11, pdus[i]);
                var n = master.PollTransmit(buf);
                if (n == 0)
                {
                    // RTU/ASCII serialise: release the bus by expiring the previous request.
                    master.HandleTimeout(10_000_000UL * (ulong)(i + 1));
                    while (master.TryPollEvent(out _)) { }
                    n = master.PollTransmit(buf);
                }
                var tid = mode == ModbusFramingMode.Tcp ? (ushort)(i + 1) : (ushort)0;
                Assert.Equal(ModbusFraming.For(mode).Encode(tid, 0x11, pdus[i]), buf[..n]);
            }
        }
    }

    [NativeFact]
    public async Task Native_client_talks_to_managed_server_tcp_and_rtu()
    {
        foreach (var mode in new[] { ModbusFramingMode.Tcp, ModbusFramingMode.Rtu })
        {
            var listener = new InMemoryTransportListener();
            await using var server = ModbusServer.Create(o => o.ListenInMemory(listener).WithStore(new ModbusDataStore(holdingRegisters: 100)).Framing = mode);
            await server.StartAsync();
            server.Store.HoldingRegisters.Write(0, [10, 20, 30]);

            var tap = new RecordingTap();
            await using var client = NativeModbusClient.Create(o => o.UseInMemory(listener).WithTap(tap).Framing = mode);
            Assert.Equal(new ushort[] { 10, 20, 30 }, await client.ReadHoldingRegistersAsync(0, 3));
            await client.WriteMultipleRegistersAsync(5, new ushort[] { 7, 8 });
            await client.WriteSingleCoilAsync(3, true);
            Assert.True(server.Store.Coils[3]);
            Assert.Equal(new ushort[] { 7, 8 }, server.Store.HoldingRegisters.Read(5, 2));

            var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(async i => (await client.ReadHoldingRegistersAsync((ushort)(i % 3), 1))[0]));
            Assert.All(results.Select((v, i) => (v, i)), x => Assert.Equal((ushort)((x.i % 3 + 1) * 10), x.v));

            var ex = await Assert.ThrowsAsync<ModbusException>(async () => await client.ReadHoldingRegistersAsync(99, 5));
            Assert.Equal(ModbusExceptionCode.IllegalDataAddress, ex.ExceptionCode);
            Assert.NotEmpty(tap.Snapshot());
        }
    }

    [NativeFact]
    public async Task Native_timeout_is_reported()
    {
        var (a, _) = InMemoryTransport.CreatePair();
        await using var client = NativeModbusClient.Create(o => o.UseInMemory(a).UseRtuFraming().WithTimeout(TimeSpan.FromMilliseconds(100)));
        await Assert.ThrowsAsync<IoTComTimeoutException>(async () => await client.ReadCoilsAsync(0, 8));
    }
}
