using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;

namespace IoTCom.Net.Transport.Ble;

/// <summary>A characteristic hosted by a <see cref="VirtualBlePeripheral"/>.</summary>
public sealed class VirtualCharacteristic
{
    internal VirtualCharacteristic(VirtualBlePeripheral owner, Guid uuid, GattProperties properties, byte[] value)
    {
        Owner = owner;
        Uuid = uuid;
        Properties = properties;
        Value = value;
    }

    internal VirtualBlePeripheral Owner { get; }

    /// <summary>UUID.</summary>
    public Guid Uuid { get; }

    /// <summary>Properties.</summary>
    public GattProperties Properties { get; }

    /// <summary>Current value.</summary>
    public byte[] Value { get; private set; }

    /// <summary>Called when a central writes; return false to reject the write (ATT error).</summary>
    public Func<byte[], bool>? OnWrite { get; set; }

    /// <summary>Changes the value and notifies subscribed centrals.</summary>
    public void Update(byte[] value)
    {
        Value = value;
        if ((Properties & (GattProperties.Notify | GattProperties.Indicate)) != 0) Owner.Network.Notify(Owner, this);
    }

    internal bool Write(byte[] value)
    {
        if (OnWrite is { } handler && !handler(value)) return false;
        Value = value;
        return true;
    }
}

/// <summary>A simulated peripheral: advertisement plus GATT services.</summary>
public sealed class VirtualBlePeripheral
{
    private readonly List<(Guid Service, VirtualCharacteristic Characteristic)> _characteristics = [];

    internal VirtualBlePeripheral(VirtualBleNetwork network, string id, string name, int rssi)
    {
        Network = network;
        Id = id;
        Name = name;
        Rssi = rssi;
    }

    internal VirtualBleNetwork Network { get; }

    /// <summary>Id (a Bluetooth address).</summary>
    public string Id { get; }

    /// <summary>Advertised name.</summary>
    public string Name { get; }

    /// <summary>Signal strength seen by centrals (dBm); change it to simulate movement.</summary>
    public int Rssi { get; set; }

    /// <summary>Advertised TX power.</summary>
    public int? TxPower { get; set; }

    /// <summary>Advertised service UUIDs.</summary>
    public List<Guid> AdvertisedServices { get; } = [];

    /// <summary>Manufacturer data.</summary>
    public Dictionary<ushort, byte[]> ManufacturerData { get; } = [];

    /// <summary>Service data.</summary>
    public Dictionary<Guid, byte[]> ServiceData { get; } = [];

    /// <summary>Accepts connections (false for beacons).</summary>
    public bool Connectable { get; set; } = true;

    /// <summary>Adds a characteristic to <paramref name="service"/>.</summary>
    public VirtualCharacteristic Characteristic(string service, string uuid, GattProperties properties, byte[]? value = null)
    {
        var c = new VirtualCharacteristic(this, BleUuid.Parse(uuid), properties, value ?? []);
        _characteristics.Add((BleUuid.Parse(service), c));
        return c;
    }

    internal VirtualCharacteristic? Find(Guid uuid) => _characteristics.FirstOrDefault(c => c.Characteristic.Uuid == uuid).Characteristic;

    internal IReadOnlyList<GattService> Services() =>
        [.. _characteristics.GroupBy(c => c.Service).Select(g => new GattService(g.Key, true, [.. g.Select(c => new GattCharacteristic(c.Characteristic.Uuid, c.Characteristic.Properties))]))];

    /// <summary>The advertisement centrals receive.</summary>
    public BleAdvertisement Advertisement() => new()
    {
        Id = Id, Address = Id, Name = Name, Rssi = Rssi, TxPower = TxPower, Services = [.. AdvertisedServices],
        ManufacturerData = new Dictionary<ushort, byte[]>(ManufacturerData), ServiceData = new Dictionary<Guid, byte[]>(ServiceData),
    };
}

/// <summary>
/// A simulated radio environment. Add peripherals (or the ready-made <see cref="AddHeartRateStrap"/>,
/// <see cref="AddEnvironmentSensor"/>, <see cref="AddBeacon"/>, <see cref="AddSmartPlug"/>) and open adapters with
/// <see cref="CreateAdapter"/>; tests, notebooks and the Gallery run BLE without a radio this way.
/// </summary>
public sealed class VirtualBleNetwork
{
    private readonly ConcurrentDictionary<string, VirtualBlePeripheral> _peripherals = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<VirtualBleAdapter> _adapters = [];
    private readonly Lock _gate = new();

    /// <summary>Peripherals in range.</summary>
    public IReadOnlyCollection<VirtualBlePeripheral> Peripherals => [.. _peripherals.Values];

    /// <summary>Adds an empty peripheral.</summary>
    public VirtualBlePeripheral Add(string id, string name, int rssi = -60)
    {
        var p = new VirtualBlePeripheral(this, id, name, rssi);
        _peripherals[id] = p;
        return p;
    }

    /// <summary>Removes a peripheral (it goes out of range; connected centrals see a disconnection).</summary>
    public void Remove(string id)
    {
        if (!_peripherals.TryRemove(id, out _)) return;
        foreach (var a in Adapters()) a.Lost(id);
    }

    /// <summary>Opens a central on this network.</summary>
    public VirtualBleAdapter CreateAdapter(string description = "Virtual BLE radio")
    {
        var a = new VirtualBleAdapter(this, description);
        lock (_gate) _adapters.Add(a);
        return a;
    }

    internal VirtualBlePeripheral? Find(string id) => _peripherals.GetValueOrDefault(id);

    private VirtualBleAdapter[] Adapters()
    {
        lock (_gate) return [.. _adapters];
    }

    internal void Detach(VirtualBleAdapter adapter)
    {
        lock (_gate) _adapters.Remove(adapter);
    }

    internal void Notify(VirtualBlePeripheral p, VirtualCharacteristic c)
    {
        foreach (var a in Adapters()) a.Deliver(p, c);
    }

    /// <summary>A chest strap: Heart Rate service (0x2A37 notify, body sensor location chest), battery, device information.</summary>
    public VirtualBlePeripheral AddHeartRateStrap(string id = "C4:7C:8D:6A:21:0F", string name = "HRM-Pro 2107", int rssi = -58)
    {
        var p = Add(id, name, rssi);
        p.AdvertisedServices.Add(BleUuid.FromShort(0x180D));
        p.TxPower = -4;
        p.Characteristic("180d", "2a37", GattProperties.Notify, GattValue.EncodeHeartRate(72, contact: true));
        p.Characteristic("180d", "2a38", GattProperties.Read, [1]);
        p.Characteristic("180f", "2a19", GattProperties.Read | GattProperties.Notify, [87]);
        p.Characteristic("180a", "2a29", GattProperties.Read, Encoding.UTF8.GetBytes("Gravicode Sport"));
        p.Characteristic("180a", "2a24", GattProperties.Read, Encoding.UTF8.GetBytes("HRM-Pro"));
        return p;
    }

    /// <summary>A greenhouse sensor: Environmental Sensing (temperature, humidity, pressure; read + notify) and battery.</summary>
    public VirtualBlePeripheral AddEnvironmentSensor(string id = "E8:4F:25:10:7A:33", string name = "Greenhouse ESS 3", int rssi = -71)
    {
        var p = Add(id, name, rssi);
        p.AdvertisedServices.Add(BleUuid.FromShort(0x181A));
        p.Characteristic("181a", "2a6e", GattProperties.Read | GattProperties.Notify, Int16(2795));
        p.Characteristic("181a", "2a6f", GattProperties.Read | GattProperties.Notify, UInt16(7120));
        p.Characteristic("181a", "2a6d", GattProperties.Read | GattProperties.Notify, UInt32(1_009_200));
        p.Characteristic("180f", "2a19", GattProperties.Read, [64]);
        return p;
    }

    /// <summary>A non-connectable iBeacon (warehouse dock door).</summary>
    public VirtualBlePeripheral AddBeacon(string id = "F2:11:0B:9C:44:01", string name = "Dock-07", int rssi = -79)
    {
        var p = Add(id, name, rssi);
        p.Connectable = false;
        p.ManufacturerData[0x004C] = new IBeacon(Guid.Parse("e2c56db5-dffb-48d2-b060-d0f5a71096e0"), 7, 1, -59).Encode();
        return p;
    }

    /// <summary>A smart plug with a vendor service: relay (read/write) and power in watts (notify).</summary>
    public VirtualBlePeripheral AddSmartPlug(string id = "D0:8E:3A:55:10:C2", string name = "Plug Pompa Air", int rssi = -64)
    {
        var p = Add(id, name, rssi);
        p.AdvertisedServices.Add(SmartPlugService);
        p.Characteristic(SmartPlugService.ToString(), SmartPlugRelay.ToString(), GattProperties.Read | GattProperties.Write, [0]);
        p.Characteristic(SmartPlugService.ToString(), SmartPlugPower.ToString(), GattProperties.Read | GattProperties.Notify, UInt16(0));
        return p;
    }

    /// <summary>Vendor service of the simulated smart plug.</summary>
    public static Guid SmartPlugService { get; } = Guid.Parse("6e400001-b5a3-f393-e0a9-e50e24dcca9e");

    /// <summary>Relay characteristic (1 byte: 0 off, 1 on).</summary>
    public static Guid SmartPlugRelay { get; } = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e");

    /// <summary>Power characteristic (uint16 watts).</summary>
    public static Guid SmartPlugPower { get; } = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e");

    internal static byte[] Int16(short v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(b, v);
        return b;
    }

    internal static byte[] UInt16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        return b;
    }

    internal static byte[] UInt32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }
}

/// <summary>A central on a <see cref="VirtualBleNetwork"/>: scans every 200 ms while scanning.</summary>
public sealed class VirtualBleAdapter : IBleAdapter
{
    private readonly VirtualBleNetwork _network;
    private readonly ConcurrentDictionary<string, byte> _connected = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string, Guid), byte> _subscribed = new();
    private CancellationTokenSource? _scan;

    internal VirtualBleAdapter(VirtualBleNetwork network, string description)
    {
        _network = network;
        Description = description;
    }

    /// <inheritdoc />
    public string Description { get; }

    /// <inheritdoc />
    public event EventHandler<BleEvent>? Event;

    /// <inheritdoc />
    public Task StartScanAsync(IReadOnlyList<Guid> services, CancellationToken ct)
    {
        _scan?.Cancel();
        _scan = new CancellationTokenSource();
        var token = _scan.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                foreach (var p in _network.Peripherals)
                {
                    var ad = p.Advertisement();
                    if (services.Count == 0 || ad.Services.Any(services.Contains)) Event?.Invoke(this, new BleAdvertisementEvent(ad));
                }

                try
                {
                    await Task.Delay(200, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopScanAsync(CancellationToken ct)
    {
        _scan?.Cancel();
        return Task.CompletedTask;
    }

    private VirtualBlePeripheral Peripheral(string id) => _network.Find(id) ?? throw new DeviceException($"device {id} has not been seen in a scan");

    private VirtualCharacteristic Char(string id, Guid uuid)
    {
        if (!_connected.ContainsKey(id)) throw new InvalidOperationException($"{id} is not connected.");
        return Peripheral(id).Find(uuid) ?? throw new DeviceException($"characteristic {uuid} not found");
    }

    /// <inheritdoc />
    public async Task ConnectAsync(string id, TimeSpan timeout, CancellationToken ct)
    {
        var p = Peripheral(id);
        if (!p.Connectable) throw new DeviceException($"{p.Name} does not accept connections (it only advertises).");
        await Task.Delay(30, ct).ConfigureAwait(false);
        if (_connected.TryAdd(id, 0)) Event?.Invoke(this, new BleConnectionEvent(id, true));
    }

    /// <inheritdoc />
    public Task DisconnectAsync(string id, CancellationToken ct)
    {
        if (_connected.TryRemove(id, out _)) Event?.Invoke(this, new BleConnectionEvent(id, false));
        return Task.CompletedTask;
    }

    internal void Lost(string id)
    {
        foreach (var key in _subscribed.Keys.Where(k => string.Equals(k.Item1, id, StringComparison.OrdinalIgnoreCase)).ToList()) _subscribed.TryRemove(key, out _);
        if (_connected.TryRemove(id, out _)) Event?.Invoke(this, new BleConnectionEvent(id, false));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<GattService>> GetServicesAsync(string id, CancellationToken ct)
    {
        if (!_connected.ContainsKey(id)) throw new InvalidOperationException($"{id} is not connected.");
        return Task.FromResult(Peripheral(id).Services());
    }

    /// <inheritdoc />
    public Task<byte[]> ReadAsync(string id, Guid characteristic, TimeSpan timeout, CancellationToken ct)
    {
        var c = Char(id, characteristic);
        if ((c.Properties & GattProperties.Read) == 0) throw new DeviceException($"{BleUuid.Name(characteristic)} is not readable");
        return Task.FromResult(c.Value.ToArray());
    }

    /// <inheritdoc />
    public Task WriteAsync(string id, Guid characteristic, ReadOnlyMemory<byte> value, bool withResponse, TimeSpan timeout, CancellationToken ct)
    {
        var c = Char(id, characteristic);
        if ((c.Properties & (GattProperties.Write | GattProperties.WriteWithoutResponse)) == 0) throw new DeviceException($"{BleUuid.Name(characteristic)} is not writable");
        if (!c.Write(value.ToArray()) && withResponse) throw new DeviceException($"{BleUuid.Name(characteristic)} rejected the value");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetNotifyAsync(string id, Guid characteristic, bool enable, CancellationToken ct)
    {
        var c = Char(id, characteristic);
        if ((c.Properties & (GattProperties.Notify | GattProperties.Indicate)) == 0) throw new DeviceException($"{BleUuid.Name(characteristic)} does not notify");
        if (enable) _subscribed[(id, characteristic)] = 0;
        else _subscribed.TryRemove((id, characteristic), out _);
        return Task.CompletedTask;
    }

    internal void Deliver(VirtualBlePeripheral p, VirtualCharacteristic c)
    {
        if (_subscribed.ContainsKey((p.Id, c.Uuid)) && _connected.ContainsKey(p.Id)) Event?.Invoke(this, new BleNotificationEvent(p.Id, c.Uuid, c.Value.ToArray()));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _scan?.Cancel();
        _scan?.Dispose();
        _network.Detach(this);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Drives the simulated peripherals of a network: heart rate, environment and plug power change every <see cref="Step"/>.</summary>
public sealed class VirtualBleSimulator(VirtualBleNetwork network, int seed = 11)
{
    private readonly Random _random = new(seed);
    private double _heart = 72;
    private double _temperature = 27.95;
    private double _humidity = 71.2;
    private int _tick;

    /// <summary>Advances one second.</summary>
    public void Step()
    {
        _tick++;
        _heart = Math.Clamp(_heart + ((_random.NextDouble() - 0.45) * 6), 58, 168);
        _temperature = Math.Clamp(_temperature + ((_random.NextDouble() - 0.5) * 0.2), 18, 40);
        _humidity = Math.Clamp(_humidity + ((_random.NextDouble() - 0.5) * 0.8), 30, 95);
        foreach (var p in network.Peripherals)
        {
            var hr = p.Find(BleUuid.FromShort(0x2A37));
            if (hr is not null)
            {
                var bpm = (int)Math.Round(_heart);
                hr.Update(GattValue.EncodeHeartRate(bpm, contact: true, [Math.Round(60.0 / bpm, 3)]));
                if (_tick % 30 == 0 && p.Find(BleUuid.FromShort(0x2A19)) is { } battery && battery.Value[0] > 5) battery.Update([(byte)(battery.Value[0] - 1)]);
            }

            p.Find(BleUuid.FromShort(0x2A6E))?.Update(VirtualBleNetwork.Int16((short)Math.Round(_temperature * 100)));
            p.Find(BleUuid.FromShort(0x2A6F))?.Update(VirtualBleNetwork.UInt16((ushort)Math.Round(_humidity * 100)));
            if (p.Find(VirtualBleNetwork.SmartPlugRelay) is { } relay && p.Find(VirtualBleNetwork.SmartPlugPower) is { } power)
                power.Update(VirtualBleNetwork.UInt16((ushort)(relay.Value is [1, ..] ? 370 + _random.Next(-8, 9) : 0)));
            p.Rssi = Math.Clamp(p.Rssi + _random.Next(-2, 3), -95, -35);
        }
    }
}
