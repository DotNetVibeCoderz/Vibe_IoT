using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Native.Modbus;
using IoTCom.Net.Protocols.Modbus;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// Smart factory: a Modbus slave (virtual PLC) and a Modbus master talking in-process.
/// The master polls the PLC every 250 ms, and the UI writes the motor coil and the setpoint register.
/// </summary>
public sealed partial class ModbusDemo : GalleryDemo
{
    public override string Id => "modbus-factory";
    public override Text Title => new("Smart factory PLC", "PLC pabrik pintar");
    public override Text Summary => new(
        "A Modbus TCP master polls a virtual PLC: temperature, motor speed, power and production counter. Start the motor, move the setpoint and watch every request and response on the wire.",
        "Master Modbus TCP membaca PLC virtual: suhu, kecepatan motor, daya, dan penghitung produksi. Nyalakan motor, geser setpoint, dan lihat setiap request dan response di jalur.");
    public override Text Docs => new(
        "Modbus is a request/response protocol: the master asks, the slave answers. Data lives in four tables — coils (read/write bits), discrete inputs (read-only bits), input registers (read-only 16-bit) and holding registers (read/write 16-bit).\n\nThis demo uses ModbusServer + ModbusSimulator.CreateVirtualPlc as the device and ModbusClient as the master. Tick \"Rust engine\" to swap the master for NativeModbusClient, which runs the iotcom-modbus Rust state machine through the C ABI — the wire bytes are identical.\n\nRegister map: IR0 temperature×10, IR1 humidity×10, IR2 pressure, IR3 rpm, IR4-5 power kW (float32), IR6-7 energy kWh (float32), HR0 setpoint×10, HR1 counter, coil 0 motor, coil 1 pump.",
        "Modbus adalah protokol request/response: master bertanya, slave menjawab. Data disimpan di empat tabel — coil (bit baca/tulis), discrete input (bit baca saja), input register (16-bit baca saja) dan holding register (16-bit baca/tulis).\n\nDemo ini memakai ModbusServer + ModbusSimulator.CreateVirtualPlc sebagai perangkat dan ModbusClient sebagai master. Centang \"Mesin Rust\" untuk mengganti master dengan NativeModbusClient yang menjalankan state machine Rust iotcom-modbus melalui C ABI — byte di jalur identik.\n\nPeta register: IR0 suhu×10, IR1 kelembapan×10, IR2 tekanan, IR3 rpm, IR4-5 daya kW (float32), IR6-7 energi kWh (float32), HR0 setpoint×10, HR1 penghitung, coil 0 motor, coil 1 pompa.");
    public override string Category => "Industrial";
    public override IReadOnlyList<string> Protocols => ["Modbus TCP", "Rust engine"];
    public override string DocsPath => "docs/en/protocols/modbus.md";

    private InMemoryTransportListener? _listener;
    private ModbusServer? _server;
    private ModbusSimulator? _simulator;
    private IModbusClient? _client;
    private CancellationTokenSource? _poll;

    public TrendBuffer TemperatureTrend { get; } = new(160, 2);

    [ObservableProperty] private double _temperature;
    [ObservableProperty] private double _setpoint = 25;
    [ObservableProperty] private double _humidity;
    [ObservableProperty] private int _rpm;
    [ObservableProperty] private double _power;
    [ObservableProperty] private double _energy;
    [ObservableProperty] private int _counter;
    [ObservableProperty] private bool _motorRun;
    [ObservableProperty] private bool _pump;
    [ObservableProperty] private bool _highTemp;
    [ObservableProperty] private bool _useRustEngine;
    [ObservableProperty] private long _requests;

    public bool RustAvailable { get; } = NativeModbusMaster.IsSupported;

    protected override async Task OnStartAsync()
    {
        // 1) The device: a Modbus slave serving a store that the simulator animates.
        _listener = new InMemoryTransportListener("virtual-plc");
        var store = new ModbusDataStore();
        _server = ModbusServer.Create(o => o.ListenInMemory(_listener).WithStore(store).WithName("plc"));
        _simulator = ModbusSimulator.CreateVirtualPlc(store, TimeSpan.FromMilliseconds(200));
        await _server.StartAsync();
        _simulator.Start();

        // 2) The master: managed C# engine or the Rust engine — same options, same IModbusClient API.
        void Configure(ModbusClientOptions o) => o.UseInMemory(_listener).WithUnitId(1).WithTap(Tap).WithName("master");
        _client = UseRustEngine && RustAvailable ? NativeModbusClient.Create(Configure) : ModbusClient.Create(Configure);
        await _client.ConnectAsync();

        _poll = new CancellationTokenSource();
        _ = PollAsync(_poll.Token);
        SetStatus(_client is NativeModbusClient ? new Text("Polling unit 1 every 250 ms via the Rust engine.", "Membaca unit 1 tiap 250 ms lewat mesin Rust.") : new Text("Polling unit 1 every 250 ms via the C# engine.", "Membaca unit 1 tiap 250 ms lewat mesin C#."));
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var ir = await _client!.ReadInputRegistersAsync(0, 8, ct: ct);
                var hr = await _client.ReadHoldingRegistersAsync(0, 2, ct: ct);
                var coils = await _client.ReadCoilsAsync(0, 2, ct: ct);
                var temperature = ir[0] / 10.0;
                TemperatureTrend.Add(temperature);
                Ui(() =>
                {
                    Temperature = temperature;
                    Humidity = ir[1] / 10.0;
                    Rpm = ir[3];
                    Power = ModbusConvert.ToSingle(ir.AsSpan(4, 2));
                    Energy = ModbusConvert.ToSingle(ir.AsSpan(6, 2));
                    Counter = hr[1];
                    MotorRun = coils[0];
                    Pump = coils[1];
                    HighTemp = temperature > hr[0] / 10.0 + 3;
                    Requests = _server?.RequestCount ?? 0;
                });
            }
            catch (OperationCanceledException) { break; }
            catch (IoTComException ex) { Ui(() => Status = ex.Message); }
        }
    }

    /// <summary>Writes coil 0 (Write Single Coil, function 0x05).</summary>
    public async Task ToggleMotorAsync()
    {
        if (_client is null) return;
        await _client.WriteSingleCoilAsync(0, !MotorRun);
    }

    /// <summary>Writes holding register 0 (Write Single Register, function 0x06) in tenths of a degree.</summary>
    public async Task WriteSetpointAsync(double celsius)
    {
        if (_client is null) return;
        await _client.WriteSingleRegisterAsync(0, (ushort)Math.Round(celsius * 10));
    }

    protected override async Task OnStopAsync()
    {
        if (_poll is not null) await _poll.CancelAsync();
        if (_client is not null) await _client.DisposeAsync();
        if (_simulator is not null) await _simulator.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
        _poll?.Dispose();
        (_client, _simulator, _server, _poll) = (null, null, null, null);
        Status = "";
    }
}
