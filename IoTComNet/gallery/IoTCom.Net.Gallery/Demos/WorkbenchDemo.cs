using System.Buffers;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Framing;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Modbus;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>A computed checksum row.</summary>
public sealed record CrcRow(string Name, string Value, string Width);

/// <summary>
/// Protocol workbench: decode any Modbus frame field by field, and compute every CRC preset and the SLIP,
/// COBS and HDLC encodings of the same bytes. Pure codecs — no network involved.
/// </summary>
public sealed partial class WorkbenchDemo : GalleryDemo
{
    public override string Id => "workbench";
    public override Text Title => new("Frame & checksum workbench", "Meja kerja frame & checksum");
    public override Text Summary => new(
        "Paste bytes from a logic analyzer, a log or a datasheet. See the Modbus frame decoded field by field, 23 CRC presets, and SLIP, COBS and HDLC framing — all live as you type.",
        "Tempel byte dari logic analyzer, log, atau datasheet. Lihat frame Modbus diurai per field, 23 preset CRC, serta framing SLIP, COBS dan HDLC — langsung saat Anda mengetik.");
    public override Text Docs => new(
        "IoTCom.Net.Framing holds the small, stateless helpers every embedded link needs: a table-driven CRC engine for widths 8–64 with 23 catalogue presets (each verified against its check value), LRC, SLIP (RFC 1055), COBS and async-HDLC with FCS-16. Streaming decoders work directly on System.IO.Pipelines buffers.\n\nModbusAnatomy splits a frame into named fields — the same data the CLI (iotcom modbus decode) and the Gateway dashboard use to draw the frame lane.",
        "IoTCom.Net.Framing berisi helper kecil tanpa state yang dibutuhkan setiap link embedded: mesin CRC berbasis tabel untuk lebar 8–64 bit dengan 23 preset katalog (masing-masing diverifikasi dengan nilai check), LRC, SLIP (RFC 1055), COBS dan async-HDLC dengan FCS-16. Decoder streaming bekerja langsung pada buffer System.IO.Pipelines.\n\nModbusAnatomy memecah frame menjadi field bernama — data yang sama dipakai CLI (iotcom modbus decode) dan dashboard Gateway untuk menggambar frame lane.");
    public override string Category => "Workbench";
    public override IReadOnlyList<string> Protocols => ["Modbus RTU/TCP", "CRC", "SLIP", "COBS", "HDLC"];
    public override Difficulty Difficulty => Difficulty.Intermediate;
    public override string DocsPath => "docs/en/protocols/framing.md";
    public override bool AlwaysOn => true;

    [ObservableProperty] private string _input = "11 03 00 6B 00 03 76 87";
    [ObservableProperty] private int _modeIndex = 1; // 0 TCP, 1 RTU
    [ObservableProperty] private bool _isResponse;
    [ObservableProperty] private string _verdict = "";
    [ObservableProperty] private bool _valid;
    [ObservableProperty] private IReadOnlyList<FieldTiles> _lane = [];
    [ObservableProperty] private IReadOnlyList<FrameField> _fields = [];
    [ObservableProperty] private IReadOnlyList<CrcRow> _crcs = [];
    [ObservableProperty] private string _slip = "";
    [ObservableProperty] private string _cobs = "";
    [ObservableProperty] private string _hdlc = "";

    public WorkbenchDemo() => Recompute();

    partial void OnInputChanged(string value) => Recompute();
    partial void OnModeIndexChanged(int value) => Recompute();
    partial void OnIsResponseChanged(bool value) => Recompute();

    private void Recompute()
    {
        byte[] bytes;
        try
        {
            bytes = HexDump.Parse(Input);
        }
        catch (FormatException)
        {
            Verdict = "Not valid hex — use pairs like 01 03 0A FF.";
            Valid = false;
            return;
        }

        var mode = ModeIndex == 0 ? ModbusFramingMode.Tcp : ModbusFramingMode.Rtu;
        Fields = ModbusAnatomy.Describe(bytes, mode, !IsResponse);
        var row = FrameRow.From(new TrafficFrame(mode == ModbusFramingMode.Tcp ? "modbus-tcp" : "modbus-rtu",
            IsResponse ? FrameDirection.Inbound : FrameDirection.Outbound, bytes, DateTimeOffset.Now));
        Lane = row.Fields;
        Valid = ModbusFraming.For(mode).TryDecode(bytes, !IsResponse, out var adu);
        Verdict = Valid ? "✓ " + ModbusPdu.Describe(adu.UnitId, adu.Pdu, !IsResponse)
            : mode == ModbusFramingMode.Rtu ? "✗ CRC mismatch or incomplete RTU frame" : "✗ Invalid MBAP header or length";

        Crcs = CrcCatalog.All.Select(a => new CrcRow(a.Parameters.Name,
            "0x" + a.Compute(bytes).ToString("X" + (a.Width / 4).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
            a.Width.ToString(CultureInfo.InvariantCulture) + "-bit")).ToArray();
        Slip = Encode(new Slip(), bytes);
        Cobs = Encode(new Cobs(), bytes);
        Hdlc = Encode(new Hdlc(), bytes);
    }

    private static string Encode(IFrameEncoder encoder, byte[] bytes)
    {
        var w = new ArrayBufferWriter<byte>();
        encoder.Encode(bytes, w);
        return HexDump.ToHex(w.WrittenSpan);
    }

    protected override Task OnStartAsync() => Task.CompletedTask;

    protected override Task OnStopAsync() => Task.CompletedTask;
}
