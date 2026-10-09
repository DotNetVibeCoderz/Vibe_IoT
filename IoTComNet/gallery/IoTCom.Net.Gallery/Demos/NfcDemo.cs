using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Nfc;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// A maintenance round with NFC asset tags: tap a tag on the (virtual) reader to read its NDEF records and see its
/// memory page by page; log a service visit back onto the tag when writing is unlocked.
/// </summary>
public sealed partial class NfcDemo : GalleryDemo
{
    public override string Id => "nfc-asset-tags";
    public override Text Title => new("NFC asset tags", "Tag aset NFC");
    public override Text Summary => new(
        "A maintenance round with NFC tags on pumps, valves and switchboards: tap a tag to read its NDEF records, see every page of its NTAG213 memory, and log a service visit back onto the tag.",
        "Ronde perawatan dengan tag NFC pada pompa, katup, dan panel: tempelkan tag untuk membaca record NDEF-nya, lihat setiap halaman memori NTAG213-nya, dan catat kunjungan servis kembali ke tag.");
    public override Text Docs => new(
        "NFC tags carry NDEF messages: a list of records, each with a type and a payload — a URI to the equipment manual, a text with the asset number, a Smart Poster combining both, Wi-Fi credentials for commissioning, or an app link. Phones open them without an app; a PC reads them through a PC/SC contactless reader such as the ACR122U.\n\nThe common NTAG213 is an NFC Forum Type 2 tag: 45 pages of four bytes. Pages 0–2 hold the 7-byte serial number with check bytes and the lock bytes, page 3 the capability container (magic E1, version, data size, write access), and from page 4 the data area holds TLVs: 03 with the NDEF message, FE as terminator. The last pages hold the configuration and dynamic locks.\n\nReaders talk to the tag with the PC/SC storage-card commands: GET DATA for the UID, READ BINARY for four pages, UPDATE BINARY for one page. IoTCom.Net writes only the pages that change, empties the NDEF TLV first and writes its header last, so a tag pulled away mid-write never holds half a message. Writes are off unless allowed, and a tag whose capability container says read-only is refused.\n\nHere the reader is IoTCom.Net's VirtualNfcReader with simulated NTAG213 tags; PcscNfcReader.Open() uses a real contactless reader with the same Type2TagClient.",
        "Tag NFC membawa pesan NDEF: daftar record, masing-masing dengan tipe dan payload — URI ke manual peralatan, teks dengan nomor aset, Smart Poster yang menggabungkan keduanya, kredensial Wi-Fi untuk commissioning, atau tautan aplikasi. Ponsel membukanya tanpa aplikasi; PC membacanya lewat pembaca contactless PC/SC seperti ACR122U.\n\nNTAG213 yang umum adalah tag NFC Forum Type 2: 45 halaman berisi empat byte. Halaman 0–2 menyimpan nomor seri 7 byte dengan byte pemeriksa dan byte kunci, halaman 3 capability container (magic E1, versi, ukuran data, akses tulis), dan mulai halaman 4 area data berisi TLV: 03 dengan pesan NDEF, FE sebagai terminator. Halaman terakhir menyimpan konfigurasi dan kunci dinamis.\n\nPembaca berbicara dengan tag memakai perintah storage-card PC/SC: GET DATA untuk UID, READ BINARY untuk empat halaman, UPDATE BINARY untuk satu halaman. IoTCom.Net hanya menulis halaman yang berubah, mengosongkan TLV NDEF lebih dulu dan menulis header-nya terakhir, sehingga tag yang ditarik di tengah penulisan tidak pernah berisi setengah pesan. Penulisan mati kecuali diizinkan, dan tag yang capability container-nya menyatakan hanya-baca ditolak.\n\nDi sini pembacanya adalah VirtualNfcReader milik IoTCom.Net dengan tag NTAG213 simulasi; PcscNfcReader.Open() memakai pembaca contactless sungguhan dengan Type2TagClient yang sama.");
    public override string Category => "Workbench";
    public override IReadOnlyList<string> Protocols => ["NFC Forum Type 2", "NDEF", "PC/SC"];
    public override Difficulty Difficulty => Difficulty.Beginner;
    public override string DocsPath => "docs/en/protocols/nfc.md";

    private readonly VirtualNfcReader _reader = new();

    /// <summary>The tags on the round.</summary>
    public ObservableCollection<VirtualType2Tag> Tags { get; } = [];

    /// <summary>Records of the tag on the reader.</summary>
    public ObservableCollection<string> Records { get; } = [];

    [ObservableProperty] private VirtualType2Tag? _onReader;
    [ObservableProperty] private string _uid = "–";
    [ObservableProperty] private string _capability = "";
    [ObservableProperty] private string _usage = "";
    [ObservableProperty] private bool _writesAllowed;

    /// <summary>Memory of the tag on the reader and its region map.</summary>
    public (byte[] Memory, Type2Region[] Map)? Memory { get; private set; }

    /// <summary>The NDEF bytes for the frame lane.</summary>
    public byte[] Ndef { get; private set; } = [];

    /// <summary>Raised when the page map and frame lane should redraw.</summary>
    public event Action? Changed;

    protected override async Task OnStartAsync()
    {
        Tags.Clear();
        Records.Clear();
        Log.Clear();
        Tags.Add(new VirtualType2Tag(Type2TagKind.Ntag213, [0x04, 0x51, 0x7A, 0x22, 0x9C, 0x61, 0x80],
            new NdefMessage([NdefRecord.SmartPoster("https://docs.example.com/p7", "Pump P-0007 manual"), NdefRecord.Text("Asset P-0007 · 7.5 kW")]), label: "Pump P-0007"));
        Tags.Add(new VirtualType2Tag(Type2TagKind.Ntag213, [0x04, 0x51, 0x7A, 0x22, 0x9C, 0x62, 0x80],
            new NdefMessage([NdefRecord.Uri("https://docs.example.com/v12"), NdefRecord.Text("Valve V-12 · DN80")]), label: "Valve V-12"));
        Tags.Add(new VirtualType2Tag(Type2TagKind.Ntag213, [0x04, 0x51, 0x7A, 0x22, 0x9C, 0x63, 0x80],
            new NdefMessage([NdefRecord.Text("Switchboard MCC-3 · 400 V · do not open live"), NdefRecord.AndroidApp("com.example.maintenance")]), writable: false, label: "Switchboard MCC-3 (locked)"));
        await TapAsync(Tags[0]);
        if (Environment.GetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT") == "1")
        {
            WritesAllowed = true;
            await LogServiceAsync();
        }

        SetStatus(new Text("Virtual PC/SC contactless reader · NTAG213 tags (144-byte data area) · writes off until allowed.",
            "Pembaca contactless PC/SC virtual · tag NTAG213 (area data 144 byte) · penulisan mati sampai diizinkan."));
    }

    /// <summary>Puts a tag on the reader and reads it.</summary>
    public async Task TapAsync(VirtualType2Tag tag)
    {
        _reader.Remove();
        _reader.Present(tag);
        OnReader = tag;
        await ReadAsync();
    }

    private async Task ReadAsync()
    {
        await using var card = await _reader.WaitForTagAsync();
        var client = new Type2TagClient(card);
        var uid = await client.GetUidAsync();
        var (cc, data) = await client.ReadDataAreaAsync();
        var message = Type2Tag.ReadNdef(data);
        var dump = await client.DumpAsync();
        Tap.OnFrame(new TrafficFrame("ndef", FrameDirection.Inbound, message?.Encode() ?? [], DateTimeOffset.UtcNow, _reader.Name, message?.ToString()));
        Ui(() =>
        {
            Uid = Convert.ToHexString(uid);
            Capability = string.Create(CultureInfo.InvariantCulture, $"CC {Convert.ToHexString(cc)} · NDEF {cc[1] >> 4}.{cc[1] & 0xF} · {cc[2] * 8} bytes · {((cc[3] & 0x0F) == 0 ? "writable" : "read-only")}");
            Ndef = message?.Encode() ?? [];
            var used = Ndef.Length + (Ndef.Length < 0xFF ? 3 : 5);
            Usage = string.Create(CultureInfo.InvariantCulture, $"{used} of {cc[2] * 8} bytes used");
            Records.Clear();
            foreach (var r in message?.Records ?? []) Records.Add(r.ToString());
            Memory = (dump, Type2Tag.Map(dump, cc[2] * 8));
            AddLog($"read {OnReader?.Label}: {Records.Count} record(s), UID {Uid}");
            Changed?.Invoke();
        });
    }

    /// <summary>Adds a "serviced" text record to the tag on the reader (when writes are allowed).</summary>
    public async Task LogServiceAsync()
    {
        if (OnReader is null) return;
        await using var card = await _reader.WaitForTagAsync();
        var client = new Type2TagClient(card, WritesAllowed ? new NfcTagOptions().AllowWrites() : new NfcTagOptions());
        try
        {
            var current = await client.ReadNdefAsync() ?? new NdefMessage([]);
            var records = current.Records.Where(r => !(r.TryGetText(out var t, out _) && t.StartsWith("Serviced ", StringComparison.Ordinal))).ToList();
            records.Add(NdefRecord.Text(string.Create(CultureInfo.InvariantCulture, $"Serviced {DateTime.Now:yyyy-MM-dd} · tech 14")));
            var before = OnReader.PagesWritten;
            await client.WriteNdefAsync(new NdefMessage(records));
            Ui(() => AddLog($"wrote {OnReader.PagesWritten - before} page(s) to {OnReader.Label}"));
        }
        catch (Exception ex) when (ex is ReadOnlyModeException or DeviceException or ArgumentException)
        {
            Ui(() => AddLog($"write refused: {ex.Message.Split(" (Parameter", 2)[0]}"));
        }

        await ReadAsync();
    }

    protected override Task OnStopAsync()
    {
        _reader.Remove();
        (OnReader, Memory, Ndef) = (null, null, []);
        WritesAllowed = false;
        Status = "";
        return Task.CompletedTask;
    }
}
