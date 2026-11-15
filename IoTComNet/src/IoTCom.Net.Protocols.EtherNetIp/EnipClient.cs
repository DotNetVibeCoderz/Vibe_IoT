using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Net;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.EtherNetIp;

/// <summary>Options for <see cref="EnipClient"/>.</summary>
public sealed class EnipClientOptions : ITransportBuilder<EnipClientOptions>
{
    /// <summary>Transport (<c>UseTcp(host, 44818)</c> or <c>UseInMemory(listener)</c>).</summary>
    public TransportFactory? TransportFactory { get; set; }

    /// <summary>Route path through a backplane (port, slot …), e.g. <c>[1, 0]</c> for a ControlLogix CPU in slot 0; null sends to the target itself.</summary>
    public byte[]? Route { get; set; }

    /// <summary>Refuses Set_Attribute and tag writes (default true).</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>Response timeout (default 5 s).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public EnipClientOptions UseTransport(TransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }

    /// <summary>Routes requests to a backplane slot (port 1).</summary>
    public EnipClientOptions UseSlot(byte slot)
    {
        Route = [1, slot];
        return this;
    }

    /// <summary>Allows writes.</summary>
    public EnipClientOptions AllowWrites()
    {
        ReadOnly = false;
        return this;
    }
}

/// <summary>
/// EtherNet/IP explicit-messaging client (originator): registers a session over TCP 44818 and sends unconnected CIP
/// requests — Get/Set_Attribute, Get_Attributes_All, Logix Read/Write Tag and Multiple_Service_Packet — optionally
/// routed through a backplane with Unconnected_Send. Writes are refused until <see cref="EnipClientOptions.AllowWrites"/>.
/// </summary>
public sealed class EnipClient : EndpointBase, IClientEndpoint
{
    private readonly EnipClientOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITransport? _transport;
    private ulong _context;

    private EnipClient(EnipClientOptions options) : base("enip", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a client.</summary>
    public static EnipClient Create(Action<EnipClientOptions> configure)
    {
        var o = new EnipClientOptions();
        configure(o);
        if (o.TransportFactory is null) throw new ArgumentException("Set a transport: UseTcp(host, 44818) or UseInMemory(listener).");
        return new EnipClient(o);
    }

    /// <summary>Session handle assigned by the target.</summary>
    public uint Session { get; private set; }

    /// <summary>True when writes are refused.</summary>
    public bool ReadOnly => _options.ReadOnly;

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_transport is not null) return;
        SetState(EndpointState.Connecting);
        try
        {
            _transport = _options.TransportFactory!();
            await _transport.OpenAsync(ct).ConfigureAwait(false);
            var reply = await ExchangeAsync(new EnipPacket { Command = EnipCommand.RegisterSession, Data = EnipPacket.RegisterSessionData }, ct).ConfigureAwait(false);
            if (reply.Status != EnipStatus.Success || reply.Session == 0) throw new DeviceException($"RegisterSession refused: {reply.Status}.", (int)reply.Status);
            Session = reply.Session;
            SetState(EndpointState.Connected);
        }
        catch (Exception ex)
        {
            if (_transport is not null) await _transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
            SetState(EndpointState.Faulted, ex);
            throw;
        }
    }

    private async Task<EnipPacket> ExchangeAsync(EnipPacket request, CancellationToken ct)
    {
        var transport = _transport ?? throw new InvalidOperationException("Not connected. Call ConnectAsync first.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var context = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(context, ++_context);
            var bytes = (request with { Session = request.Command == EnipCommand.RegisterSession ? 0 : Session, SenderContext = context }).Encode();
            Tap(FrameDirection.Outbound, bytes, () => EnipPacket.Decode(bytes).Describe());
            await transport.Pipe.Output.WriteAsync(bytes, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.Timeout);
            while (true)
            {
                byte[] frame;
                try
                {
                    frame = await ReadFrameAsync(transport.Pipe.Input, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IoTComTimeoutException($"No answer to {request.Command} within {_options.Timeout.TotalSeconds:0} s.");
                }

                var reply = EnipPacket.Decode(frame);
                Tap(FrameDirection.Inbound, frame, reply.Describe);
                if (reply.SenderContext.AsSpan().SequenceEqual(context)) return reply;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static async Task<byte[]> ReadFrameAsync(PipeReader input, CancellationToken ct)
    {
        var head = new byte[EnipPacket.HeaderSize];
        while (true)
        {
            var result = await input.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (buffer.Length >= EnipPacket.HeaderSize)
            {
                buffer.Slice(0, EnipPacket.HeaderSize).CopyTo(head);
                var total = EnipPacket.FrameLength(head);
                if (buffer.Length >= total)
                {
                    var frame = buffer.Slice(0, total).ToArray();
                    input.AdvanceTo(buffer.GetPosition(total));
                    return frame;
                }
            }

            if (result.IsCompleted) throw new TransportException("The EtherNet/IP connection closed.");
            input.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    /// <summary>Sends one CIP request (routed when a route is configured) and returns the reply, success or not.</summary>
    public async Task<CipResponse> RequestAsync(CipRequest request, CancellationToken ct = default)
    {
        var cip = request.Encode();
        if (_options.Route is { } route) cip = UnconnectedSend(cip, route);
        var reply = await ExchangeAsync(new EnipPacket { Command = EnipCommand.SendRRData, Data = EnipPacket.SendRRData(cip) }, ct).ConfigureAwait(false);
        if (reply.Status != EnipStatus.Success) throw new DeviceException($"SendRRData refused: {reply.Status}.", (int)reply.Status);
        return CipResponse.Decode(EnipPacket.UnconnectedPayload(reply.Data));
    }

    /// <summary>Wraps a request in Unconnected_Send to the Connection Manager with a route path.</summary>
    public static byte[] UnconnectedSend(ReadOnlySpan<byte> embedded, ReadOnlySpan<byte> route)
    {
        var data = new List<byte> { 0x0A, 0x0E, (byte)embedded.Length, (byte)(embedded.Length >> 8) };   // priority/tick, timeout ticks, size
        data.AddRange(embedded.ToArray());
        if (embedded.Length % 2 == 1) data.Add(0);
        data.Add((byte)((route.Length + 1) / 2));
        data.Add(0);
        data.AddRange(route.ToArray());
        if (route.Length % 2 == 1) data.Add(0);
        return new CipRequest(CipService.UnconnectedSend, CipPath.Logical(CipClass.ConnectionManager, 1), [.. data]).Encode();
    }

    private void EnsureWrites(string what)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException($"{what} changes the controller; this client is read-only until AllowWrites().");
    }

    /// <summary>Get_Attribute_Single.</summary>
    public async Task<byte[]> GetAttributeSingleAsync(ushort cls, uint instance, ushort attribute, CancellationToken ct = default) =>
        (await RequestAsync(new CipRequest(CipService.GetAttributeSingle, CipPath.Logical(cls, instance, attribute), []), ct).ConfigureAwait(false)).EnsureSuccess().Data;

    /// <summary>Get_Attributes_All.</summary>
    public async Task<byte[]> GetAttributesAllAsync(ushort cls, uint instance, CancellationToken ct = default) =>
        (await RequestAsync(new CipRequest(CipService.GetAttributesAll, CipPath.Logical(cls, instance), []), ct).ConfigureAwait(false)).EnsureSuccess().Data;

    /// <summary>Set_Attribute_Single (refused in read-only mode).</summary>
    public async Task SetAttributeSingleAsync(ushort cls, uint instance, ushort attribute, byte[] data, CancellationToken ct = default)
    {
        EnsureWrites($"Set_Attribute_Single on class 0x{cls:X2}");
        (await RequestAsync(new CipRequest(CipService.SetAttributeSingle, CipPath.Logical(cls, instance, attribute), data), ct).ConfigureAwait(false)).EnsureSuccess();
    }

    /// <summary>Reads the Identity object (class 1, instance 1).</summary>
    public async Task<EnipIdentity> ReadIdentityAsync(CancellationToken ct = default)
    {
        var d = await GetAttributesAllAsync(CipClass.Identity, 1, ct).ConfigureAwait(false);
        return EnipIdentity.Decode([.. new byte[18], .. d, 3]) with { Address = null };
    }

    /// <summary>Logix Read Tag (0x4C).</summary>
    public async Task<CipTagValue> ReadTagAsync(string tag, ushort count = 1, CancellationToken ct = default)
    {
        var reply = (await RequestAsync(new CipRequest(CipService.ReadTag, CipPath.Tag(tag), [(byte)count, (byte)(count >> 8)]), ct).ConfigureAwait(false)).EnsureSuccess();
        return ParseTagReply(reply.Data);
    }

    private static CipTagValue ParseTagReply(byte[] d)
    {
        if (d.Length < 2) throw new ProtocolException("Read Tag reply without a data type.");
        var type = (CipDataType)BinaryPrimitives.ReadUInt16LittleEndian(d);
        if (!Enum.IsDefined(type)) throw new ProtocolException($"Tag data type 0x{(ushort)type:X4} is not an elementary type (structures are not supported).");
        return new CipTagValue(type, d[2..]);
    }

    /// <summary>Logix Write Tag (0x4D), refused in read-only mode.</summary>
    public async Task WriteTagAsync(string tag, CipTagValue value, CancellationToken ct = default)
    {
        EnsureWrites($"Writing tag {tag}");
        var data = new byte[4 + value.Data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)value.Type);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), (ushort)value.Count);
        value.Data.CopyTo(data, 4);
        (await RequestAsync(new CipRequest(CipService.WriteTag, CipPath.Tag(tag), data), ct).ConfigureAwait(false)).EnsureSuccess();
    }

    /// <summary>Reads several tags in one Multiple_Service_Packet; a tag that fails yields its <see cref="CipException"/>.</summary>
    public async Task<IReadOnlyList<(string Tag, CipTagValue? Value, CipException? Error)>> ReadTagsAsync(IReadOnlyList<string> tags, CancellationToken ct = default)
    {
        var services = tags.Select(t => new CipRequest(CipService.ReadTag, CipPath.Tag(t), [1, 0]).Encode()).ToList();
        var reply = (await RequestAsync(new CipRequest(CipService.MultipleServicePacket, CipPath.Logical(CipClass.MessageRouter, 1), MultipleServicePacket(services)), ct).ConfigureAwait(false));
        if (reply.Status is not (CipStatus.Success or CipStatus.EmbeddedServiceError)) reply.EnsureSuccess();
        var replies = SplitMultiple(reply.Data);
        return [.. tags.Select((t, i) =>
        {
            var r = CipResponse.Decode(replies[i]);
            return r.Status == CipStatus.Success ? (t, (CipTagValue?)ParseTagReply(r.Data), (CipException?)null) : (t, null, new CipException(r.Status, [.. r.Additional]));
        })];
    }

    /// <summary>Builds Multiple_Service_Packet data: count, offsets (from the count), services.</summary>
    public static byte[] MultipleServicePacket(IReadOnlyList<byte[]> services)
    {
        var header = 2 + (2 * services.Count);
        var b = new List<byte> { (byte)services.Count, (byte)(services.Count >> 8) };
        var offset = header;
        foreach (var s in services)
        {
            b.Add((byte)offset);
            b.Add((byte)(offset >> 8));
            offset += s.Length;
        }

        foreach (var s in services) b.AddRange(s);
        return [.. b];
    }

    /// <summary>Splits Multiple_Service_Packet data into its services or replies.</summary>
    public static IReadOnlyList<byte[]> SplitMultiple(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2) throw new ProtocolException("Multiple_Service_Packet without a count.");
        var count = BinaryPrimitives.ReadUInt16LittleEndian(d);
        if (d.Length < 2 + (2 * count)) throw new ProtocolException("Multiple_Service_Packet offsets truncated.");
        var offsets = new int[count + 1];
        for (var i = 0; i < count; i++) offsets[i] = BinaryPrimitives.ReadUInt16LittleEndian(d[(2 + (2 * i))..]);
        offsets[count] = d.Length;
        var parts = new List<byte[]>();
        for (var i = 0; i < count; i++)
        {
            if (offsets[i] > offsets[i + 1] || offsets[i] < 2 + (2 * count) || offsets[i + 1] > d.Length) throw new ProtocolException("Multiple_Service_Packet offsets out of order.");
            parts.Add(d[offsets[i]..offsets[i + 1]].ToArray());
        }

        return parts;
    }

    /// <summary>ListIdentity over this TCP session.</summary>
    public async Task<EnipIdentity> ListIdentityAsync(CancellationToken ct = default)
    {
        var reply = await ExchangeAsync(new EnipPacket { Command = EnipCommand.ListIdentity }, ct).ConfigureAwait(false);
        return EnipIdentity.Decode(EnipPacket.DecodeCpf(reply.Data)[0].Data);
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        var t = Interlocked.Exchange(ref _transport, null);
        if (t is null) return;
        try
        {
            var bytes = new EnipPacket { Command = EnipCommand.UnregisterSession, Session = Session }.Encode();
            Tap(FrameDirection.Outbound, bytes, () => "UnregisterSession");
            await t.Pipe.Output.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TransportException)
        {
        }

        await t.DisposeAsync().ConfigureAwait(false);
        Session = 0;
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

/// <summary>Finds EtherNet/IP devices with a ListIdentity broadcast on UDP 44818.</summary>
public static class EnipDiscovery
{
    /// <summary>Broadcasts ListIdentity and collects the answers for <paramref name="window"/> (default 2 s).</summary>
    public static async Task<IReadOnlyList<EnipIdentity>> ListIdentityAsync(IDatagramTransport? transport = null, EndPoint? broadcast = null, TimeSpan? window = null, CancellationToken ct = default)
    {
        var own = transport is null;
        if (transport is null)
        {
            var udp = new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
            transport = udp;
        }

        var found = new Dictionary<string, EnipIdentity>(StringComparer.Ordinal);
        try
        {
            await transport.SendAsync(new EnipPacket { Command = EnipCommand.ListIdentity }.Encode(), broadcast ?? new IPEndPoint(IPAddress.Broadcast, EnipPacket.Port), ct).ConfigureAwait(false);
            using var w = CancellationTokenSource.CreateLinkedTokenSource(ct);
            w.CancelAfter(window ?? TimeSpan.FromSeconds(2));
            while (true)
            {
                Datagram d;
                try
                {
                    d = await transport.ReceiveAsync(w.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    var p = EnipPacket.Decode(d.Data.Span);
                    if (p.Command != EnipCommand.ListIdentity || p.Data.Length == 0) continue;
                    var id = EnipIdentity.Decode(EnipPacket.DecodeCpf(p.Data)[0].Data);
                    if (id.Address is { } a && a.Address.Equals(IPAddress.Any) && d.Remote is IPEndPoint r) id = id with { Address = new IPEndPoint(r.Address, a.Port) };
                    found[$"{id.VendorId}/{id.SerialNumber}/{id.Address}"] = id;
                }
                catch (ProtocolException)
                {
                }
            }
        }
        finally
        {
            if (own) await transport.DisposeAsync().ConfigureAwait(false);
        }

        return [.. found.Values];
    }
}
