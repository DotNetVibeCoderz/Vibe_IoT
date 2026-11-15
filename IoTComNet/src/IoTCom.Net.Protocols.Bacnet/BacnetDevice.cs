using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Bacnet;

/// <summary>Options for <see cref="BacnetDevice"/>.</summary>
public sealed class BacnetDeviceOptions : IDatagramBuilder<BacnetDeviceOptions>
{
    /// <summary>BACnet/IP UDP port (47808, 0xBAC0).</summary>
    public const int DefaultPort = 47808;

    /// <summary>Socket (default UDP 0.0.0.0:47808 with broadcast enabled).</summary>
    public DatagramTransportFactory? TransportFactory { get; set; }

    /// <summary>Device instance (0–4194302).</summary>
    public uint DeviceInstance { get; set; } = 2604;

    /// <summary>Device object name.</summary>
    public string DeviceName { get; set; } = "IoTCom BACnet device";

    /// <summary>Vendor name.</summary>
    public string VendorName { get; set; } = "Gravicode Studios";

    /// <summary>Vendor identifier. Products need an identifier assigned by ASHRAE; 999 is used for tests and demos.</summary>
    public uint VendorId { get; set; } = 999;

    /// <summary>Model name.</summary>
    public string ModelName { get; set; } = "IoTCom.Net BacnetDevice";

    /// <summary>Firmware revision.</summary>
    public string FirmwareRevision { get; set; } = "1.0";

    /// <summary>Application software version.</summary>
    public string ApplicationSoftwareVersion { get; set; } = IoTComInfo.Version;

    /// <summary>Device description.</summary>
    public string Description { get; set; } = "";

    /// <summary>Where broadcasts (I-Am) go: 255.255.255.255:47808 by default; on an in-memory network use a multicast group.</summary>
    public EndPoint BroadcastAddress { get; set; } = new IPEndPoint(IPAddress.Broadcast, DefaultPort);

    /// <summary>Also answer Who-Is with a unicast I-Am to the requester (helps where broadcasts do not arrive; default false).</summary>
    public bool UnicastIAm { get; set; }

    /// <summary>Broadcast I-Am when the device starts (default true).</summary>
    public bool AnnounceOnStart { get; set; } = true;

    /// <summary>Refuse every WriteProperty with write-access-denied (default false).</summary>
    public bool WriteProtected { get; set; }

    /// <summary>Largest APDU (default 1476; segmentation is not supported, so larger answers are aborted).</summary>
    public int MaxApdu { get; set; } = 1476;

    /// <summary>Wait for the subscriber's SimpleACK to a confirmed COV notification (default 3 s, one retry).</summary>
    public TimeSpan ApduTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public BacnetDeviceOptions UseDatagramTransport(DatagramTransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }
}

/// <summary>A COV subscription held by the device.</summary>
public sealed class BacnetCovSubscription
{
    internal BacnetCovSubscription(EndPoint subscriber, uint processId, BacnetObjectId obj, bool confirmed, uint lifetime) =>
        (Subscriber, ProcessId, Object, Confirmed, Lifetime) = (subscriber, processId, obj, confirmed, lifetime);

    /// <summary>Subscriber address.</summary>
    public EndPoint Subscriber { get; }

    /// <summary>Subscriber process identifier.</summary>
    public uint ProcessId { get; }

    /// <summary>Monitored object.</summary>
    public BacnetObjectId Object { get; }

    /// <summary>Confirmed notifications.</summary>
    public bool Confirmed { get; internal set; }

    /// <summary>Lifetime in seconds (0 = indefinite).</summary>
    public uint Lifetime { get; internal set; }

    /// <summary>When it expires (null = never).</summary>
    public DateTimeOffset? Expires { get; internal set; }

    internal double LastValue = double.NaN;
    internal string LastFlags = "";

    /// <inheritdoc />
    public override string ToString() => $"{Object.ShortName} → {Subscriber} pid {ProcessId} ({(Confirmed ? "confirmed" : "unconfirmed")}{(Expires is { } e ? $", until {e:HH:mm:ss}" : "")})";
}

/// <summary>
/// A BACnet/IP device (server side, ASHRAE 135 Annex J): answers Who-Is and Who-Has, ReadProperty and
/// ReadPropertyMultiple (including ALL/REQUIRED/OPTIONAL), WriteProperty with priorities, and SubscribeCOV with
/// confirmed or unconfirmed notifications, for a Device object and the <see cref="BacnetObject"/>s you add.
/// </summary>
public sealed class BacnetDevice : EndpointBase, IServerEndpoint
{
    private readonly BacnetDeviceOptions _options;
    private readonly ConcurrentDictionary<BacnetObjectId, BacnetObject> _objects = new();
    private readonly ConcurrentDictionary<(string, uint, BacnetObjectId), BacnetCovSubscription> _subscriptions = new();
    private readonly ConcurrentDictionary<byte, TaskCompletionSource<BacnetPacket>> _pending = new();
    private readonly Channel<(BacnetObject, BacnetPropertyId)> _changes = Channel.CreateUnbounded<(BacnetObject, BacnetPropertyId)>(new UnboundedChannelOptions { SingleReader = true });
    private IDatagramTransport? _transport;
    private CancellationTokenSource? _cts;
    private Task? _receive, _notify, _expire;
    private int _invoke;
    private uint _databaseRevision = 1;

    private BacnetDevice(BacnetDeviceOptions options) : base("bacnet", options.Logger)
    {
        _options = options;
        Name = options.Name ?? options.DeviceName;
    }

    /// <summary>Creates a device.</summary>
    public static BacnetDevice Create(Action<BacnetDeviceOptions> configure)
    {
        var o = new BacnetDeviceOptions();
        configure(o);
        if (o.DeviceInstance >= BacnetObjectId.MaxInstance) throw new ArgumentOutOfRangeException(nameof(configure), "Device instance must be 0–4194302.");
        return new BacnetDevice(o);
    }

    /// <summary>Device instance.</summary>
    public uint DeviceInstance => _options.DeviceInstance;

    /// <summary>Device object identifier.</summary>
    public BacnetObjectId DeviceId => new(BacnetObjectType.Device, _options.DeviceInstance);

    /// <summary>Hosted objects (the Device object is implicit).</summary>
    public IReadOnlyCollection<BacnetObject> Objects => [.. _objects.Values.OrderBy(o => o.Id)];

    /// <summary>Active COV subscriptions.</summary>
    public IReadOnlyCollection<BacnetCovSubscription> Subscriptions => [.. _subscriptions.Values];

    /// <summary>The bound endpoint (after start).</summary>
    public EndPoint? LocalEndPoint => _transport?.LocalEndPoint;

    /// <summary>Raised after a client wrote a property (object, property, priority).</summary>
    public event Action<BacnetObject, BacnetPropertyId, byte?>? PropertyWritten;

    /// <summary>Adds an object (names and identifiers must be unique).</summary>
    public BacnetObject Add(BacnetObject obj)
    {
        if (obj.Id.Type == BacnetObjectType.Device) throw new ArgumentException("The Device object is created by BacnetDevice.", nameof(obj));
        if (_objects.Values.Any(o => o.Name == obj.Name)) throw new ArgumentException($"An object named '{obj.Name}' already exists.", nameof(obj));
        if (!_objects.TryAdd(obj.Id, obj)) throw new ArgumentException($"{obj.Id} already exists.", nameof(obj));
        obj.Changed += (o, p) => _changes.Writer.TryWrite((o, p));
        _databaseRevision++;
        return obj;
    }

    /// <summary>Finds an object.</summary>
    public BacnetObject? Find(BacnetObjectId id) => _objects.TryGetValue(id, out var o) ? o : null;

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_transport is not null) return;
        SetState(EndpointState.Connecting);
        _transport = (_options.TransportFactory ?? (() =>
        {
            var udp = new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, BacnetDeviceOptions.DefaultPort), reuseAddress: true);
            udp.EnableBroadcast = true;
            return udp;
        }))();
        if (_transport is InMemoryDatagramTransport m && _options.BroadcastAddress is IPEndPoint group && IsMulticast(group.Address)) m.JoinMulticastGroup(group.Address);
        _cts = new CancellationTokenSource();
        _receive = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        _notify = Task.Run(() => NotifyLoopAsync(_cts.Token), CancellationToken.None);
        _expire = Task.Run(() => ExpireLoopAsync(_cts.Token), CancellationToken.None);
        SetState(EndpointState.Listening);
        if (_options.AnnounceOnStart) await BroadcastIAmAsync(ct).ConfigureAwait(false);
    }

    private static bool IsMulticast(IPAddress a) => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && (a.GetAddressBytes()[0] & 0xF0) == 0xE0;

    /// <summary>Broadcasts I-Am.</summary>
    public Task BroadcastIAmAsync(CancellationToken ct = default) => SendAsync(IAm(BacnetBvlcFunction.OriginalBroadcastNpdu), _options.BroadcastAddress, ct);

    private BacnetPacket IAm(BacnetBvlcFunction function) => new()
    {
        Function = function, PduType = BacnetPduType.UnconfirmedRequest, Service = (byte)BacnetUnconfirmedService.IAm,
        Data = BacnetServices.IAm(_options.DeviceInstance, _options.MaxApdu, BacnetSegmentation.None, _options.VendorId),
    };

    private async Task SendAsync(BacnetPacket packet, EndPoint to, CancellationToken ct)
    {
        var bytes = packet.Encode();
        Tap(FrameDirection.Outbound, bytes, packet.Describe);
        try
        {
            await _transport!.SendAsync(bytes, to, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or TransportException)
        {
            Logger.LogDebug(ex, "BACnet send to {To} failed", to);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Datagram d;
            try
            {
                d = await _transport!.ReceiveAsync(ct).ConfigureAwait(false);
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

            BacnetPacket p;
            try
            {
                p = BacnetPacket.Decode(d.Data.Span);
            }
            catch (ProtocolException ex)
            {
                Tap(FrameDirection.Inbound, d.Data.Span, () => "invalid: " + ex.Message);
                continue;
            }

            if (d.Remote is IPEndPoint self && _transport.LocalEndPoint is IPEndPoint local && self.Port == local.Port && self.Address.Equals(local.Address)) continue;
            Tap(FrameDirection.Inbound, d.Data.Span, p.Describe);
            var replyTo = p.ForwardedFrom ?? d.Remote;
            try
            {
                await HandleAsync(p, replyTo, ct).ConfigureAwait(false);
            }
            catch (ProtocolException ex) when (p.PduType == BacnetPduType.ConfirmedRequest)
            {
                Logger.LogDebug(ex, "BACnet: bad request from {Remote}", replyTo);
                await RejectAsync(p, BacnetRejectReason.InvalidTag, replyTo, ct).ConfigureAwait(false);
            }
            catch (ProtocolException ex)
            {
                Logger.LogDebug(ex, "BACnet: bad message from {Remote}", replyTo);
            }
        }
    }

    private Task RejectAsync(BacnetPacket request, BacnetRejectReason reason, EndPoint to, CancellationToken ct) =>
        SendAsync(new BacnetPacket { PduType = BacnetPduType.Reject, InvokeId = request.InvokeId, Reason = (byte)reason }, to, ct);

    private Task ErrorAsync(BacnetPacket request, BacnetErrorClass cls, BacnetErrorCode code, EndPoint to, CancellationToken ct) =>
        SendAsync(new BacnetPacket { PduType = BacnetPduType.Error, InvokeId = request.InvokeId, Service = request.Service, ErrorClass = cls, ErrorCode = code }, to, ct);

    private async Task HandleAsync(BacnetPacket p, EndPoint from, CancellationToken ct)
    {
        if (p.NetworkMessage) return;
        switch (p.PduType)
        {
            case BacnetPduType.UnconfirmedRequest when p.Service == (byte)BacnetUnconfirmedService.WhoIs:
            {
                var (low, high) = BacnetServices.DecodeWhoIs(p.Data);
                if (low is null || (_options.DeviceInstance >= low && _options.DeviceInstance <= high))
                {
                    await BroadcastIAmAsync(ct).ConfigureAwait(false);
                    if (_options.UnicastIAm) await SendAsync(IAm(BacnetBvlcFunction.OriginalUnicastNpdu), from, ct).ConfigureAwait(false);
                }

                break;
            }

            case BacnetPduType.UnconfirmedRequest when p.Service == (byte)BacnetUnconfirmedService.WhoHas:
            {
                var (low, high, objId, name) = BacnetServices.DecodeWhoHas(p.Data);
                if (low is not null && (_options.DeviceInstance < low || _options.DeviceInstance > high)) break;
                var found = objId is { } id ? (id == DeviceId ? (BacnetObjectId?)DeviceId : Find(id)?.Id) : name == _options.DeviceName ? DeviceId : _objects.Values.FirstOrDefault(o => o.Name == name)?.Id;
                if (found is { } f)
                {
                    var n = f == DeviceId ? _options.DeviceName : _objects[f].Name;
                    await SendAsync(new BacnetPacket { Function = BacnetBvlcFunction.OriginalBroadcastNpdu, PduType = BacnetPduType.UnconfirmedRequest, Service = (byte)BacnetUnconfirmedService.IHave, Data = BacnetServices.IHave(_options.DeviceInstance, f, n) }, _options.BroadcastAddress, ct).ConfigureAwait(false);
                }

                break;
            }

            case BacnetPduType.SimpleAck or BacnetPduType.Error or BacnetPduType.Reject or BacnetPduType.Abort:
                if (_pending.TryRemove(p.InvokeId, out var tcs)) tcs.TrySetResult(p);
                break;
            case BacnetPduType.ConfirmedRequest:
                if (p.Segmented)
                {
                    await SendAsync(new BacnetPacket { PduType = BacnetPduType.Abort, InvokeId = p.InvokeId, Reason = (byte)BacnetAbortReason.SegmentationNotSupported }, from, ct).ConfigureAwait(false);
                    break;
                }

                await ConfirmedAsync(p, from, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task ConfirmedAsync(BacnetPacket p, EndPoint from, CancellationToken ct)
    {
        byte[] ack;
        try
        {
            switch ((BacnetConfirmedService)p.Service)
            {
                case BacnetConfirmedService.ReadProperty:
                {
                    var (obj, prop) = BacnetServices.DecodeReadProperty(p.Data);
                    ack = BacnetServices.ReadPropertyAck(obj, prop, ReadProperty(obj, prop));
                    break;
                }

                case BacnetConfirmedService.ReadPropertyMultiple:
                {
                    var specs = BacnetServices.DecodeReadPropertyMultiple(p.Data);
                    ack = BacnetServices.ReadPropertyMultipleAck(specs.Select(s => (s.Object, (IReadOnlyList<BacnetPropertyResult>)ReadMultiple(s.Object, s.Properties))).ToList());
                    break;
                }

                case BacnetConfirmedService.WriteProperty:
                {
                    var (obj, prop, values, priority) = BacnetServices.DecodeWriteProperty(p.Data);
                    WriteProperty(obj, prop, values, priority);
                    await SendAsync(new BacnetPacket { PduType = BacnetPduType.SimpleAck, InvokeId = p.InvokeId, Service = p.Service }, from, ct).ConfigureAwait(false);
                    return;
                }

                case BacnetConfirmedService.SubscribeCov:
                {
                    var (pid, obj, confirmed, lifetime) = BacnetServices.DecodeSubscribeCov(p.Data);
                    var sub = Subscribe(from, pid, obj, confirmed, lifetime);
                    await SendAsync(new BacnetPacket { PduType = BacnetPduType.SimpleAck, InvokeId = p.InvokeId, Service = p.Service }, from, ct).ConfigureAwait(false);
                    if (sub is not null) _ = NotifyAsync(sub, _objects[obj], ct);   // the initial notification
                    return;
                }

                default:
                    await RejectAsync(p, BacnetRejectReason.UnrecognizedService, from, ct).ConfigureAwait(false);
                    return;
            }
        }
        catch (BacnetErrorException ex)
        {
            await ErrorAsync(p, ex.ErrorClass, ex.ErrorCode, from, ct).ConfigureAwait(false);
            return;
        }

        var reply = new BacnetPacket { PduType = BacnetPduType.ComplexAck, InvokeId = p.InvokeId, Service = p.Service, Data = ack };
        if (ack.Length + 3 > Math.Min(p.MaxApdu, _options.MaxApdu))
        {
            await SendAsync(new BacnetPacket { PduType = BacnetPduType.Abort, InvokeId = p.InvokeId, Reason = (byte)BacnetAbortReason.SegmentationNotSupported }, from, ct).ConfigureAwait(false);
            return;
        }

        await SendAsync(reply, from, ct).ConfigureAwait(false);
    }

    // ---- properties ---------------------------------------------------------------------------------------------------

    private static readonly int[] ServicesSupported =
    [
        (int)BacnetConfirmedService.ConfirmedCovNotification, (int)BacnetConfirmedService.SubscribeCov, (int)BacnetConfirmedService.ReadProperty,
        (int)BacnetConfirmedService.ReadPropertyMultiple, (int)BacnetConfirmedService.WriteProperty, 26 /* i-Am */, 27 /* i-Have */,
        28 /* unconfirmedCOVNotification */, 33 /* who-Has */, 34 /* who-Is */,
    ];

    private static readonly BacnetPropertyId[] DeviceProperties =
    [
        BacnetPropertyId.ObjectIdentifier, BacnetPropertyId.ObjectName, BacnetPropertyId.ObjectType, BacnetPropertyId.SystemStatus, BacnetPropertyId.VendorName,
        BacnetPropertyId.VendorIdentifier, BacnetPropertyId.ModelName, BacnetPropertyId.FirmwareRevision, BacnetPropertyId.ApplicationSoftwareVersion,
        BacnetPropertyId.Description, BacnetPropertyId.ProtocolVersion, BacnetPropertyId.ProtocolRevision, BacnetPropertyId.ProtocolServicesSupported,
        BacnetPropertyId.ProtocolObjectTypesSupported, BacnetPropertyId.ObjectList, BacnetPropertyId.MaxApduLengthAccepted, BacnetPropertyId.SegmentationSupported,
        BacnetPropertyId.ApduTimeout, BacnetPropertyId.NumberOfApduRetries, BacnetPropertyId.DeviceAddressBinding, BacnetPropertyId.DatabaseRevision,
        BacnetPropertyId.PropertyList,
    ];

    private IReadOnlyList<BacnetValue> ReadDevice(BacnetPropertyReference p)
    {
        IReadOnlyList<BacnetValue> all = p.Property switch
        {
            BacnetPropertyId.ObjectIdentifier => [BacnetValue.Object(DeviceId)],
            BacnetPropertyId.ObjectName => [BacnetValue.Text(_options.DeviceName)],
            BacnetPropertyId.ObjectType => [BacnetValue.Enumerated((uint)BacnetObjectType.Device)],
            BacnetPropertyId.SystemStatus => [BacnetValue.Enumerated(0)],
            BacnetPropertyId.VendorName => [BacnetValue.Text(_options.VendorName)],
            BacnetPropertyId.VendorIdentifier => [BacnetValue.Unsigned(_options.VendorId)],
            BacnetPropertyId.ModelName => [BacnetValue.Text(_options.ModelName)],
            BacnetPropertyId.FirmwareRevision => [BacnetValue.Text(_options.FirmwareRevision)],
            BacnetPropertyId.ApplicationSoftwareVersion => [BacnetValue.Text(_options.ApplicationSoftwareVersion)],
            BacnetPropertyId.Description => [BacnetValue.Text(_options.Description)],
            BacnetPropertyId.ProtocolVersion => [BacnetValue.Unsigned(1)],
            BacnetPropertyId.ProtocolRevision => [BacnetValue.Unsigned(22)],
            BacnetPropertyId.ProtocolServicesSupported => [BacnetValue.Bits(BacnetBitString.Of(44, ServicesSupported))],
            BacnetPropertyId.ProtocolObjectTypesSupported => [BacnetValue.Bits(BacnetBitString.Of(59, [.. _objects.Keys.Select(k => (int)k.Type).Append((int)BacnetObjectType.Device).Distinct()]))],
            BacnetPropertyId.ObjectList => [BacnetValue.Object(DeviceId), .. _objects.Keys.Order().Select(BacnetValue.Object)],
            BacnetPropertyId.MaxApduLengthAccepted => [BacnetValue.Unsigned((uint)_options.MaxApdu)],
            BacnetPropertyId.SegmentationSupported => [BacnetValue.Enumerated((uint)BacnetSegmentation.None)],
            BacnetPropertyId.ApduTimeout => [BacnetValue.Unsigned((uint)_options.ApduTimeout.TotalMilliseconds)],
            BacnetPropertyId.NumberOfApduRetries => [BacnetValue.Unsigned(1)],
            BacnetPropertyId.DeviceAddressBinding => [],
            BacnetPropertyId.DatabaseRevision => [BacnetValue.Unsigned(_databaseRevision)],
            BacnetPropertyId.PropertyList => [.. DeviceProperties.Where(x => x is not (BacnetPropertyId.ObjectIdentifier or BacnetPropertyId.ObjectName or BacnetPropertyId.ObjectType or BacnetPropertyId.PropertyList)).Select(x => BacnetValue.Enumerated((uint)x))],
            _ => throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.UnknownProperty, $"The Device object has no property {p.Property}."),
        };
        return BacnetObject.Index(p, all, p.Property is BacnetPropertyId.ObjectList or BacnetPropertyId.PropertyList);
    }

    /// <summary>Reads a property as a client would (also usable locally).</summary>
    public IReadOnlyList<BacnetValue> ReadProperty(BacnetObjectId obj, BacnetPropertyReference p)
    {
        if (p.Property is BacnetPropertyId.All or BacnetPropertyId.Required or BacnetPropertyId.Optional)
            throw new BacnetErrorException(BacnetErrorClass.Services, BacnetErrorCode.InvalidDataType, $"{p.Property} is only valid in ReadPropertyMultiple.");
        if (IsDevice(obj)) return ReadDevice(p);
        return (Find(obj) ?? throw new BacnetErrorException(BacnetErrorClass.Object, BacnetErrorCode.UnknownObject, $"{obj} does not exist.")).Read(p);
    }

    private bool IsDevice(BacnetObjectId obj) => obj.Type == BacnetObjectType.Device && (obj.Instance == _options.DeviceInstance || obj.Instance == BacnetObjectId.MaxInstance);

    private List<BacnetPropertyResult> ReadMultiple(BacnetObjectId obj, IReadOnlyList<BacnetPropertyReference> props)
    {
        var results = new List<BacnetPropertyResult>();
        var device = IsDevice(obj);
        var target = device ? null : Find(obj);
        foreach (var p in props)
        {
            if (!device && target is null)
            {
                results.Add(new BacnetPropertyResult(p, null, BacnetErrorClass.Object, BacnetErrorCode.UnknownObject));
                continue;
            }

            if (p.Property is BacnetPropertyId.All or BacnetPropertyId.Required or BacnetPropertyId.Optional)
            {
                var list = device ? DeviceProperties : target!.Properties;
                foreach (var each in list)
                    results.Add(new BacnetPropertyResult(new BacnetPropertyReference(each), device ? ReadDevice(new(each)) : target!.Read(new(each))));
                continue;
            }

            try
            {
                results.Add(new BacnetPropertyResult(p, device ? ReadDevice(p) : target!.Read(p)));
            }
            catch (BacnetErrorException ex)
            {
                results.Add(new BacnetPropertyResult(p, null, ex.ErrorClass, ex.ErrorCode));
            }
        }

        return results;
    }

    private void WriteProperty(BacnetObjectId obj, BacnetPropertyReference p, IReadOnlyList<BacnetValue> values, byte? priority)
    {
        if (IsDevice(obj)) throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.WriteAccessDenied, "Device object properties are read-only.");
        var target = Find(obj) ?? throw new BacnetErrorException(BacnetErrorClass.Object, BacnetErrorCode.UnknownObject, $"{obj} does not exist.");
        if (_options.WriteProtected) throw new BacnetErrorException(BacnetErrorClass.Property, BacnetErrorCode.WriteAccessDenied, "The device is write-protected.");
        target.Write(p, values, priority);
        PropertyWritten?.Invoke(target, p.Property, priority);
    }

    // ---- COV ----------------------------------------------------------------------------------------------------------

    private BacnetCovSubscription? Subscribe(EndPoint from, uint pid, BacnetObjectId obj, bool? confirmed, uint? lifetime)
    {
        if (Find(obj) is null) throw new BacnetErrorException(BacnetErrorClass.Object, BacnetErrorCode.UnknownObject, $"{obj} does not exist.");
        var key = (from.ToString()!, pid, obj);
        if (confirmed is null)
        {
            _subscriptions.TryRemove(key, out _);
            return null;
        }

        var sub = _subscriptions.AddOrUpdate(key, _ => new BacnetCovSubscription(from, pid, obj, confirmed.Value, lifetime ?? 0), (_, s) =>
        {
            s.Confirmed = confirmed.Value;
            s.Lifetime = lifetime ?? 0;
            return s;
        });
        sub.Expires = sub.Lifetime == 0 ? null : DateTimeOffset.UtcNow.AddSeconds(sub.Lifetime);
        sub.LastValue = double.NaN;
        return sub;
    }

    private async Task NotifyLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (obj, _) in _changes.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                foreach (var sub in _subscriptions.Values.Where(s => s.Object == obj.Id))
                {
                    var value = obj.Value;
                    var flags = obj.StatusFlags.ToString();
                    var significant = flags != sub.LastFlags || double.IsNaN(sub.LastValue)
                        || (obj.CovIncrement is { } inc && obj.Id.Type is BacnetObjectType.AnalogInput or BacnetObjectType.AnalogOutput or BacnetObjectType.AnalogValue
                            ? Math.Abs(value - sub.LastValue) >= inc
                            : value != sub.LastValue);
                    if (significant) await NotifyAsync(sub, obj, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task NotifyAsync(BacnetCovSubscription sub, BacnetObject obj, CancellationToken ct)
    {
        sub.LastValue = obj.Value;
        sub.LastFlags = obj.StatusFlags.ToString();
        var remaining = sub.Expires is { } e ? (uint)Math.Max(0, (e - DateTimeOffset.UtcNow).TotalSeconds) : 0;
        var data = BacnetServices.CovNotification(new BacnetCovNotification(sub.ProcessId, DeviceId, obj.Id, remaining,
        [
            new BacnetPropertyValue(new(BacnetPropertyId.PresentValue), [obj.PresentValue]),
            new BacnetPropertyValue(new(BacnetPropertyId.StatusFlags), [BacnetValue.Bits(obj.StatusFlags)]),
        ]));
        if (!sub.Confirmed)
        {
            await SendAsync(new BacnetPacket { PduType = BacnetPduType.UnconfirmedRequest, Service = (byte)BacnetUnconfirmedService.UnconfirmedCovNotification, Data = data }, sub.Subscriber, ct).ConfigureAwait(false);
            return;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var id = (byte)Interlocked.Increment(ref _invoke);
            var tcs = new TaskCompletionSource<BacnetPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            await SendAsync(new BacnetPacket { PduType = BacnetPduType.ConfirmedRequest, ExpectingReply = true, InvokeId = id, Service = (byte)BacnetConfirmedService.ConfirmedCovNotification, Data = data }, sub.Subscriber, ct).ConfigureAwait(false);
            try
            {
                await tcs.Task.WaitAsync(_options.ApduTimeout, ct).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                _pending.TryRemove(id, out _);
            }
        }

        Logger.LogDebug("BACnet: subscriber {Subscriber} did not acknowledge a COV notification", sub.Subscriber);
    }

    private async Task ExpireLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                foreach (var (key, sub) in _subscriptions)
                    if (sub.Expires is { } e && e < DateTimeOffset.UtcNow) _subscriptions.TryRemove(key, out _);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        if (_transport is null) return;
        SetState(EndpointState.Stopping);
        await (_cts?.CancelAsync() ?? Task.CompletedTask).ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        foreach (var t in new[] { _receive, _notify, _expire })
            if (t is not null) await t.ConfigureAwait(false);
        _transport = null;
        _cts?.Dispose();
        _cts = null;
        _subscriptions.Clear();
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);
}
