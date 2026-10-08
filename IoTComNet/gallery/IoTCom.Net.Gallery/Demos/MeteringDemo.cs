using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Dlms;
using IoTCom.Net.Protocols.MBus;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// Smart meter reading: a household electricity meter with rooftop solar (DLMS/COSEM over HDLC) and the building's
/// M-Bus segment (heat, water and electricity sub-meters). A public client reads registers and the load profile; a
/// management client with a password switches the supply relay.
/// </summary>
public sealed partial class MeteringDemo : GalleryDemo
{
    public override string Id => "smart-metering";
    public override Text Title => new("Smart meter reading", "Pembacaan smart meter");
    public override Text Summary => new(
        "Read a household meter with rooftop solar over DLMS/COSEM — registers, the 15-minute load profile, the supply relay — and the building's heat and water meters over M-Bus.",
        "Baca meter rumah tangga dengan panel surya atap lewat DLMS/COSEM — register, load profile 15 menit, relay suplai — dan meter panas serta air gedung lewat M-Bus.");
    public override Text Docs => new(
        "A smart meter is a small database of COSEM objects, each named by an OBIS code: 1-0:1.8.0 is active energy imported, 1-0:2.8.0 exported, 1-0:32.7.0 the voltage of phase L1. A register holds an integer plus a scaler and a unit, so 2304 with scaler −1 and unit V reads 230.4 V.\n\nBefore reading anything, the client opens an association. The public client (SAP 16) needs no password and can only read; the management client (SAP 1) authenticates with a password (low-level security) or with a GMAC challenge and ciphered APDUs (high-level security) and may switch the relay. On the optical port the APDUs travel inside HDLC frames, cut into segments the meter acknowledges one by one.\n\nThe load profile keeps a row every 15 minutes. Selective access asks the meter only for the rows between two timestamps — here, the last two days — and long answers come back in blocks.\n\nIn the basement, heat and water meters hang on a two-wire M-Bus. The master pings primary addresses, then reads each slave's records: DIF says how the value is coded, VIF what it measures and in which unit.",
        "Smart meter adalah basis data kecil berisi objek COSEM yang masing-masing dinamai kode OBIS: 1-0:1.8.0 adalah energi aktif yang diimpor, 1-0:2.8.0 yang diekspor, 1-0:32.7.0 tegangan fase L1. Register menyimpan bilangan bulat beserta scaler dan unit, sehingga 2304 dengan scaler −1 dan unit V terbaca 230,4 V.\n\nSebelum membaca apa pun, client membuka asosiasi. Public client (SAP 16) tidak butuh password dan hanya bisa membaca; management client (SAP 1) mengautentikasi dengan password (low-level security) atau dengan tantangan GMAC dan APDU terenkripsi (high-level security) dan boleh memutus relay. Di port optik, APDU dibawa di dalam frame HDLC yang dipotong menjadi segmen dan di-ACK satu per satu oleh meter.\n\nLoad profile menyimpan satu baris setiap 15 menit. Selective access hanya meminta baris di antara dua timestamp — di sini, dua hari terakhir — dan jawaban panjang dikirim dalam blok.\n\nDi ruang bawah tanah, meter panas dan air tergantung pada M-Bus dua kabel. Master melakukan ping ke alamat primer, lalu membaca record setiap slave: DIF menyatakan cara nilai dikodekan, VIF apa yang diukur dan dalam unit apa.");
    public override string Category => "Lpwan";
    public override IReadOnlyList<string> Protocols => ["DLMS/COSEM", "HDLC", "OBIS", "M-Bus"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/dlms.md";

    private static readonly (string Obis, Text Label)[] Display =
    [
        ("1.0.1.8.0.255", new("Energy import, total", "Energi impor, total")),
        ("1.0.1.8.2.255", new("Energy import, WBP peak", "Energi impor, WBP")),
        ("1.0.2.8.0.255", new("Energy export (solar)", "Energi ekspor (surya)")),
        ("1.0.1.7.0.255", new("Power import", "Daya impor")),
        ("1.0.2.7.0.255", new("Power export", "Daya ekspor")),
        ("1.0.32.7.0.255", new("Voltage L1", "Tegangan L1")),
        ("1.0.14.7.0.255", new("Frequency", "Frekuensi")),
    ];

    private InMemoryTransportListener? _meterLink;
    private DlmsServer? _server;
    private DlmsMeterSimulator? _meter;
    private DlmsClient? _reader;
    private MBusSlaveSimulator? _segment;
    private MBusMaster? _master;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, RegisterValue> _values = [];
    private int _displayIndex;

    /// <summary>15-minute slots of the last two days: import (kWh) and export (kWh).</summary>
    public ObservableCollection<(DateTimeOffset Time, double Import, double Export)> Profile { get; } = [];

    /// <summary>M-Bus sub-meters.</summary>
    public ObservableCollection<SubMeterRow> SubMeters { get; } = [];

    [ObservableProperty] private string _lcdCode = "--:-.-.-";
    [ObservableProperty] private string _lcdValue = "";
    [ObservableProperty] private string _lcdUnit = "";
    [ObservableProperty] private string _lcdLabel = "";
    [ObservableProperty] private bool _lcdTariffPeak;
    [ObservableProperty] private bool _relayConnected = true;
    [ObservableProperty] private string _phases = "";
    [ObservableProperty] private string _meterTime = "";
    [ObservableProperty] private string _lastAction = "";
    [ObservableProperty] private long _apdus;
    [ObservableProperty] private bool _allowWrite;

    protected override async Task OnStartAsync()
    {
        // 1) The meter: a DLMS server on an in-memory "optical port", HDLC framing, populated by the simulator.
        _meterLink = new InMemoryTransportListener("meter");
        _server = DlmsServer.Create(o => o.ListenInMemory(_meterLink));
        _meter = new DlmsMeterSimulator(_server);
        _meter.Relay.Switched += on => Ui(() => RelayConnected = on);
        await _server.StartAsync();
        _meter.Start();

        // 2) The reader: public client, read-only, 128-byte HDLC segments.
        _reader = DlmsClient.Create(o => o.UseInMemory(_meterLink));
        _reader.AddTap(Tap);
        await _reader.ConnectAsync();

        // 3) The basement: an M-Bus segment with three sub-meters and a master.
        var busLink = new InMemoryTransportListener("mbus");
        _segment = MBusSlaveSimulator.Create(o => o.ListenInMemory(busLink)).AddDefaultDevices();
        await _segment.StartAsync();
        _master = MBusMaster.Create(o => { o.UseInMemory(busLink); o.ResponseTimeout = TimeSpan.FromMilliseconds(200); });
        _master.AddTap(Tap);
        await _master.ConnectAsync();

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        await ReadProfileAsync(ct);
        _ = Loop(TimeSpan.FromSeconds(2), ReadRegistersAsync, ct);
        _ = Loop(TimeSpan.FromSeconds(2.5), _ => { NextDisplay(); return Task.CompletedTask; }, ct);
        _ = Loop(TimeSpan.FromSeconds(60), ReadProfileAsync, ct);
        _ = Loop(TimeSpan.FromSeconds(4), ReadSubMetersAsync, ct);
        SetStatus(new Text("Public client (SAP 16) on HDLC 16 → 1/17 · M-Bus master at 2400 baud (simulated) · the relay needs the management client.",
            "Public client (SAP 16) di HDLC 16 → 1/17 · master M-Bus 2400 baud (simulasi) · relay butuh management client."));
    }

    private static async Task Loop(TimeSpan period, Func<CancellationToken, Task> body, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(period);
        try
        {
            do
            {
                try
                {
                    await body(ct);
                }
                catch (IoTComException)
                {
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReadRegistersAsync(CancellationToken ct)
    {
        if (_reader is null) return;
        foreach (var (obis, _) in Display)
            _values[obis] = await _reader.ReadRegisterAsync(ObisCode.Parse(obis), ct);
        var v = new double[3];
        var a = new double[3];
        string[] voltage = ["1.0.32.7.0.255", "1.0.52.7.0.255", "1.0.72.7.0.255"];
        string[] current = ["1.0.31.7.0.255", "1.0.51.7.0.255", "1.0.71.7.0.255"];
        for (var i = 0; i < 3; i++)
        {
            v[i] = (await _reader.ReadRegisterAsync(ObisCode.Parse(voltage[i]), ct)).Value;
            a[i] = (await _reader.ReadRegisterAsync(ObisCode.Parse(current[i]), ct)).Value;
        }

        var clock = await _reader.ReadClockAsync(ct);
        var tariff = await _reader.GetAsync(CosemClass.Data, ObisCode.Parse("0.0.96.14.0.255"), 2, ct: ct);
        Ui(() =>
        {
            Phases = string.Join('\n', Enumerable.Range(0, 3).Select(i => $"L{i + 1}  {v[i],5:0.0} V  {a[i],5:0.00} A"));
            MeterTime = clock.ToString("dd-MM-yyyy  HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            LcdTariffPeak = tariff.AsInt64() == 2;
            Apdus = Tap.Snapshot().Count;
        });
    }

    private void NextDisplay()
    {
        if (_values.Count == 0) return;
        var (obis, label) = Display[_displayIndex++ % Display.Length];
        if (!_values.TryGetValue(obis, out var value)) return;
        var code = ObisCode.Parse(obis);
        var kilo = value.UnitSymbol is "Wh" or "W" && Math.Abs(value.Value) >= 1000;
        Ui(() =>
        {
            LcdCode = $"{code.C}.{code.D}.{code.E}";
            LcdValue = kilo ? (value.Value / 1000).ToString(value.UnitSymbol == "Wh" ? "000000.00" : "0.000", System.Globalization.CultureInfo.InvariantCulture) : value.ToString().Split(' ')[0];
            LcdUnit = kilo ? "k" + value.UnitSymbol : value.UnitSymbol;
            LcdLabel = Loc.T(label);
        });
    }

    private async Task ReadProfileAsync(CancellationToken ct)
    {
        if (_reader is null) return;
        var now = await _reader.ReadClockAsync(ct);
        var table = await _reader.ReadProfileAsync(ObisCode.Parse("1.0.99.1.0.255"), now.AddDays(-2), now, ct);
        var slots = new List<(DateTimeOffset, double, double)>();
        for (var i = 1; i < table.Rows.Count; i++)
        {
            if (table.Rows[i][0].AsDateTime() is not { } t) continue;
            slots.Add((t, (table.Rows[i][1].AsDouble() - table.Rows[i - 1][1].AsDouble()) / 1000, (table.Rows[i][2].AsDouble() - table.Rows[i - 1][2].AsDouble()) / 1000));
        }

        Ui(() =>
        {
            Profile.Clear();
            foreach (var s in slots) Profile.Add(s);
        });
    }

    private async Task ReadSubMetersAsync(CancellationToken ct)
    {
        if (_master is null) return;
        var rows = new List<SubMeterRow>();
        for (byte address = 1; address <= 3; address++)
        {
            var t = await _master.ReadAsync(address, ct);
            rows.Add(new SubMeterRow(address, t));
        }

        Ui(() =>
        {
            SubMeters.Clear();
            foreach (var r in rows) SubMeters.Add(r);
        });
    }

    /// <summary>Switches the relay through a management client (LLS password) — guarded by <see cref="AllowWrite"/>.</summary>
    public async Task SwitchRelayAsync(bool connect)
    {
        if (_meterLink is null) return;
        if (!AllowWrite)
        {
            LastAction = Loc.L("Tick “allow writes” first: the relay cuts the household's supply.", "Centang “izinkan penulisan” dulu: relay memutus suplai rumah.");
            return;
        }

        try
        {
            await using var engineer = DlmsClient.Create(o => { o.UseInMemory(_meterLink).WithPassword("12345678"); o.ReadOnly = false; });
            engineer.AddTap(Tap);
            await engineer.ConnectAsync();
            await engineer.ActionAsync(CosemClass.DisconnectControl, ObisCode.Parse("0.0.96.3.10.255"), connect ? (sbyte)2 : (sbyte)1);
            LastAction = Loc.L($"Management client (SAP 1, LLS): ACTION disconnect control #{(connect ? 2 : 1)} → success.",
                $"Management client (SAP 1, LLS): ACTION disconnect control #{(connect ? 2 : 1)} → sukses.");
        }
        catch (IoTComException ex)
        {
            LastAction = ex.Message;
        }
    }

    /// <summary>Tries the same ACTION with the public client: the meter answers ReadWriteDenied.</summary>
    public async Task TryAsPublicAsync()
    {
        if (_meterLink is null) return;
        try
        {
            await using var curious = DlmsClient.Create(o => { o.UseInMemory(_meterLink); o.ReadOnly = false; });
            curious.AddTap(Tap);
            await curious.ConnectAsync();
            await curious.ActionAsync(CosemClass.DisconnectControl, ObisCode.Parse("0.0.96.3.10.255"), 1);
        }
        catch (DlmsException ex)
        {
            LastAction = Loc.L($"Public client (SAP 16): {ex.Message}", $"Public client (SAP 16): {ex.Message}");
        }
    }

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_reader is not null) await _reader.DisposeAsync();
        if (_master is not null) await _master.DisposeAsync();
        if (_segment is not null) await _segment.DisposeAsync();
        if (_meter is not null) await _meter.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
        _cts?.Dispose();
        (_reader, _master, _segment, _meter, _server, _cts, _meterLink) = (null, null, null, null, null, null, null);
        _values.Clear();
        Status = "";
    }
}

/// <summary>An M-Bus sub-meter card.</summary>
public sealed record SubMeterRow(byte Address, MBusTelegram Telegram)
{
    public string Title => $"{Telegram.MediumName} · {Address}";

    public string Identity => $"{Telegram.SecondaryAddress} · #{Telegram.AccessNumber}";

    public IReadOnlyList<string> Lines => [.. Telegram.Records.Where(r => r.StorageNumber == 0 && r.Tariff == 0).Take(5).Select(r => $"{r.Quantity}: {r.FormattedValue}")];
}
