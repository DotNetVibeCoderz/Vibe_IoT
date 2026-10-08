// IoTCom.Net benchmarks. Run: dotnet run -c Release --project benchmarks/IoTCom.Net.Benchmarks -- --filter *
using System.Buffers;
using BenchmarkDotNet.Attributes;
using IoTCom.Net;
using BenchmarkDotNet.Running;
using IoTCom.Net.Framing;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Transports;

BenchmarkSwitcher.FromAssembly(typeof(CrcBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
public class CrcBenchmarks
{
    private readonly byte[] _frame = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();

    [Benchmark(Baseline = true)] public ushort Crc16Modbus_Helper() => Crc16.Modbus(_frame);
    [Benchmark] public ulong Crc16Modbus_Catalog() => CrcCatalog.Crc16Modbus.Compute(_frame);
    [Benchmark] public ulong Crc32_Catalog() => CrcCatalog.Crc32.Compute(_frame);
    [Benchmark] public ulong Crc64Xz_Catalog() => CrcCatalog.Crc64Xz.Compute(_frame);
}

[MemoryDiagnoser]
public class FramingBenchmarks
{
    private readonly byte[] _payload = Enumerable.Range(0, 512).Select(i => (byte)(i % 7 == 0 ? 0 : i)).ToArray();
    private readonly byte[] _buffer = new byte[2048];
    private byte[] _cobsEncoded = [];

    [GlobalSetup]
    public void Setup()
    {
        var n = Cobs.Encode(_payload, _buffer);
        _cobsEncoded = _buffer[..n];
    }

    [Benchmark] public int CobsEncode() => Cobs.Encode(_payload, _buffer);
    [Benchmark] public int CobsDecode() => Cobs.Decode(_cobsEncoded, _buffer);
    [Benchmark] public int SlipEncode() => Slip.Encode(_payload, _buffer);
}

[MemoryDiagnoser]
public class ModbusCodecBenchmarks
{
    private readonly byte[] _pdu = ModbusPdu.Read(ModbusFunctionCode.ReadHoldingRegisters, 0, 10);
    private readonly ArrayBufferWriter<byte> _writer = new(512);
    private byte[] _rtuResponse = [];

    [GlobalSetup]
    public void Setup()
    {
        var response = new byte[2 + 20];
        response[0] = 3;
        response[1] = 20;
        _rtuResponse = ModbusFraming.Rtu.Encode(0, 1, response);
    }

    [Benchmark]
    public int EncodeTcp()
    {
        _writer.ResetWrittenCount();
        ModbusFraming.Tcp.Write(_writer, 1, 1, _pdu);
        return _writer.WrittenCount;
    }

    [Benchmark]
    public int EncodeRtu()
    {
        _writer.ResetWrittenCount();
        ModbusFraming.Rtu.Write(_writer, 0, 1, _pdu);
        return _writer.WrittenCount;
    }

    [Benchmark]
    public byte DecodeRtuResponse()
    {
        var seq = new ReadOnlySequence<byte>(_rtuResponse);
        ModbusFraming.Rtu.TryRead(ref seq, expectRequest: false, out var adu);
        return adu.UnitId;
    }
}

/// <summary>End-to-end request rate: client ↔ server over the in-memory transport (design target ≥ 20k req/s).</summary>
public class ModbusRoundTripBenchmarks
{
    private ModbusServer _server = null!;
    private ModbusClient _client = null!;

    [Params(1, 16)] public int Concurrency { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var link = new InMemoryTransportListener();
        _server = ModbusServer.Create(o => o.ListenInMemory(link));
        await _server.StartAsync();
        _client = ModbusClient.Create(o => o.UseInMemory(link).WithReconnect(IoTCom.Net.ReconnectPolicy.None));
        await _client.ConnectAsync();
    }

    [Benchmark(OperationsPerInvoke = 1000)]
    public async Task ReadHolding1000()
    {
        var perWorker = 1000 / Concurrency;
        await Task.WhenAll(Enumerable.Range(0, Concurrency).Select(async _ =>
        {
            for (var i = 0; i < perWorker; i++) await _client.ReadHoldingRegistersAsync(0, 10);
        }));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }
}

[MemoryDiagnoser]
public class LoRaWanBenchmarks
{
    private readonly IoTCom.Net.Protocols.LoRaWan.LoRaWanSessionKeys _keys = IoTCom.Net.Protocols.LoRaWan.LoRaWanSessionKeys.FromHex("44024241ED4CE9A68C6A8BC055233FD3", "EC925802AE430CA77FD3DD73CB2CC588");
    private readonly byte[] _payload = Enumerable.Range(0, 24).Select(i => (byte)i).ToArray();
    private byte[] _phy = [];

    [GlobalSetup]
    public void Setup() => _phy = EncodeUplink();

    /// <summary>Encrypt + MIC: what a device (or the simulator) does per uplink.</summary>
    [Benchmark]
    public byte[] EncodeUplink() => IoTCom.Net.Protocols.LoRaWan.LoRaWanPacket.EncodeData(IoTCom.Net.Protocols.LoRaWan.LoRaWanMType.UnconfirmedDataUp,
        new IoTCom.Net.Protocols.LoRaWan.DevAddr(0x26011BDA), IoTCom.Net.Protocols.LoRaWan.LoRaWanFCtrl.Create(true, adr: true), 42, [], 1, _payload, _keys);

    /// <summary>Decode + MIC check + decrypt: what the network server does per uplink.</summary>
    [Benchmark]
    public int DecodeVerifyDecrypt()
    {
        var p = IoTCom.Net.Protocols.LoRaWan.LoRaWanPacket.Decode(_phy);
        return p.VerifyMic(_keys.NwkSKey, 42) ? p.DecryptPayload(_keys, 42).Length : -1;
    }
}
