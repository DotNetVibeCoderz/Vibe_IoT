using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.AtCommand;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// A truck's telematics unit drives its LTE-M module with AT commands: identity, signal and registration polled, URCs
/// (+CEREG, +CMTI) decoded as they arrive, SMS read and sent; the truck drives through a tunnel and across a border.
/// </summary>
public sealed partial class CellularModemDemo : GalleryDemo
{
    public override string Id => "at-cellular";
    public override Text Title => new("Cellular module over AT commands", "Modul seluler lewat perintah AT");
    public override Text Summary => new(
        "A truck's telematics unit talks to its LTE-M module with AT commands: watch signal and registration, see +CEREG and +CMTI arrive between answers, type commands in a terminal, read the dispatcher's SMS and reply — then drive into a tunnel.",
        "Unit telematika truk berbicara dengan modul LTE-M-nya lewat perintah AT: pantau sinyal dan registrasi, lihat +CEREG dan +CMTI tiba di sela jawaban, ketik perintah di terminal, baca SMS dispatcher dan balas — lalu masuk terowongan.");
    public override Text Docs => new(
        "Cellular and GNSS modules (Quectel, SIMCom, u-blox, Nordic…) are driven over a UART with AT commands from 3GPP TS 27.007 and 27.005. The host sends one command line, and the module answers with information lines and a final result code: OK, ERROR, or +CME ERROR / +CMS ERROR with a number. AT+CSQ reports the signal (rssi 0–31, −113 dBm + 2 dBm per step), AT+CEREG? the LTE registration, AT+COPS? the operator.\n\nThe hard part is that the module also speaks on its own. Unsolicited result codes (URCs) such as +CEREG: 1 when the module registers, or +CMTI: \"ME\",3 when an SMS arrives, can appear at any moment — even between a command and its OK. AtParser separates them from command answers so neither gets lost, and AtModem delivers them as a stream.\n\nSMS in text mode (AT+CMGF=1) is read with AT+CMGR and sent with AT+CMGS followed by a > prompt, the text and Ctrl+Z. Here the module is an AtModemSimulator (an LTE-M modem on an in-memory serial link) and the telematics unit is an AtModem; sending SMS is behind a write lock.",
        "Modul seluler dan GNSS (Quectel, SIMCom, u-blox, Nordic…) dikendalikan lewat UART dengan perintah AT dari 3GPP TS 27.007 dan 27.005. Host mengirim satu baris perintah, dan modul menjawab dengan baris informasi serta result code akhir: OK, ERROR, atau +CME ERROR / +CMS ERROR dengan sebuah angka. AT+CSQ melaporkan sinyal (rssi 0–31, −113 dBm + 2 dBm per langkah), AT+CEREG? registrasi LTE, AT+COPS? operator.\n\nBagian sulitnya, modul juga berbicara sendiri. Unsolicited result code (URC) seperti +CEREG: 1 saat modul terdaftar, atau +CMTI: \"ME\",3 saat SMS tiba, dapat muncul kapan saja — bahkan di antara perintah dan OK-nya. AtParser memisahkannya dari jawaban perintah agar tidak ada yang hilang, dan AtModem menyampaikannya sebagai aliran.\n\nSMS mode teks (AT+CMGF=1) dibaca dengan AT+CMGR dan dikirim dengan AT+CMGS diikuti prompt >, teks, dan Ctrl+Z. Di sini modulnya adalah AtModemSimulator (modem LTE-M pada link serial dalam memori) dan unit telematikanya adalah AtModem; pengiriman SMS ada di balik kunci tulis.");
    public override string Category => "Lpwan";
    public override IReadOnlyList<string> Protocols => ["AT commands", "3GPP 27.007", "27.005 SMS", "LTE-M"];
    public override Difficulty Difficulty => Difficulty.Beginner;
    public override string DocsPath => "docs/en/protocols/at-commands.md";

    private AtModemSimulator? _module;
    private AtModem? _modem;
    private CancellationTokenSource? _cts;

    public ObservableCollection<string> Terminal { get; } = [];
    public ObservableCollection<string> Urcs { get; } = [];

    [ObservableProperty] private string _command = "AT+COPS?";
    [ObservableProperty] private bool _writesAllowed;
    [ObservableProperty] private int _bars;
    [ObservableProperty] private string _signal = "";
    [ObservableProperty] private string _registration = "";
    [ObservableProperty] private string _identity = "";
    [ObservableProperty] private string _place = "Jalan tol Cipularang, km 92";
    [ObservableProperty] private string _reply = "On schedule, ETA 14:30";

    protected override async Task OnStartAsync()
    {
        _cts = new CancellationTokenSource();
        Terminal.Clear();
        Urcs.Clear();
        var link = new InMemoryTransportListener("uart1");
        _module = AtModemSimulator.Create(o => { o.ListenInMemory(link); o.RegistrationDelay = TimeSpan.FromSeconds(1.5); });
        _module.AddTap(Tap);
        await _module.StartAsync();
        _modem = AtModem.Create(o => o.UseInMemory(link));   // the demo's write lock sits in front of SMS sending
        await _modem.ConnectAsync();
        _ = UrcLoopAsync(_cts.Token);
        foreach (var c in new[] { "ATE0", "AT+CMEE=2", "AT+CMGF=1", "AT+CEREG=2", "AT+CNMI=2,1" }) await RunAsync(c);
        var info = await _modem.GetInfoAsync();
        Identity = $"{info.Manufacturer} {info.Model} · rev {info.Revision} · IMEI {info.Imei} · ICCID {info.Iccid} · SIM {info.SimStatus}";
        _ = PollAsync(_cts.Token);

        if (Environment.GetEnvironmentVariable("IOTCOM_GALLERY_SCREENSHOT") == "1")
        {
            await Task.Delay(1800);
            await RunAsync("AT+COPS?");
            ReceiveSms();
            await Task.Delay(500);
        }

        SetStatus(new Text("LTE-M module (AtModemSimulator) on an in-memory UART · telematics unit AtModem · +CEREG=2 and +CNMI=2,1 enabled, so registration changes and new SMS arrive as URCs.",
            "Modul LTE-M (AtModemSimulator) pada UART dalam memori · unit telematika AtModem · +CEREG=2 dan +CNMI=2,1 aktif, jadi perubahan registrasi dan SMS baru tiba sebagai URC."));
    }

    private void Line(string text) => Ui(() =>
    {
        Terminal.Add(text);
        while (Terminal.Count > 200) Terminal.RemoveAt(0);
    });

    /// <summary>Sends one command and prints the exchange like a terminal.</summary>
    public async Task RunAsync(string command)
    {
        if (_modem is null || string.IsNullOrWhiteSpace(command)) return;
        Line($"> {command}");
        try
        {
            var r = await _modem.SendAsync(command.Trim());
            foreach (var l in r.Lines) Line($"  {l}");
            Line(r.IsSuccess ? "  OK" : $"  {r.Result}{(r.ErrorText is null ? "" : ": " + r.ErrorText)}");
        }
        catch (IoTComException ex)
        {
            Line($"  ! {ex.Message}");
        }
    }

    public Task RunTypedAsync() => RunAsync(Command);

    private async Task UrcLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var urc in _modem!.UrcsAsync(ct))
            {
                var meaning = urc.Name switch
                {
                    "+CEREG" => urc.Line.Split(':')[1].Trim().Split(',')[0] switch
                    {
                        "1" => "registered, home network",
                        "5" => "registered, roaming",
                        "2" => "searching",
                        "0" => "not registered",
                        "3" => "registration denied",
                        _ => "registration changed",
                    },
                    "+CMTI" => "new SMS stored — reading it",
                    _ => "",
                };
                Ui(() =>
                {
                    Urcs.Insert(0, $"{urc.Time.ToLocalTime():HH:mm:ss}  {urc.Line}{(meaning.Length > 0 ? "  → " + meaning : "")}");
                    while (Urcs.Count > 40) Urcs.RemoveAt(Urcs.Count - 1);
                });
                if (urc.Name == "+CMTI" && int.TryParse(urc.Line.Split(',').Last(), out var index)) _ = ReadSmsAsync(index);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReadSmsAsync(int index)
    {
        try
        {
            var sms = await _modem!.ReadSmsAsync(index);
            AddLog($"SMS {index} from {sms.Sender}: \"{sms.Text}\"");
        }
        catch (IoTComException ex)
        {
            AddLog($"AT+CMGR={index}: {ex.Message}");
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var s = await _modem!.GetSignalAsync(ct);
                var reg = await _modem.GetRegistrationAsync(ct);
                Ui(() =>
                {
                    Bars = s.Bars;
                    Signal = s.Dbm is { } dbm ? $"{dbm} dBm (rssi {s.Rssi})" : "no signal (rssi 99)";
                    Registration = reg.ToString();
                });
                await Task.Delay(1500, ct);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IoTComException)
        {
        }
    }

    // ---- the road ----------------------------------------------------------------------------------------------------

    public void EnterTunnel()
    {
        if (_module is null) return;
        Place = "Terowongan Kiara, inside";
        _module.Rssi = 2;
        _module.ChangeRegistration(AtRegistration.Searching);
        AddLog("The truck enters a tunnel: the signal drops and the module starts searching");
    }

    public void LeaveTunnel()
    {
        if (_module is null) return;
        Place = "Jalan tol Cipularang, km 104";
        _module.Rssi = 22;
        _module.ChangeRegistration(AtRegistration.Home);
        AddLog("Out of the tunnel: the module registers again");
    }

    public void CrossBorder()
    {
        if (_module is null) return;
        Place = "Border crossing, partner network";
        _module.Rssi = 17;
        _module.ChangeRegistration(AtRegistration.Roaming);
        AddLog("Across the border: the module registers on a partner network (roaming)");
    }

    public void ReceiveSms()
    {
        _module?.DeliverSms("+6281234500077", "Dispatch: please confirm ETA at gudang Cikarang");
        AddLog("The dispatcher sends an SMS");
    }

    public async Task SendReplyAsync()
    {
        if (_modem is null) return;
        if (!WritesAllowed)
        {
            AddLog("AT+CMGS: refused — the write lock is closed (sending SMS costs money and reaches people)");
            return;
        }

        try
        {
            var reference = await _modem.SendSmsAsync("+6281234500077", Reply);
            AddLog($"AT+CMGS → +CMGS: {reference} (\"{Reply}\")");
        }
        catch (IoTComException ex)
        {
            AddLog($"AT+CMGS: {ex.Message}");
        }
    }

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_modem is not null) await _modem.DisposeAsync();
        if (_module is not null) await _module.DisposeAsync();
        (_modem, _module) = (null, null);
        _cts?.Dispose();
        _cts = null;
        WritesAllowed = false;
        Status = "";
    }
}
