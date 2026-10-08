using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using IoTCom.Net.Transport.Can;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace IoTCom.Net.Protocols.J1939;

/// <summary>A complete J1939 message (single frame or reassembled from the transport protocol).</summary>
/// <param name="Pgn">PGN.</param>
/// <param name="Source">Source address.</param>
/// <param name="Destination">Destination (0xFF = global).</param>
/// <param name="Priority">Priority.</param>
/// <param name="Data">Payload.</param>
public sealed record J1939Message(uint Pgn, byte Source, byte Destination, byte Priority, byte[] Data)
{
    /// <summary>Decoded SPNs for well-known PGNs.</summary>
    public IReadOnlyList<J1939Spn.Reading> Values => J1939Spn.Decode(Pgn, Data);

    /// <inheritdoc />
    public override string ToString() => $"{IoTCom.Net.Protocols.J1939.Pgn.Name(Pgn)} from 0x{Source:X2}: {(J1939Spn.Knows(Pgn) ? string.Join(", ", Values) : Convert.ToHexString(Data))}";
}

/// <summary>J1939 node options.</summary>
public sealed class J1939NodeOptions
{
    /// <summary>Preferred source address.</summary>
    public byte Address { get; set; } = 0xF9;   // off-board diagnostic-service tool 1

    /// <summary>NAME claimed with the address.</summary>
    public J1939Name Name { get; set; } = new(0x1D0F5, 0x7FF, 0, 0, 249, 0, 0, 1, ArbitraryAddressCapable: true);

    /// <summary>Do not claim an address and never transmit (passive monitoring).</summary>
    public bool ListenOnly { get; set; }

    /// <summary>Refuse <see cref="J1939Node.SendAsync"/> (requests and address claims still go out).</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Gap between TP.DT packets of a broadcast (J1939-21: 50–200 ms).</summary>
    public TimeSpan BroadcastPacketGap { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Transport protocol timeouts (T1–T4 are 750–1250 ms in J1939-21).</summary>
    public TimeSpan TransportTimeout { get; set; } = TimeSpan.FromMilliseconds(1250);

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }
}

/// <summary>
/// A J1939 node on an <see cref="ICanBus"/>: address claim (with arbitration by NAME), the transport protocol in both
/// directions (BAM for broadcasts, RTS/CTS for destination-specific messages up to 1785 bytes), requests, and
/// answers to requests for registered PGNs.
/// </summary>
public sealed class J1939Node : IAsyncDisposable
{
    private readonly ICanBus _bus;
    private readonly J1939NodeOptions _options;
    private readonly ILogger _log;
    private readonly Channel<CanFrame> _inbox = Channel.CreateBounded<CanFrame>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ConcurrentDictionary<(byte Source, byte Destination), Reassembly> _rx = new();
    private readonly ConcurrentDictionary<uint, Func<byte, byte[]?>> _responders = new();
    private readonly ConcurrentDictionary<(byte Peer, uint Pgn), Channel<TpConnectionMessage>> _txSessions = new();
    private readonly ConcurrentDictionary<byte, J1939Name> _claims = new();
    private readonly SemaphoreSlim _tx = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private sealed class Reassembly
    {
        public required uint Pgn { get; init; }
        public required int Size { get; init; }
        public required int Packets { get; init; }
        public required bool Broadcast { get; init; }
        public byte[] Buffer { get; init; } = [];
        public int Received { get; set; }
        public long Started { get; init; } = Environment.TickCount64;
    }

    private J1939Node(ICanBus bus, J1939NodeOptions options)
    {
        (_bus, _options) = (bus, options);
        _log = options.Logger ?? NullLogger.Instance;
        Address = options.Address;
    }

    /// <summary>Creates a node.</summary>
    public static J1939Node Create(ICanBus bus, Action<J1939NodeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(bus);
        var o = new J1939NodeOptions();
        configure?.Invoke(o);
        return new J1939Node(bus, o);
    }

    /// <summary>Claimed source address (0xFE when the claim was lost).</summary>
    public byte Address { get; private set; }

    /// <summary>Addresses claimed by other nodes, with their NAMEs.</summary>
    public IReadOnlyDictionary<byte, J1939Name> Claims => _claims;

    /// <summary>Raised for every complete message (single frames and reassembled transport protocol messages).</summary>
    public event Action<J1939Message>? MessageReceived;

    /// <summary>Connects the bus if needed, starts listening and claims the address.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_cts is not null) return;
        if (_bus.State != EndpointState.Connected) await _bus.ConnectAsync(ct).ConfigureAwait(false);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _bus.FrameReceived += OnFrame;
        _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
        if (_options.ListenOnly) return;
        await ClaimAsync(Address, ct).ConfigureAwait(false);
        await Task.Delay(250, ct).ConfigureAwait(false);   // J1939-81: wait 250 ms for contending claims
    }

    private Task ClaimAsync(byte address, CancellationToken ct) =>
        _bus.SendAsync(new J1939Id(6, Pgn.AddressClaimed, J1939Id.Global, address).Frame(_options.Name.Encode()), ct).AsTask();

    /// <summary>Answers requests for <paramref name="pgn"/> with the data returned by <paramref name="responder"/> (requester → data, null = no answer).</summary>
    public void Respond(uint pgn, Func<byte, byte[]?> responder) => _responders[pgn] = responder;

    private void OnFrame(CanFrame f)
    {
        if (f.IsExtended && !f.IsRemote) _inbox.Writer.TryWrite(f);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var f in _inbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(J1939Id.FromCanId(f.Id), f.Data.ToArray(), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is FormatException or IoTComException or InvalidOperationException)
                {
                    _log.LogDebug(ex, "J1939: ignored {Frame}", f);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool ForUs(J1939Id id) => !id.IsPdu1 || id.Destination == J1939Id.Global || id.Destination == Address || _options.ListenOnly;

    private async Task HandleAsync(J1939Id id, byte[] d, CancellationToken ct)
    {
        switch (id.Pgn)
        {
            case Pgn.AddressClaimed when d.Length >= 8:
                var name = J1939Name.Parse(d);
                _claims[id.Source] = name;
                if (!_options.ListenOnly && id.Source == Address && name.Value != _options.Name.Value)
                {
                    if (name.Value < _options.Name.Value)
                    {
                        // We lose: move to a free address if allowed, otherwise announce "cannot claim".
                        var next = _options.Name.ArbitraryAddressCapable ? Enumerable.Range(128, 120).Select(a => (byte)a).FirstOrDefault(a => !_claims.ContainsKey(a)) : (byte)0;
                        Address = next == 0 ? J1939Id.Null : next;
                        _log.LogWarning("J1939: address 0x{Old:X2} lost to a lower NAME; now 0x{New:X2}", id.Source, Address);
                    }

                    await ClaimAsync(Address, ct).ConfigureAwait(false);
                }

                Dispatch(id, d);
                return;
            case Pgn.Request when d.Length >= 3:
                if (_options.ListenOnly || !ForUs(id)) break;
                var requested = d[0] | ((uint)d[1] << 8) | ((uint)d[2] << 16);
                if (requested == Pgn.AddressClaimed)
                {
                    await ClaimAsync(Address, ct).ConfigureAwait(false);
                }
                else if (_responders.TryGetValue(requested, out var responder) && responder(id.Source) is { } answer)
                {
                    await TransmitAsync(requested, answer, id.Destination == J1939Id.Global ? J1939Id.Global : id.Source, 6, ct).ConfigureAwait(false);
                }

                Dispatch(id, d);
                return;
            case Pgn.TpConnection when d.Length >= 8:
                await OnTpConnectionAsync(id, TpConnectionMessage.Parse(d), ct).ConfigureAwait(false);
                return;
            case Pgn.TpData when d.Length >= 2:
                await OnTpDataAsync(id, d, ct).ConfigureAwait(false);
                return;
        }

        if (ForUs(id)) Dispatch(id, d);
    }

    private void Dispatch(J1939Id id, byte[] d) => MessageReceived?.Invoke(new J1939Message(id.Pgn, id.Source, id.Destination, id.Priority, d));

    private async Task OnTpConnectionAsync(J1939Id id, TpConnectionMessage tp, CancellationToken ct)
    {
        switch (tp.Control)
        {
            case TpControl.Broadcast:
                _rx[(id.Source, J1939Id.Global)] = new Reassembly { Pgn = tp.Pgn, Size = tp.Size, Packets = tp.Packets, Broadcast = true, Buffer = new byte[tp.Packets * 7] };
                break;
            case TpControl.RequestToSend when id.Destination == Address || _options.ListenOnly:
                _rx[(id.Source, id.Destination)] = new Reassembly { Pgn = tp.Pgn, Size = tp.Size, Packets = tp.Packets, Broadcast = false, Buffer = new byte[tp.Packets * 7] };
                if (!_options.ListenOnly)
                    await SendRawAsync(new J1939Id(7, Pgn.TpConnection, id.Source, Address), new TpConnectionMessage(TpControl.ClearToSend, 0, tp.Packets, 1, 0, 0, tp.Pgn).Encode(), ct).ConfigureAwait(false);
                break;
            case TpControl.ClearToSend or TpControl.EndOfMessageAck or TpControl.Abort when id.Destination == Address:
                if (_txSessions.TryGetValue((id.Source, tp.Pgn), out var session)) session.Writer.TryWrite(tp);
                break;
        }
    }

    private async Task OnTpDataAsync(J1939Id id, byte[] d, CancellationToken ct)
    {
        var key = (id.Source, id.IsPdu1 ? id.Destination : J1939Id.Global);
        if (!_rx.TryGetValue(key, out var r)) return;
        var seq = d[0];
        if (seq < 1 || seq > r.Packets) return;
        d.AsSpan(1, Math.Min(7, d.Length - 1)).CopyTo(r.Buffer.AsSpan((seq - 1) * 7));
        r.Received = Math.Max(r.Received, seq);
        if (seq != r.Packets) return;
        _rx.TryRemove(key, out _);
        if (!r.Broadcast && !_options.ListenOnly)
            await SendRawAsync(new J1939Id(7, Pgn.TpConnection, id.Source, Address), new TpConnectionMessage(TpControl.EndOfMessageAck, (ushort)r.Size, (byte)r.Packets, 0, 0, 0, r.Pgn).Encode(), ct).ConfigureAwait(false);
        MessageReceived?.Invoke(new J1939Message(r.Pgn, id.Source, r.Broadcast ? J1939Id.Global : id.Destination, 6, r.Buffer[..r.Size]));
    }

    private Task SendRawAsync(J1939Id id, byte[] data, CancellationToken ct) => _bus.SendAsync(id.Frame(data), ct).AsTask();

    /// <summary>
    /// Sends a message: one frame up to 8 bytes, BAM for longer broadcasts, RTS/CTS for longer destination-specific
    /// messages. Refused in read-only mode.
    /// </summary>
    public Task SendAsync(uint pgn, ReadOnlyMemory<byte> data, byte destination = J1939Id.Global, byte priority = 6, CancellationToken ct = default)
    {
        if (_options.ReadOnly || _options.ListenOnly) throw new ReadOnlyModeException();
        return TransmitAsync(pgn, data.ToArray(), destination, priority, ct);
    }

    private async Task TransmitAsync(uint pgn, byte[] data, byte destination, byte priority, CancellationToken ct)
    {
        if (data.Length > 1785) throw new ArgumentException("J1939 messages are at most 1785 bytes.", nameof(data));
        var pdu1 = (pgn >> 8 & 0xFF) < 240;
        var dest = pdu1 ? destination : J1939Id.Global;
        if (data.Length <= 8)
        {
            await SendRawAsync(new J1939Id(priority, pgn, dest, Address), data, ct).ConfigureAwait(false);
            return;
        }

        await _tx.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var packets = TpConnectionMessage.PacketsFor(data.Length);
            if (dest == J1939Id.Global)
            {
                await SendRawAsync(new J1939Id(7, Pgn.TpConnection, J1939Id.Global, Address), new TpConnectionMessage(TpControl.Broadcast, (ushort)data.Length, packets, 0, 0, 0, pgn).Encode(), ct).ConfigureAwait(false);
                for (var seq = 1; seq <= packets; seq++)
                {
                    await Task.Delay(_options.BroadcastPacketGap, ct).ConfigureAwait(false);
                    await SendRawAsync(new J1939Id(7, Pgn.TpData, J1939Id.Global, Address), Packet(data, seq), ct).ConfigureAwait(false);
                }

                return;
            }

            var session = Channel.CreateUnbounded<TpConnectionMessage>();
            _txSessions[(dest, pgn)] = session;
            try
            {
                await SendRawAsync(new J1939Id(7, Pgn.TpConnection, dest, Address), new TpConnectionMessage(TpControl.RequestToSend, (ushort)data.Length, packets, 0, 0xFF, 0, pgn).Encode(), ct).ConfigureAwait(false);
                while (true)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(_options.TransportTimeout);
                    TpConnectionMessage answer;
                    try
                    {
                        answer = await session.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await SendRawAsync(new J1939Id(7, Pgn.TpConnection, dest, Address), new TpConnectionMessage(TpControl.Abort, 0, 0, 0, 0, 3, pgn).Encode(), CancellationToken.None).ConfigureAwait(false);
                        throw new IoTComTimeoutException($"J1939 transport to 0x{dest:X2}: no CTS/EndOfMsgAck within {_options.TransportTimeout.TotalMilliseconds:0} ms.");
                    }

                    switch (answer.Control)
                    {
                        case TpControl.EndOfMessageAck:
                            return;
                        case TpControl.Abort:
                            throw new DeviceException($"J1939 transport to 0x{dest:X2} aborted (reason {answer.AbortReason}).");
                        case TpControl.ClearToSend:
                            for (int seq = answer.NextPacket; seq < answer.NextPacket + answer.Packets && seq <= packets; seq++)
                                await SendRawAsync(new J1939Id(7, Pgn.TpData, dest, Address), Packet(data, seq), ct).ConfigureAwait(false);
                            break;
                    }
                }
            }
            finally
            {
                _txSessions.TryRemove((dest, pgn), out _);
            }
        }
        finally
        {
            _tx.Release();
        }
    }

    private static byte[] Packet(byte[] data, int seq)
    {
        var p = new byte[8];
        p.AsSpan().Fill(0xFF);
        p[0] = (byte)seq;
        var start = (seq - 1) * 7;
        data.AsSpan(start, Math.Min(7, data.Length - start)).CopyTo(p.AsSpan(1));
        return p;
    }

    /// <summary>Requests <paramref name="pgn"/> from <paramref name="destination"/> and waits for the answer (any source for global requests).</summary>
    public async Task<J1939Message> RequestAsync(uint pgn, byte destination = J1939Id.Global, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (_options.ListenOnly) throw new ReadOnlyModeException();
        var answer = new TaskCompletionSource<J1939Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(J1939Message m)
        {
            if (m.Pgn == pgn && (destination == J1939Id.Global || m.Source == destination)) answer.TrySetResult(m);
        }

        MessageReceived += Handler;
        try
        {
            await SendRawAsync(new J1939Id(6, Pgn.Request, destination, Address), [(byte)pgn, (byte)(pgn >> 8), (byte)(pgn >> 16)], ct).ConfigureAwait(false);
            return await answer.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new IoTComTimeoutException($"No answer to the request for {Pgn.Name(pgn)} from 0x{destination:X2}.");
        }
        finally
        {
            MessageReceived -= Handler;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        _bus.FrameReceived -= OnFrame;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _cts.Dispose();
        _cts = null;
        _tx.Dispose();
    }
}

/// <summary>
/// A simulated heavy-duty diesel engine ECU (source address 0x00): EEC1 and CCVS1 every 100 ms, EFL/P1 and LFE1 every
/// 500 ms, ET1, VEP1 and DM1 every second, and VIN, component identification and engine hours on request. After
/// <see cref="OilLeak"/> the oil pressure falls and DM1 reports SPN 100 FMI 1 with the amber lamp.
/// </summary>
public sealed class J1939EngineSimulator : IAsyncDisposable
{
    private readonly J1939Node _node;
    private readonly Random _random = new(9);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private double _rpm = 650, _speed, _coolant = 72, _oilPressure = 380, _hours = 12_843.6, _throttle;

    private J1939EngineSimulator(J1939Node node) => _node = node;

    /// <summary>Creates the ECU on <paramref name="bus"/>.</summary>
    public static J1939EngineSimulator Create(ICanBus bus, byte address = 0x00) =>
        new(J1939Node.Create(bus, o =>
        {
            o.Address = address;
            o.Name = new J1939Name(0x0A2B3, 0x146, 0, 0, 0, 0, 0, 1, ArbitraryAddressCapable: false);   // engine, on-highway
            o.BroadcastPacketGap = TimeSpan.FromMilliseconds(10);
        }));

    /// <summary>Vehicle identification number answered on request.</summary>
    public string Vin { get; set; } = "IOTJ1939SIMTRUCK1";

    /// <summary>Accelerator pedal position (0–100 %).</summary>
    public double Throttle
    {
        get => _throttle;
        set => _throttle = Math.Clamp(value, 0, 100);
    }

    /// <summary>True once <see cref="OilLeak"/> was called.</summary>
    public bool Leaking { get; private set; }

    /// <summary>Starts losing oil pressure.</summary>
    public void OilLeak() => Leaking = true;

    /// <summary>The node (address, claims).</summary>
    public J1939Node Node => _node;

    /// <summary>Claims the address and starts broadcasting.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _node.Respond(Pgn.VehicleIdentification, _ => Encoding.ASCII.GetBytes(Vin + "*"));
        _node.Respond(Pgn.ComponentIdentification, _ => Encoding.ASCII.GetBytes("IOTCOM*D13 SIM*SN0042*ENG-1*"));
        _node.Respond(Pgn.Hours, _ => Frame(Pgn.Hours, (247, _hours), (249, _hours * 1450 * 60 / 1000)));
        _node.Respond(Pgn.Dm2, _ => new J1939Dm1(0, 0, 0, 0, [new J1939Dtc(110, 0, 2)]).Encode());
        await _node.StartAsync(ct).ConfigureAwait(false);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
    }

    private static byte[] Frame(uint pgn, params (uint Spn, double Value)[] values)
    {
        var b = new byte[8];
        b.AsSpan().Fill(0xFF);
        foreach (var (spn, value) in values) J1939Spn.Encode(pgn, spn, value, b);
        return b;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var tick = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                tick++;
                Step(0.1);
                var send = new List<(uint Pgn, byte[] Data)>
                {
                    (Pgn.Eec1, Frame(Pgn.Eec1, (899, 1), (512, _throttle * 0.8), (513, Math.Max(0, _throttle * 0.7 - 5)), (190, _rpm), (1483, 0))),
                    (Pgn.Ccvs1, Frame(Pgn.Ccvs1, (84, _speed))),
                };
                if (tick % 5 == 0)
                {
                    send.Add((Pgn.EflP1, Frame(Pgn.EflP1, (94, 380), (98, Leaking ? 61 : 92), (100, _oilPressure))));
                    send.Add((Pgn.Lfe1, Frame(Pgn.Lfe1, (183, 2.4 + _rpm / 1900 * 38 * (0.3 + _throttle / 140)), (51, _throttle))));
                }

                if (tick % 10 == 0)
                {
                    send.Add((Pgn.Et1, Frame(Pgn.Et1, (110, _coolant), (174, 41), (175, _coolant + 9))));
                    send.Add((Pgn.Vep1, Frame(Pgn.Vep1, (167, 28.1), (168, 27.6 + _random.NextDouble() * 0.2))));
                    var dtcs = Leaking && _oilPressure < 150 ? new[] { new J1939Dtc(100, 1, 1) } : [];
                    send.Add((Pgn.Dm1, new J1939Dm1(0, 0, (byte)(dtcs.Length > 0 ? 1 : 0), 0, dtcs).Encode()));
                }

                foreach (var (pgn, data) in send) await _node.SendAsync(pgn, data, ct: ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IoTComException)
        {
        }
    }

    /// <summary>Advances the engine model by <paramref name="seconds"/>.</summary>
    public void Step(double seconds)
    {
        var targetRpm = 650 + _throttle * 13;
        _rpm += (targetRpm - _rpm) * Math.Min(1, seconds * 2) + (_random.NextDouble() - 0.5) * 6;
        var targetSpeed = Math.Max(0, (_rpm - 700) / 1200 * 90 * (_throttle > 5 ? 1 : 0.6));
        _speed += (targetSpeed - _speed) * Math.Min(1, seconds * 0.3);
        _coolant += ((_rpm > 1200 ? 88 : 80) - _coolant) * seconds * 0.05;
        var targetOil = Leaking ? 60 : 250 + _rpm / 1900 * 220;
        _oilPressure += (targetOil - _oilPressure) * Math.Min(1, seconds * (Leaking ? 0.4 : 1.5));
        _hours += seconds / 3600;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _cts?.Dispose();
        await _node.DisposeAsync().ConfigureAwait(false);
    }
}
