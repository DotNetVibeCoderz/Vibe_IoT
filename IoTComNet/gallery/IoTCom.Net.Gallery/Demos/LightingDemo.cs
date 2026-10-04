using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using IoTCom.Net.Gallery.Infrastructure;
using IoTCom.Net.Protocols.Dmx;

namespace IoTCom.Net.Gallery.Demos;

/// <summary>
/// Stage lighting: a "console" Art-Net node streams a DMX universe at 30 fps to a "fixture" node over UDP
/// (loopback). Faders set RGB levels for four fixtures; the receiver renders what actually arrived.
/// </summary>
public sealed partial class LightingDemo : GalleryDemo
{
    public override string Id => "artnet-stage";
    public override Text Title => new("Stage lighting over Art-Net", "Lampu panggung lewat Art-Net");
    public override Text Summary => new(
        "A lighting console streams DMX512 over Art-Net to four RGB fixtures. Move the faders or run a chase — the fixtures show what the receiver decoded from UDP.",
        "Konsol lampu mengirim DMX512 lewat Art-Net ke empat fixture RGB. Geser fader atau jalankan chase — fixture menampilkan apa yang diurai penerima dari UDP.");
    public override Text Docs => new(
        "DMX512 carries up to 512 one-byte channels per universe. Art-Net wraps a universe in a UDP packet (port 6454): an 18-byte header (ID, OpCode 0x5000, version, sequence, port-address, length) followed by the channel data.\n\nArtNetNode sends with SendDmxAsync and receives via ReceiveAsync / DmxReceived. Consoles resend every universe continuously (here 30 fps) so receivers recover from packet loss. PollAsync discovers nodes with ArtPoll/ArtPollReply. SacnNode offers the same API for sACN (E1.31) multicast.",
        "DMX512 membawa hingga 512 kanal satu byte per universe. Art-Net membungkus satu universe dalam paket UDP (port 6454): header 18 byte (ID, OpCode 0x5000, versi, sequence, port-address, panjang) lalu data kanal.\n\nArtNetNode mengirim dengan SendDmxAsync dan menerima lewat ReceiveAsync / DmxReceived. Konsol mengirim ulang tiap universe terus-menerus (di sini 30 fps) agar penerima pulih dari paket hilang. PollAsync menemukan node dengan ArtPoll/ArtPollReply. SacnNode menawarkan API yang sama untuk sACN (E1.31) multicast.");
    public override string Category => "Building";
    public override IReadOnlyList<string> Protocols => ["Art-Net 4", "DMX512", "UDP"];
    public override Difficulty Difficulty => Difficulty.Beginner;
    public override string DocsPath => "docs/en/protocols/dmx.md";

    public const int Fixtures = 4;
    private readonly DmxUniverse _universe = new(0);
    private ArtNetNode? _console;
    private ArtNetNode? _fixtures;
    private CancellationTokenSource? _cts;
    private long _received;

    /// <summary>Packets decoded by the receiver.</summary>
    public long ReceivedPackets => Interlocked.Read(ref _received);

    /// <summary>Fader levels: fixture × (R, G, B).</summary>
    public byte[] Levels { get; } = [255, 120, 0, 0, 160, 255, 255, 0, 120, 40, 255, 80];

    /// <summary>Levels as decoded by the receiving node.</summary>
    public byte[] Received { get; } = new byte[Fixtures * 3];

    public event Action? ReceivedChanged;

    [ObservableProperty] private double _master = 1.0;
    [ObservableProperty] private bool _chase;
    [ObservableProperty] private long _packets;

    protected override async Task OnStartAsync()
    {
        _fixtures = ArtNetNode.Create(o => o.Bind(IPAddress.Loopback, 0).WithName("Fixtures"));
        await _fixtures.StartAsync();
        _fixtures.DmxReceived += frame =>
        {
            frame.Data.Span[..Math.Min(Received.Length, frame.Data.Length)].CopyTo(Received);
            Interlocked.Increment(ref _received);
            ReceivedChanged?.Invoke();
        };

        _console = ArtNetNode.Create(o => o.Bind(IPAddress.Loopback, 0).WithName("Console").SendTo(_fixtures.LocalEndPoint!));
        _console.AddTap(Tap);
        await _console.StartAsync();

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => StreamAsync(_cts.Token));
        SetStatus(new Text($"Streaming universe 0 to {_fixtures.LocalEndPoint} at 30 fps.", $"Mengirim universe 0 ke {_fixtures.LocalEndPoint} pada 30 fps."));
    }

    /// <summary>Like a real console: resend the whole universe at a steady frame rate.</summary>
    private async Task StreamAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        var frame = new byte[Fixtures * 3];
        var t = 0.0;
        while (await timer.WaitForNextTickAsync(ct))
        {
            t += 0.033;
            for (var f = 0; f < Fixtures; f++)
            {
                var gain = Master * (Chase ? Math.Max(0, Math.Cos((t * 2.5 - f * 0.9) % (2 * Math.PI))) : 1);
                for (var c = 0; c < 3; c++) frame[f * 3 + c] = (byte)Math.Round(Levels[f * 3 + c] * gain);
            }
            _universe.Set(1, frame);
            await _console!.SendDmxAsync(_universe.Number, _universe.Snapshot().AsMemory(0, 16), ct: ct);
        }
    }

    public void SetLevel(int fixture, int color, byte value) => Levels[fixture * 3 + color] = value;

    protected override async Task OnStopAsync()
    {
        if (_cts is not null) await _cts.CancelAsync();
        if (_console is not null) await _console.DisposeAsync();
        if (_fixtures is not null) await _fixtures.DisposeAsync();
        _cts?.Dispose();
        (_console, _fixtures, _cts) = (null, null, null);
        Array.Clear(Received);
        ReceivedChanged?.Invoke();
        Status = "";
    }
}
