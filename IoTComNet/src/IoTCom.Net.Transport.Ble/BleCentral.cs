using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Transport.Ble;

/// <summary>BLE central options.</summary>
public sealed class BleCentralOptions
{
    /// <summary>Creates the radio (default: the native adapter).</summary>
    public Func<IBleAdapter> AdapterFactory { get; set; } = NativeBleAdapter.Open;

    /// <summary>Refuse characteristic writes with <see cref="ReadOnlyModeException"/>.</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Timeout of connects, reads and writes.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Uses the platform radio through the <c>iotcom_ble</c> native library.</summary>
    public BleCentralOptions UseNative()
    {
        AdapterFactory = NativeBleAdapter.Open;
        return this;
    }

    /// <summary>Uses a simulated radio.</summary>
    public BleCentralOptions UseVirtual(VirtualBleNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);
        AdapterFactory = () => network.CreateAdapter();
        return this;
    }
}

/// <summary>
/// A Bluetooth Low Energy central: scan for advertisements, connect to peripherals, read, write (blocked in read-only
/// mode) and subscribe to characteristics. <see cref="ConnectAsync"/> opens the radio; peripherals are reached with
/// <see cref="OpenAsync"/>. GATT operations are reported to the traffic tap.
/// </summary>
/// <example>
/// <code>
/// await using var ble = BleCentral.Create(o => o.UseNative());
/// await ble.ConnectAsync();
/// var strap = (await ble.ScanAsync(TimeSpan.FromSeconds(5), [BleUuid.FromShort(0x180D)])).First();
/// await using var hr = await ble.OpenAsync(strap.Id);
/// await foreach (var v in hr.SubscribeAsync(BleUuid.FromShort(0x2A37)))
///     Console.WriteLine(GattValue.ParseHeartRate(v).BeatsPerMinute);
/// </code>
/// </example>
public sealed class BleCentral : EndpointBase, IClientEndpoint
{
    private readonly BleCentralOptions _options;
    private readonly ConcurrentDictionary<string, BleAdvertisement> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Channel<BleEvent>, byte> _listeners = new();
    private IBleAdapter? _adapter;

    private BleCentral(BleCentralOptions options) : base("ble", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a central.</summary>
    public static BleCentral Create(Action<BleCentralOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new BleCentralOptions();
        configure(o);
        return new BleCentral(o);
    }

    /// <summary>The radio.</summary>
    public IBleAdapter? Adapter => _adapter;

    /// <summary>Devices seen so far, with their latest advertisement.</summary>
    public IReadOnlyCollection<BleAdvertisement> Seen => [.. _seen.Values];

    internal BleCentralOptions Options => _options;

    /// <summary>Opens the radio.</summary>
    public ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_adapter is not null) return ValueTask.CompletedTask;
        SetState(EndpointState.Connecting);
        try
        {
            _adapter = _options.AdapterFactory();
        }
        catch (Exception ex) when (ex is TransportException or PlatformNotSupportedException)
        {
            SetState(EndpointState.Disconnected, ex);
            throw;
        }

        _adapter.Event += OnEvent;
        SetState(EndpointState.Connected);
        Logger.LogInformation("BLE radio {Radio}", _adapter.Description);
        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the radio.</summary>
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        if (_adapter is null) return;
        _adapter.Event -= OnEvent;
        await _adapter.DisposeAsync().ConfigureAwait(false);
        _adapter = null;
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await DisconnectAsync().ConfigureAwait(false);

    internal IBleAdapter Radio => _adapter ?? throw new InvalidOperationException("Call ConnectAsync first to open the radio.");

    private void OnEvent(object? sender, BleEvent e)
    {
        if (e is BleAdvertisementEvent a) _seen[a.Advertisement.Id] = a.Advertisement;
        foreach (var l in _listeners.Keys) l.Writer.TryWrite(e);
    }

    internal async IAsyncEnumerable<BleEvent> ListenAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<BleEvent>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        _listeners[channel] = 0;
        try
        {
            await foreach (var e in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return e;
        }
        finally
        {
            _listeners.TryRemove(channel, out _);
        }
    }

    /// <summary>Scans and yields every advertisement as it arrives (until cancelled).</summary>
    public async IAsyncEnumerable<BleAdvertisement> WatchAsync(IReadOnlyList<Guid>? services = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var radio = Radio;
        await using var e = ListenAsync(ct).GetAsyncEnumerator(ct);
        var first = e.MoveNextAsync();
        await radio.StartScanAsync(services ?? [], ct).ConfigureAwait(false);
        try
        {
            for (var has = await first.ConfigureAwait(false); has; has = await e.MoveNextAsync().ConfigureAwait(false))
                if (e.Current is BleAdvertisementEvent a) yield return a.Advertisement;
        }
        finally
        {
            await radio.StopScanAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Scans for <paramref name="duration"/> and returns each device once (strongest signal first).</summary>
    public async Task<IReadOnlyList<BleAdvertisement>> ScanAsync(TimeSpan duration, IReadOnlyList<Guid>? services = null, CancellationToken ct = default)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(duration);
        var found = new Dictionary<string, BleAdvertisement>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await foreach (var ad in WatchAsync(services, window.Token).ConfigureAwait(false)) found[ad.Id] = ad;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        return [.. found.Values.OrderByDescending(a => a.Rssi ?? int.MinValue)];
    }

    /// <summary>Connects to a peripheral seen in a scan and discovers its services.</summary>
    public async Task<BlePeripheral> OpenAsync(string id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        await Radio.ConnectAsync(id, _options.Timeout, ct).ConfigureAwait(false);
        var services = await Radio.GetServicesAsync(id, ct).ConfigureAwait(false);
        Tap(FrameDirection.Outbound, System.Text.Encoding.UTF8.GetBytes($"connect {id}"), () => $"connect {id}: {services.Count} services");
        return new BlePeripheral(this, id, _seen.GetValueOrDefault(id)?.Name, services);
    }

    internal void TapValue(FrameDirection direction, string what, Guid characteristic, ReadOnlySpan<byte> value)
    {
        var copy = value.ToArray();
        Tap(direction, copy, () => $"{what} {BleUuid.Short(characteristic)} {GattValue.Describe(characteristic, copy)}");
    }
}

/// <summary>A connected peripheral.</summary>
public sealed class BlePeripheral : IAsyncDisposable
{
    private readonly BleCentral _central;

    internal BlePeripheral(BleCentral central, string id, string? name, IReadOnlyList<GattService> services)
    {
        _central = central;
        Id = id;
        Name = name;
        Services = services;
    }

    /// <summary>Id.</summary>
    public string Id { get; }

    /// <summary>Advertised name.</summary>
    public string? Name { get; }

    /// <summary>Services discovered on connect.</summary>
    public IReadOnlyList<GattService> Services { get; }

    /// <summary>All characteristics.</summary>
    public IEnumerable<GattCharacteristic> Characteristics => Services.SelectMany(s => s.Characteristics);

    /// <summary>Reads a characteristic.</summary>
    public async Task<byte[]> ReadAsync(Guid characteristic, CancellationToken ct = default)
    {
        var v = await _central.Radio.ReadAsync(Id, characteristic, _central.Options.Timeout, ct).ConfigureAwait(false);
        _central.TapValue(FrameDirection.Inbound, "read", characteristic, v);
        return v;
    }

    /// <summary>Reads a characteristic by short or long UUID text ("2a19").</summary>
    public Task<byte[]> ReadAsync(string characteristic, CancellationToken ct = default) => ReadAsync(BleUuid.Parse(characteristic), ct);

    /// <summary>Writes a characteristic; throws <see cref="ReadOnlyModeException"/> in read-only mode.</summary>
    public async Task WriteAsync(Guid characteristic, ReadOnlyMemory<byte> value, bool withResponse = true, CancellationToken ct = default)
    {
        if (_central.Options.ReadOnly) throw new ReadOnlyModeException();
        _central.TapValue(FrameDirection.Outbound, "write", characteristic, value.Span);
        await _central.Radio.WriteAsync(Id, characteristic, value, withResponse, _central.Options.Timeout, ct).ConfigureAwait(false);
    }

    /// <summary>Enables notifications and yields each value until cancelled (then disables them).</summary>
    public async IAsyncEnumerable<byte[]> SubscribeAsync(Guid characteristic, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var e = _central.ListenAsync(ct).GetAsyncEnumerator(ct);
        var first = e.MoveNextAsync();
        await _central.Radio.SetNotifyAsync(Id, characteristic, true, ct).ConfigureAwait(false);
        try
        {
            for (var has = await first.ConfigureAwait(false); has; has = await e.MoveNextAsync().ConfigureAwait(false))
            {
                switch (e.Current)
                {
                    case BleNotificationEvent n when n.Characteristic == characteristic && string.Equals(n.DeviceId, Id, StringComparison.OrdinalIgnoreCase):
                        _central.TapValue(FrameDirection.Inbound, "notify", characteristic, n.Value);
                        yield return n.Value;
                        break;
                    case BleConnectionEvent { Connected: false } c when string.Equals(c.DeviceId, Id, StringComparison.OrdinalIgnoreCase):
                        throw new TransportException($"{Name ?? Id} disconnected.");
                }
            }
        }
        finally
        {
            try
            {
                await _central.Radio.SetNotifyAsync(Id, characteristic, false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IoTComException or InvalidOperationException)
            {
            }
        }
    }

    /// <summary>Disconnects.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _central.Radio.DisconnectAsync(Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IoTComException or InvalidOperationException)
        {
        }
    }
}
