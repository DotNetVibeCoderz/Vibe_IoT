using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.EtherNetIp;

/// <summary>A symbolic tag served by <see cref="EnipAdapter"/> (Logix Read/Write Tag).</summary>
public sealed class EnipTag
{
    private readonly Lock _gate = new();
    private CipTagValue _value;

    internal EnipTag(string name, CipTagValue value, bool writable) => (Name, _value, Writable) = (name, value, writable);

    /// <summary>Tag name (dots allowed for program-scoped or member tags).</summary>
    public string Name { get; }

    /// <summary>Clients may write it.</summary>
    public bool Writable { get; set; }

    /// <summary>The value (all elements).</summary>
    public CipTagValue Value
    {
        get
        {
            lock (_gate) return _value;
        }
    }

    /// <summary>Raised when a client or the process changes the value.</summary>
    public event Action<EnipTag>? Changed;

    /// <summary>Sets the value from the process.</summary>
    public void Set(params double[] values) => Replace(CipTagValue.Of(_value.Type, values.Length == 0 ? [0] : values));

    internal void Replace(CipTagValue v)
    {
        lock (_gate) _value = v;
        Changed?.Invoke(this);
    }

    internal void WriteElements(int start, CipTagValue v)
    {
        lock (_gate)
        {
            var data = (byte[])_value.Data.Clone();
            v.Data.CopyTo(data, start * CipTagValue.SizeOf(_value.Type));
            _value = _value with { Data = data };
        }

        Changed?.Invoke(this);
    }
}

/// <summary>Options for <see cref="EnipAdapter"/>.</summary>
public sealed class EnipAdapterOptions : IListenerBuilder<EnipAdapterOptions>, IDatagramBuilder<EnipAdapterOptions>
{
    /// <summary>TCP listener (<c>UseTcp(IPAddress.Any, 44818)</c> or <c>ListenInMemory</c>).</summary>
    public TransportListenerFactory? ListenerFactory { get; set; }

    /// <summary>Optional UDP socket answering ListIdentity broadcasts (<c>UseUdp(44818)</c>).</summary>
    public DatagramTransportFactory? DiscoveryFactory { get; set; }

    /// <summary>The Identity object.</summary>
    public EnipIdentity Identity { get; set; } = new() { VendorId = 0xFFFF, DeviceType = 0x0E, ProductCode = 4, RevisionMajor = 1, RevisionMinor = 2, SerialNumber = 0x00C0FFEE, ProductName = "IoTCom EtherNet/IP Adapter" };

    /// <summary>Refuse every write (Set_Attribute, Write Tag) with privilege violation (default false).</summary>
    public bool WriteProtected { get; set; }

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public EnipAdapterOptions UseListener(TransportListenerFactory factory)
    {
        ListenerFactory = factory;
        return this;
    }

    /// <inheritdoc />
    EnipAdapterOptions IListenerBuilder<EnipAdapterOptions>.UseTransport(TransportFactory factory) =>
        throw new NotSupportedException("An adapter accepts connections; use UseTcp(address, port) or ListenInMemory(listener).");

    /// <inheritdoc />
    public EnipAdapterOptions UseDatagramTransport(DatagramTransportFactory factory)
    {
        DiscoveryFactory = factory;
        return this;
    }
}

/// <summary>
/// An EtherNet/IP target (adapter) for explicit messaging: sessions, ListIdentity (TCP and UDP), ListServices,
/// SendRRData with a CIP message router serving the Identity object, Assembly objects, Multiple_Service_Packet,
/// Unconnected_Send and Logix-style symbolic tags.
/// </summary>
public sealed class EnipAdapter : EndpointBase, IServerEndpoint
{
    private readonly EnipAdapterOptions _options;
    private readonly ConcurrentDictionary<string, EnipTag> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, (byte[] Data, bool Writable)> _assemblies = new();
    private readonly ConcurrentDictionary<uint, byte> _sessions = new();
    private ITransportListener? _listener;
    private IDatagramTransport? _udp;
    private CancellationTokenSource? _cts;
    private Task? _accept, _discovery;
    private int _nextSession = 0x1000;

    private EnipAdapter(EnipAdapterOptions options) : base("enip", options.Logger)
    {
        _options = options;
        Name = options.Name ?? options.Identity.ProductName;
    }

    /// <summary>Creates an adapter.</summary>
    public static EnipAdapter Create(Action<EnipAdapterOptions> configure)
    {
        var o = new EnipAdapterOptions();
        configure(o);
        return new EnipAdapter(o);
    }

    /// <summary>The Identity object.</summary>
    public EnipIdentity Identity => _options.Identity;

    /// <summary>Served tags.</summary>
    public IReadOnlyCollection<EnipTag> Tags => [.. _tags.Values.OrderBy(t => t.Name, StringComparer.Ordinal)];

    /// <summary>Open sessions.</summary>
    public int Sessions => _sessions.Count;

    /// <summary>Raised after a client wrote a tag.</summary>
    public event Action<EnipTag>? TagWritten;

    /// <summary>Adds a tag.</summary>
    public EnipTag AddTag(string name, CipTagValue value, bool writable = true)
    {
        var tag = new EnipTag(name, value, writable);
        if (!_tags.TryAdd(name, tag)) throw new ArgumentException($"Tag '{name}' already exists.", nameof(name));
        return tag;
    }

    /// <summary>Finds a tag.</summary>
    public EnipTag? Find(string name) => _tags.TryGetValue(name, out var t) ? t : null;

    /// <summary>Adds an Assembly object instance (attribute 3 = data).</summary>
    public void AddAssembly(uint instance, int size, bool writable) => _assemblies[instance] = (new byte[size], writable);

    /// <summary>Reads or replaces an assembly's data from the process.</summary>
    public byte[] Assembly(uint instance, byte[]? data = null)
    {
        if (data is not null) _assemblies[instance] = (data, _assemblies.TryGetValue(instance, out var a) && a.Writable);
        return _assemblies[instance].Data;
    }

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_listener is not null) return;
        SetState(EndpointState.Connecting);
        _listener = (_options.ListenerFactory ?? (() => new TcpTransportListener(new IPEndPoint(IPAddress.Any, EnipPacket.Port))))();
        await _listener.StartAsync(ct).ConfigureAwait(false);
        _cts = new CancellationTokenSource();
        var acceptLoopToken = _cts.Token;   // read now: a concurrent teardown may null _cts before the task runs
        _accept = Task.Run(() => AcceptLoopAsync(acceptLoopToken), CancellationToken.None);
        if (_options.DiscoveryFactory is { } f)
        {
            _udp = f();
            var discoveryLoopToken = _cts.Token;   // read now: a concurrent teardown may null _cts before the task runs
            _discovery = Task.Run(() => DiscoveryLoopAsync(discoveryLoopToken), CancellationToken.None);
        }

        SetState(EndpointState.Listening);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ITransport t;
            try
            {
                t = await _listener!.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is TransportException or ObjectDisposedException or InvalidOperationException)
            {
                if (ct.IsCancellationRequested) break;
                continue;
            }

            _ = Task.Run(() => ServeAsync(t, ct), CancellationToken.None);
        }
    }

    private async Task ServeAsync(ITransport t, CancellationToken ct)
    {
        uint session = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await EnipClient.ReadFrameAsync(t.Pipe.Input, ct).ConfigureAwait(false);
                EnipPacket request;
                try
                {
                    request = EnipPacket.Decode(frame);
                }
                catch (ProtocolException)
                {
                    break;
                }

                Tap(FrameDirection.Inbound, frame, request.Describe);
                var (reply, close) = Handle(request, ref session);
                if (reply is not null)
                {
                    var bytes = reply.Encode();
                    Tap(FrameDirection.Outbound, bytes, reply.Describe);
                    await t.Pipe.Output.WriteAsync(bytes, ct).ConfigureAwait(false);
                }

                if (close) break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or TransportException or IOException or InvalidOperationException)
        {
        }
        finally
        {
            if (session != 0) _sessions.TryRemove(session, out _);
            await t.DisposeAsync().ConfigureAwait(false);
        }
    }

    private (EnipPacket? Reply, bool Close) Handle(EnipPacket p, ref uint session)
    {
        var reply = p with { Data = [], Status = EnipStatus.Success };
        switch (p.Command)
        {
            case EnipCommand.Nop:
                return (null, false);
            case EnipCommand.ListIdentity:
                return (reply with { Data = EnipPacket.Cpf(new CpfItem(CpfItemType.ListIdentity, _options.Identity.Encode())) }, false);
            case EnipCommand.ListServices:
                byte[] svc = [1, 0, 0x20, 0x01, .. "Communications\0\0"u8.ToArray()];   // version 1, flags: CIP over TCP
                return (reply with { Data = EnipPacket.Cpf(new CpfItem(CpfItemType.ListServices, svc)) }, false);
            case EnipCommand.RegisterSession:
                if (p.Data.Length != 4 || BinaryPrimitives.ReadUInt16LittleEndian(p.Data) != 1) return (reply with { Status = EnipStatus.UnsupportedProtocolRevision, Data = EnipPacket.RegisterSessionData }, false);
                session = (uint)Interlocked.Increment(ref _nextSession);
                _sessions[session] = 0;
                return (reply with { Session = session, Data = EnipPacket.RegisterSessionData }, false);
            case EnipCommand.UnregisterSession:
                return (null, true);
            case EnipCommand.SendRRData:
                if (p.Session == 0 || p.Session != session) return (reply with { Status = EnipStatus.InvalidSessionHandle }, false);
                byte[] cip;
                try
                {
                    cip = EnipPacket.UnconnectedPayload(p.Data);
                }
                catch (ProtocolException)
                {
                    return (reply with { Status = EnipStatus.IncorrectData }, false);
                }

                var answer = Route(cip);
                return (reply with { Data = EnipPacket.SendRRData(answer, 0) }, false);
            default:
                return (reply with { Status = EnipStatus.InvalidCommand }, false);
        }
    }

    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Datagram d;
            try
            {
                d = await _udp!.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or ChannelClosedException)
            {
                if (ct.IsCancellationRequested) break;
                continue;
            }

            try
            {
                var p = EnipPacket.Decode(d.Data.Span);
                if (p.Command != EnipCommand.ListIdentity) continue;
                var local = _udp.LocalEndPoint as IPEndPoint;
                var id = _options.Identity with { Address = new IPEndPoint(local?.Address ?? IPAddress.Any, EnipPacket.Port) };
                var bytes = (p with { Data = EnipPacket.Cpf(new CpfItem(CpfItemType.ListIdentity, id.Encode())) }).Encode();
                await _udp.SendAsync(bytes, d.Remote, ct).ConfigureAwait(false);
            }
            catch (ProtocolException)
            {
            }
        }
    }

    // ---- CIP message router ------------------------------------------------------------------------------------------

    private byte[] Route(byte[] cip)
    {
        CipRequest req;
        try
        {
            req = CipRequest.Decode(cip);
        }
        catch (ProtocolException)
        {
            return CipResponse.Error(cip.Length > 0 ? (CipService)(cip[0] & 0x7F) : 0, CipStatus.PathSegmentError).Encode();
        }

        return Dispatch(req).Encode();
    }

    /// <summary>Handles one CIP request (also usable without a network).</summary>
    public CipResponse Dispatch(CipRequest req)
    {
        try
        {
            if (req.Path.Symbols.Count > 0) return Tag(req);
            return (req.Path.Class, req.Service) switch
            {
                (CipClass.ConnectionManager, CipService.UnconnectedSend) => UnconnectedSend(req),
                (CipClass.MessageRouter, CipService.MultipleServicePacket) => Multiple(req),
                (CipClass.Identity, _) => IdentityObject(req),
                (CipClass.Assembly, _) => AssemblyObject(req),
                (null, _) => CipResponse.Error(req.Service, CipStatus.PathSegmentError),
                _ => CipResponse.Error(req.Service, CipStatus.PathDestinationUnknown),
            };
        }
        catch (CipException ex)
        {
            return CipResponse.Error(req.Service, ex.Status, [.. ex.Additional]);
        }
    }

    private CipResponse UnconnectedSend(CipRequest req)
    {
        var d = req.Data;
        if (d.Length < 4) return CipResponse.Error(req.Service, CipStatus.NotEnoughData);
        var size = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(2));
        if (d.Length < 4 + size) return CipResponse.Error(req.Service, CipStatus.NotEnoughData);
        return Dispatch(CipRequest.Decode(d.AsSpan(4, size)));   // the route ends here: this target is the destination
    }

    private CipResponse Multiple(CipRequest req)
    {
        var parts = EnipClient.SplitMultiple(req.Data);
        var replies = parts.Select(p => Dispatch(CipRequest.Decode(p)).Encode()).ToList();
        var anyError = replies.Any(r => r[2] != 0);
        return new CipResponse(req.Service, anyError ? CipStatus.EmbeddedServiceError : CipStatus.Success, [], EnipClient.MultipleServicePacket(replies));
    }

    private CipResponse IdentityObject(CipRequest req)
    {
        if (req.Path.Instance is not 1) throw new CipException(CipStatus.ObjectDoesNotExist);
        var all = _options.Identity.AttributesAll();
        switch (req.Service)
        {
            case CipService.GetAttributesAll:
                return CipResponse.Ok(req.Service, all);
            case CipService.GetAttributeSingle:
                byte[] value = req.Path.Attribute switch
                {
                    1 => all[0..2], 2 => all[2..4], 3 => all[4..6], 4 => all[6..8], 5 => all[8..10], 6 => all[10..14], 7 => all[14..],
                    _ => throw new CipException(CipStatus.AttributeNotSupported),
                };
                return CipResponse.Ok(req.Service, value);
            case CipService.SetAttributeSingle:
                throw new CipException(req.Path.Attribute is >= 1 and <= 7 ? CipStatus.AttributeNotSettable : CipStatus.AttributeNotSupported);
            case CipService.Reset:
                throw new CipException(CipStatus.PrivilegeViolation, null, "Reset is not allowed on a simulator.");
            default:
                throw new CipException(CipStatus.ServiceNotSupported);
        }
    }

    private CipResponse AssemblyObject(CipRequest req)
    {
        if (req.Path.Instance is not { } inst || !_assemblies.TryGetValue(inst, out var a)) throw new CipException(CipStatus.ObjectDoesNotExist);
        if (req.Path.Attribute is not 3) throw new CipException(CipStatus.AttributeNotSupported);
        switch (req.Service)
        {
            case CipService.GetAttributeSingle:
                return CipResponse.Ok(req.Service, (byte[])a.Data.Clone());
            case CipService.SetAttributeSingle:
                if (_options.WriteProtected || !a.Writable) throw new CipException(CipStatus.AttributeNotSettable);
                if (req.Data.Length < a.Data.Length) throw new CipException(CipStatus.NotEnoughData);
                if (req.Data.Length > a.Data.Length) throw new CipException(CipStatus.TooMuchData);
                _assemblies[inst] = (req.Data, true);
                return CipResponse.Ok(req.Service);
            default:
                throw new CipException(CipStatus.ServiceNotSupported);
        }
    }

    private CipResponse Tag(CipRequest req)
    {
        var name = string.Join(".", req.Path.Symbols);
        if (!_tags.TryGetValue(name, out var tag)) throw new CipException(CipStatus.PathDestinationUnknown, null, $"Tag '{name}' does not exist.");
        var v = tag.Value;
        var size = CipTagValue.SizeOf(v.Type);
        var start = (int)(req.Path.Element ?? 0);
        switch (req.Service)
        {
            case CipService.ReadTag:
            {
                var count = req.Data.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(req.Data) : 1;
                if (start + count > v.Count) throw new CipException(CipStatus.PathSegmentError, [0x2105], $"{name} has {v.Count} element(s).");
                byte[] data = [(byte)v.Type, (byte)((ushort)v.Type >> 8), .. v.Data.AsSpan(start * size, count * size)];
                return CipResponse.Ok(req.Service, data);
            }

            case CipService.WriteTag:
            {
                if (_options.WriteProtected || !tag.Writable) throw new CipException(CipStatus.PrivilegeViolation, null, $"{name} is read-only.");
                if (req.Data.Length < 4) throw new CipException(CipStatus.NotEnoughData);
                var type = (CipDataType)BinaryPrimitives.ReadUInt16LittleEndian(req.Data);
                if (type != v.Type) throw new CipException(CipStatus.ObjectStateConflict, [0x2107], $"{name} is {v.Type}, not {type}.");
                var count = BinaryPrimitives.ReadUInt16LittleEndian(req.Data.AsSpan(2));
                if (start + count > v.Count) throw new CipException(CipStatus.PathSegmentError, [0x2105]);
                if (req.Data.Length - 4 != count * size) throw new CipException(req.Data.Length - 4 < count * size ? CipStatus.NotEnoughData : CipStatus.TooMuchData);
                tag.WriteElements(start, new CipTagValue(type, req.Data[4..]));
                TagWritten?.Invoke(tag);
                return CipResponse.Ok(req.Service);
            }

            default:
                throw new CipException(CipStatus.ServiceNotSupported);
        }
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        if (_listener is null) return;
        SetState(EndpointState.Stopping);
        await (_cts?.CancelAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        await _listener.DisposeAsync().ConfigureAwait(false);
        if (_udp is not null) await _udp.DisposeAsync().ConfigureAwait(false);
        if (_accept is not null) await _accept.ConfigureAwait(false);
        if (_discovery is not null) await _discovery.ConfigureAwait(false);
        (_listener, _udp) = (null, null);
        _cts?.Dispose();
        _cts = null;
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);
}

/// <summary>
/// A packaging-line controller as an <see cref="EnipAdapter"/>: tags Line_Speed, Motor_Run, Count_Good, Count_Reject,
/// Seal_Temp, Seal_Setpoint, Recipe, Fault_Code and Zone_Temps[4], plus input assembly 100 and output assembly 150.
/// </summary>
public sealed class EnipPlcSimulator : IAsyncDisposable
{
    private readonly Random _random;
    private double _goodFraction;

    private EnipPlcSimulator(EnipAdapter adapter, int seed)
    {
        Adapter = adapter;
        _random = new Random(seed);
        LineSpeed = adapter.AddTag("Line_Speed", CipTagValue.Of(CipDataType.Real, 120), writable: true);
        MotorRun = adapter.AddTag("Motor_Run", CipTagValue.Of(CipDataType.Bool, 1), writable: true);
        CountGood = adapter.AddTag("Count_Good", CipTagValue.Of(CipDataType.Dint, 18_204), writable: false);
        CountReject = adapter.AddTag("Count_Reject", CipTagValue.Of(CipDataType.Dint, 213), writable: false);
        SealTemp = adapter.AddTag("Seal_Temp", CipTagValue.Of(CipDataType.Real, 171.8), writable: false);
        SealSetpoint = adapter.AddTag("Seal_Setpoint", CipTagValue.Of(CipDataType.Real, 172), writable: true);
        Recipe = adapter.AddTag("Recipe", CipTagValue.Of(CipDataType.Dint, 3), writable: true);
        FaultCode = adapter.AddTag("Fault_Code", CipTagValue.Of(CipDataType.Int, 0), writable: false);
        ZoneTemps = adapter.AddTag("Zone_Temps", CipTagValue.Of(CipDataType.Real, 168.2, 170.9, 172.4, 169.8), writable: false);
        adapter.AddAssembly(100, 16, writable: false);
        adapter.AddAssembly(150, 8, writable: true);
        UpdateAssembly();
    }

    /// <summary>Creates the controller; configure the adapter (listener, discovery, identity).</summary>
    public static EnipPlcSimulator Create(Action<EnipAdapterOptions>? configure = null, int seed = 5) =>
        new(EnipAdapter.Create(o =>
        {
            o.Identity = new EnipIdentity { VendorId = 0xFFFF, DeviceType = 0x0E, ProductCode = 4, RevisionMajor = 33, RevisionMinor = 11, SerialNumber = 0x0BADCAFE, ProductName = "IoTCom PackLine-1 PLC" };
            configure?.Invoke(o);
        }), seed);

    /// <summary>The adapter.</summary>
    public EnipAdapter Adapter { get; }

    /// <summary>Line speed, packs per minute (REAL, writable).</summary>
    public EnipTag LineSpeed { get; }
    /// <summary>Conveyor motor run command (BOOL, writable).</summary>
    public EnipTag MotorRun { get; }
    /// <summary>Good packs (DINT).</summary>
    public EnipTag CountGood { get; }
    /// <summary>Rejected packs (DINT).</summary>
    public EnipTag CountReject { get; }
    /// <summary>Seal jaw temperature, °C (REAL).</summary>
    public EnipTag SealTemp { get; }
    /// <summary>Seal jaw set point, °C (REAL, writable).</summary>
    public EnipTag SealSetpoint { get; }
    /// <summary>Active recipe number (DINT, writable).</summary>
    public EnipTag Recipe { get; }
    /// <summary>Fault code, 0 = none (INT).</summary>
    public EnipTag FaultCode { get; }
    /// <summary>Four heater zone temperatures, °C (REAL[4]).</summary>
    public EnipTag ZoneTemps { get; }

    /// <summary>Seal temperature the film needs, °C (packs sealed more than 4 °C away are often rejected).</summary>
    public const double NominalSealTemp = 172;

    /// <summary>Starts the adapter.</summary>
    public ValueTask StartAsync(CancellationToken ct = default) => Adapter.StartAsync(ct);

    /// <summary>Advances the line: packs counted, seal temperature follows its set point, a cold seal raises rejects.</summary>
    public void Step(double seconds)
    {
        var running = MotorRun.Value.Value != 0 && FaultCode.Value.Value == 0;
        var target = SealSetpoint.Value.Value;
        var seal = SealTemp.Value.Value;
        seal += (target - seal) * Math.Min(1, seconds / 40) + ((_random.NextDouble() - 0.5) * 0.3);
        SealTemp.Set(Math.Round(seal, 2));
        ZoneTemps.Set([.. Enumerable.Range(0, 4).Select(i => Math.Round(seal - 3 + (i * 1.2) + ((_random.NextDouble() - 0.5) * 0.6), 1))]);
        if (running)
        {
            var packs = LineSpeed.Value.Value / 60 * seconds;
            var rejectRate = Math.Abs(seal - NominalSealTemp) > 4 ? 0.12 : 0.012;   // seals outside the recipe window leak
            _goodFraction += packs;
            var whole = Math.Floor(_goodFraction);
            _goodFraction -= whole;
            var rejects = Math.Round(whole * rejectRate);
            CountGood.Set(CountGood.Value.Value + whole - rejects);
            CountReject.Set(CountReject.Value.Value + rejects);
        }

        UpdateAssembly();
    }

    private void UpdateAssembly()
    {
        var a = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(a, (int)CountGood.Value.Value);
        BinaryPrimitives.WriteInt32LittleEndian(a.AsSpan(4), (int)CountReject.Value.Value);
        BinaryPrimitives.WriteSingleLittleEndian(a.AsSpan(8), (float)SealTemp.Value.Value);
        a[12] = (byte)(MotorRun.Value.Value != 0 ? 1 : 0);
        BinaryPrimitives.WriteInt16LittleEndian(a.AsSpan(14), (short)FaultCode.Value.Value);
        Adapter.Assembly(100, a);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Adapter.DisposeAsync();
}
