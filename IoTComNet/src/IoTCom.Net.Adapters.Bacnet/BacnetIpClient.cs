using System.Collections.Concurrent;
using System.Net;
using IoTCom.Net.Protocols.Bacnet;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;
using Lib = System.IO.BACnet;

namespace IoTCom.Net.Adapters.Bacnet;

/// <summary>A device found by Who-Is.</summary>
/// <param name="Instance">Device instance.</param>
/// <param name="Address">BACnet/IP address.</param>
/// <param name="MaxApdu">Largest APDU it accepts.</param>
/// <param name="Segmentation">Segmentation support.</param>
/// <param name="VendorId">Vendor identifier.</param>
public sealed record BacnetDeviceInfo(uint Instance, IPEndPoint Address, int MaxApdu, BacnetSegmentation Segmentation, uint VendorId)
{
    /// <inheritdoc />
    public override string ToString() => $"device {Instance} at {Address} (vendor {VendorId}, max APDU {MaxApdu}, segmentation {Segmentation})";
}

/// <summary>Options for <see cref="BacnetIpClient"/>.</summary>
public sealed class BacnetIpClientOptions : IDatagramBuilder<BacnetIpClientOptions>
{
    /// <summary>Socket (default UDP 0.0.0.0:47808 shared, broadcast enabled).</summary>
    public DatagramTransportFactory? TransportFactory { get; set; }

    /// <summary>Where Who-Is goes (default 255.255.255.255:47808; on an in-memory network a multicast group).</summary>
    public IPEndPoint BroadcastAddress { get; set; } = new(IPAddress.Broadcast, BacnetDeviceOptions.DefaultPort);

    /// <summary>Refuses WriteProperty (default true). Reads, discovery and COV subscriptions stay allowed.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>APDU timeout (default 3 s).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Retries per request (default 1).</summary>
    public int Retries { get; set; } = 1;

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public BacnetIpClientOptions UseDatagramTransport(DatagramTransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }

    /// <summary>Allows WriteProperty.</summary>
    public BacnetIpClientOptions AllowWrites()
    {
        ReadOnly = false;
        return this;
    }
}

/// <summary>
/// BACnet/IP client as an adapter over the mature ela-compil BACnet library (MIT): Who-Is discovery, ReadProperty,
/// ReadPropertyMultiple, WriteProperty with priorities, and SubscribeCOV with notifications, exposed with IoTCom's
/// BACnet types and run over any IoTCom datagram transport (real UDP or the in-memory network). Writes are refused
/// until <see cref="BacnetIpClientOptions.AllowWrites"/>.
/// </summary>
public sealed class BacnetIpClient : EndpointBase, IClientEndpoint
{
    private readonly BacnetIpClientOptions _options;
    private readonly ConcurrentDictionary<uint, BacnetDeviceInfo> _devices = new();
    private readonly ConcurrentDictionary<uint, (uint Device, BacnetObjectId Object, Action<BacnetCovNotification>? Callback)> _subscriptions = new();
    private Lib.BacnetClient? _client;
    private int _nextProcessId = 100;

    private BacnetIpClient(BacnetIpClientOptions options) : base("bacnet", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a client.</summary>
    public static BacnetIpClient Create(Action<BacnetIpClientOptions>? configure = null)
    {
        var o = new BacnetIpClientOptions();
        configure?.Invoke(o);
        return new BacnetIpClient(o);
    }

    /// <summary>True when writes are refused.</summary>
    public bool ReadOnly => _options.ReadOnly;

    /// <summary>Devices seen so far (I-Am), by instance.</summary>
    public IReadOnlyDictionary<uint, BacnetDeviceInfo> Devices => _devices;

    /// <summary>Raised for every I-Am.</summary>
    public event Action<BacnetDeviceInfo>? DeviceFound;

    /// <summary>Raised for every COV notification (also those without a callback).</summary>
    public event Action<BacnetCovNotification>? CovNotification;

    /// <inheritdoc />
    public ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_client is not null) return ValueTask.CompletedTask;
        SetState(EndpointState.Connecting);
        var socket = (_options.TransportFactory ?? (() =>
        {
            var udp = new UdpDatagramTransport(new IPEndPoint(IPAddress.Any, BacnetDeviceOptions.DefaultPort), reuseAddress: true);
            udp.EnableBroadcast = true;
            return udp;
        }))();
        if (socket is InMemoryDatagramTransport m && (_options.BroadcastAddress.Address.GetAddressBytes()[0] & 0xF0) == 0xE0) m.JoinMulticastGroup(_options.BroadcastAddress.Address);
        var transport = new IoTComBacnetTransport(socket, _options.BroadcastAddress, (d, frame) => Tap(d, frame, () => Describe(frame)));
        _client = new Lib.BacnetClient(transport, (int)_options.Timeout.TotalMilliseconds, _options.Retries);
        _client.OnIam += OnIam;
        _client.OnCOVNotification += OnCov;
        _client.Start();
        SetState(EndpointState.Connected);
        return ValueTask.CompletedTask;
    }

    private static string Describe(byte[] frame)
    {
        try
        {
            return BacnetPacket.Decode(frame).Describe();
        }
        catch (ProtocolException ex)
        {
            return "invalid: " + ex.Message;
        }
    }

    private Lib.BacnetClient Client => _client ?? throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

    private void OnIam(Lib.BacnetClient sender, Lib.BacnetAddress adr, uint deviceId, uint maxApdu, Lib.BacnetSegmentations segmentation, ushort vendorId)
    {
        var info = new BacnetDeviceInfo(deviceId, IoTComBacnetTransport.Endpoint(adr), (int)maxApdu, (BacnetSegmentation)(uint)segmentation, vendorId);
        _devices[deviceId] = info;
        DeviceFound?.Invoke(info);
    }

    private void OnCov(Lib.BacnetClient sender, Lib.BacnetAddress adr, byte invokeId, uint processId, Lib.BacnetObjectId device, Lib.BacnetObjectId obj, uint remaining, bool needConfirm, ICollection<Lib.BacnetPropertyValue> values, Lib.BacnetMaxSegments maxSegments)
    {
        if (needConfirm) sender.SimpleAckResponse(adr, Lib.BacnetConfirmedServices.SERVICE_CONFIRMED_COV_NOTIFICATION, invokeId);
        var n = new BacnetCovNotification(processId, From(device), From(obj), remaining,
            [.. values.Select(v => new BacnetPropertyValue(new BacnetPropertyReference((BacnetPropertyId)v.property.propertyIdentifier, Index(v.property.propertyArrayIndex)), [.. v.value.Select(From)]))]);
        if (_subscriptions.TryGetValue(processId, out var s)) s.Callback?.Invoke(n);
        CovNotification?.Invoke(n);
    }

    // ---- conversions --------------------------------------------------------------------------------------------------

    private static uint? Index(uint i) => i == uint.MaxValue ? null : i;

    private static Lib.BacnetObjectId To(BacnetObjectId id) => new((Lib.BacnetObjectTypes)(int)id.Type, id.Instance);

    private static BacnetObjectId From(Lib.BacnetObjectId id) => new((BacnetObjectType)(ushort)id.Type, id.Instance);

    internal static BacnetValue From(Lib.BacnetValue v) => v.Value switch
    {
        null => BacnetValue.Null,
        float f => BacnetValue.Real(f),
        double d => new BacnetValue(BacnetApplicationTag.Double, d),
        bool b => BacnetValue.Boolean(b),
        string s => BacnetValue.Text(s),
        int i => BacnetValue.Signed(i),
        byte[] o => new BacnetValue(BacnetApplicationTag.OctetString, o),
        Lib.BacnetObjectId id => BacnetValue.Object(From(id)),
        Lib.BacnetBitString bits => BacnetValue.Bits(new BacnetBitString([.. Enumerable.Range(0, bits.bits_used).Select(i => bits.GetBit((byte)i))])),
        Lib.BacnetError e => throw new BacnetErrorException((BacnetErrorClass)(uint)e.error_class, (BacnetErrorCode)(uint)e.error_code),
        uint u when v.Tag == Lib.BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED => BacnetValue.Enumerated(u),
        uint u => BacnetValue.Unsigned(u),
        DateTime t when v.Tag == Lib.BacnetApplicationTags.BACNET_APPLICATION_TAG_DATE => new BacnetValue(BacnetApplicationTag.Date, BacnetDate.From(DateOnly.FromDateTime(t))),
        DateTime t => new BacnetValue(BacnetApplicationTag.Time, BacnetTime.From(TimeOnly.FromDateTime(t))),
        _ when v.Tag == Lib.BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED => BacnetValue.Enumerated(Convert.ToUInt32(v.Value, System.Globalization.CultureInfo.InvariantCulture)),
        _ => new BacnetValue((BacnetApplicationTag)(byte)v.Tag, v.Value),
    };

    private static Lib.BacnetValue To(BacnetValue v) => v.Tag switch
    {
        BacnetApplicationTag.Null => new Lib.BacnetValue(Lib.BacnetApplicationTags.BACNET_APPLICATION_TAG_NULL, null),
        BacnetApplicationTag.Real => new Lib.BacnetValue(Lib.BacnetApplicationTags.BACNET_APPLICATION_TAG_REAL, Convert.ToSingle(v.Value, System.Globalization.CultureInfo.InvariantCulture)),
        BacnetApplicationTag.UnsignedInteger => new Lib.BacnetValue(Lib.BacnetApplicationTags.BACNET_APPLICATION_TAG_UNSIGNED_INT, Convert.ToUInt32(v.Value, System.Globalization.CultureInfo.InvariantCulture)),
        BacnetApplicationTag.Enumerated => new Lib.BacnetValue(Lib.BacnetApplicationTags.BACNET_APPLICATION_TAG_ENUMERATED, Convert.ToUInt32(v.Value, System.Globalization.CultureInfo.InvariantCulture)),
        BacnetApplicationTag.SignedInteger => new Lib.BacnetValue(Lib.BacnetApplicationTags.BACNET_APPLICATION_TAG_SIGNED_INT, Convert.ToInt32(v.Value, System.Globalization.CultureInfo.InvariantCulture)),
        BacnetApplicationTag.Boolean => new Lib.BacnetValue(Lib.BacnetApplicationTags.BACNET_APPLICATION_TAG_BOOLEAN, v.Value is true),
        BacnetApplicationTag.CharacterString => new Lib.BacnetValue(Lib.BacnetApplicationTags.BACNET_APPLICATION_TAG_CHARACTER_STRING, (string)v.Value!),
        BacnetApplicationTag.Double => new Lib.BacnetValue(Lib.BacnetApplicationTags.BACNET_APPLICATION_TAG_DOUBLE, Convert.ToDouble(v.Value, System.Globalization.CultureInfo.InvariantCulture)),
        _ => throw new NotSupportedException($"Writing {v.Tag} values is not supported."),
    };

    private Lib.BacnetAddress AddressOf(uint device) => _devices.TryGetValue(device, out var d)
        ? IoTComBacnetTransport.Address(d.Address)
        : throw new InvalidOperationException($"Device {device} is unknown. Run DiscoverAsync first, or add it with AddDevice.");

    /// <summary>Registers a device without discovery (for networks where broadcasts do not arrive).</summary>
    public void AddDevice(uint instance, IPEndPoint address) => _devices[instance] = new BacnetDeviceInfo(instance, address, 1476, BacnetSegmentation.None, 0);

    private static async Task<T> Call<T>(Func<Task<T>> call, string what)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (IoTComException or OperationCanceledException or InvalidOperationException or NotSupportedException))
        {
            throw Translate(ex, what);
        }
    }

    private static Exception Translate(Exception ex, string what)
    {
        var text = ex.Message;
        if (text.Contains("timeout", StringComparison.OrdinalIgnoreCase) || ex is TimeoutException) return new IoTComTimeoutException($"{what}: no answer.", ex);
        return new DeviceException($"{what}: {text}");
    }

    // ---- operations ---------------------------------------------------------------------------------------------------

    /// <summary>Broadcasts Who-Is and collects I-Am answers for <paramref name="window"/> (default 2 s).</summary>
    public async Task<IReadOnlyList<BacnetDeviceInfo>> DiscoverAsync(TimeSpan? window = null, uint? low = null, uint? high = null, CancellationToken ct = default)
    {
        var seen = new ConcurrentDictionary<uint, BacnetDeviceInfo>();
        void Found(BacnetDeviceInfo d) => seen[d.Instance] = d;
        DeviceFound += Found;
        try
        {
            Client.WhoIs(low is null ? -1 : (int)low, high is null ? -1 : (int)high);
            await Task.Delay(window ?? TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        finally
        {
            DeviceFound -= Found;
        }

        return [.. seen.Values.OrderBy(d => d.Instance)];
    }

    /// <summary>Reads one property.</summary>
    public async Task<IReadOnlyList<BacnetValue>> ReadPropertyAsync(uint device, BacnetObjectId obj, BacnetPropertyId property, uint? arrayIndex = null, CancellationToken ct = default)
    {
        var values = await Call(() => Client.ReadPropertyAsync(AddressOf(device), To(obj), (Lib.BacnetPropertyIds)(uint)property, 0, arrayIndex ?? uint.MaxValue, ct), $"ReadProperty {obj.ShortName}.{property}").ConfigureAwait(false);
        return [.. values.Select(From)];
    }

    /// <summary>Reads the present value.</summary>
    public async Task<BacnetValue> ReadPresentValueAsync(uint device, BacnetObjectId obj, CancellationToken ct = default) =>
        (await ReadPropertyAsync(device, obj, BacnetPropertyId.PresentValue, ct: ct).ConfigureAwait(false))[0];

    /// <summary>Reads the object list of a device.</summary>
    public async Task<IReadOnlyList<BacnetObjectId>> ReadObjectListAsync(uint device, CancellationToken ct = default) =>
        [.. (await ReadPropertyAsync(device, new BacnetObjectId(BacnetObjectType.Device, device), BacnetPropertyId.ObjectList, ct: ct).ConfigureAwait(false)).Select(v => (BacnetObjectId)v.Value!)];

    /// <summary>Reads several properties of one object with ReadPropertyMultiple; failed properties are reported as errors.</summary>
    public async Task<IReadOnlyList<BacnetPropertyResult>> ReadPropertiesAsync(uint device, BacnetObjectId obj, IEnumerable<BacnetPropertyId> properties, CancellationToken ct = default)
    {
        var refs = properties.Select(p => new Lib.BacnetPropertyReference((uint)p, uint.MaxValue)).ToList();
        var results = await Call(() => Client.ReadPropertyMultipleAsync(AddressOf(device), To(obj), refs, 0, ct), $"ReadPropertyMultiple {obj.ShortName}").ConfigureAwait(false);
        var list = new List<BacnetPropertyResult>();
        foreach (var r in results)
        {
            foreach (var v in r.values)
            {
                var reference = new BacnetPropertyReference((BacnetPropertyId)v.property.propertyIdentifier, Index(v.property.propertyArrayIndex));
                if (v.value.Count == 1 && v.value[0].Value is Lib.BacnetError e)
                    list.Add(new BacnetPropertyResult(reference, null, (BacnetErrorClass)(uint)e.error_class, (BacnetErrorCode)(uint)e.error_code));
                else
                    list.Add(new BacnetPropertyResult(reference, [.. v.value.Select(From)]));
            }
        }

        return list;
    }

    /// <summary>Writes a property; <paramref name="priority"/> 1–16 commands a commandable present value (null relinquishes when the value is <see cref="BacnetValue.Null"/>).</summary>
    /// <exception cref="ReadOnlyModeException">The client is read-only.</exception>
    public Task WritePropertyAsync(uint device, BacnetObjectId obj, BacnetPropertyId property, BacnetValue value, byte? priority = null, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException($"Writing {obj.ShortName}.{property} changes the building's equipment; this client is read-only until AllowWrites().");
        return Call(async () =>
        {
            await Client.WritePropertyAsync(AddressOf(device), To(obj), (Lib.BacnetPropertyIds)(uint)property, [To(value)], 0, priority, uint.MaxValue, ct).ConfigureAwait(false);
            return true;
        }, $"WriteProperty {obj.ShortName}.{property}");
    }

    /// <summary>Subscribes to COV notifications of an object; dispose the result to cancel.</summary>
    public async Task<IAsyncDisposable> SubscribeCovAsync(uint device, BacnetObjectId obj, Action<BacnetCovNotification>? callback = null, bool confirmed = false, uint lifetime = 300, CancellationToken ct = default)
    {
        var pid = (uint)Interlocked.Increment(ref _nextProcessId);
        _subscriptions[pid] = (device, obj, callback);
        try
        {
            await Call(async () =>
            {
                await Client.SubscribeCOVAsync(AddressOf(device), To(obj), pid, false, confirmed, lifetime, 0, ct).ConfigureAwait(false);
                return true;
            }, $"SubscribeCOV {obj.ShortName}").ConfigureAwait(false);
        }
        catch
        {
            _subscriptions.TryRemove(pid, out _);
            throw;
        }

        return new Subscription(this, pid);
    }

    private sealed class Subscription(BacnetIpClient owner, uint pid) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (!owner._subscriptions.TryRemove(pid, out var s) || owner._client is null) return;
            try
            {
                await owner._client.SubscribeCOVAsync(owner.AddressOf(s.Device), To(s.Object), pid, true, false, 0).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // the device forgets the subscription at its lifetime anyway
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is null) return ValueTask.CompletedTask;
        client.OnIam -= OnIam;
        client.OnCOVNotification -= OnCov;
        client.Dispose();
        SetState(EndpointState.Disconnected);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await DisconnectAsync().ConfigureAwait(false);
}
