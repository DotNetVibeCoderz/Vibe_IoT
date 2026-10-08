namespace IoTCom.Net.Transport.Ble;

/// <summary>Characteristic properties (GATT).</summary>
[Flags]
public enum GattProperties
{
    /// <summary>None.</summary>
    None = 0,
    /// <summary>Broadcast.</summary>
    Broadcast = 1,
    /// <summary>Read.</summary>
    Read = 2,
    /// <summary>Write without response.</summary>
    WriteWithoutResponse = 4,
    /// <summary>Write.</summary>
    Write = 8,
    /// <summary>Notify.</summary>
    Notify = 16,
    /// <summary>Indicate.</summary>
    Indicate = 32,
}

/// <summary>A characteristic of a connected peripheral.</summary>
/// <param name="Uuid">UUID.</param>
/// <param name="Properties">Properties.</param>
public sealed record GattCharacteristic(Guid Uuid, GattProperties Properties)
{
    /// <summary>Readable name.</summary>
    public string Name => BleUuid.Name(Uuid);
}

/// <summary>A service of a connected peripheral.</summary>
/// <param name="Uuid">UUID.</param>
/// <param name="Primary">Primary service.</param>
/// <param name="Characteristics">Characteristics.</param>
public sealed record GattService(Guid Uuid, bool Primary, IReadOnlyList<GattCharacteristic> Characteristics)
{
    /// <summary>Readable name.</summary>
    public string Name => BleUuid.Name(Uuid);
}

/// <summary>Something the adapter reports asynchronously.</summary>
public abstract record BleEvent;

/// <summary>An advertisement was received.</summary>
/// <param name="Advertisement">The advertisement.</param>
public sealed record BleAdvertisementEvent(BleAdvertisement Advertisement) : BleEvent;

/// <summary>A notification or indication arrived.</summary>
/// <param name="DeviceId">Peripheral id.</param>
/// <param name="Characteristic">Characteristic UUID.</param>
/// <param name="Value">Value.</param>
public sealed record BleNotificationEvent(string DeviceId, Guid Characteristic, byte[] Value) : BleEvent;

/// <summary>A peripheral connected or disconnected.</summary>
/// <param name="DeviceId">Peripheral id.</param>
/// <param name="Connected">True when connected.</param>
public sealed record BleConnectionEvent(string DeviceId, bool Connected) : BleEvent;

/// <summary>
/// A BLE central radio: the native adapter (btleplug through the <c>iotcom_ble</c> library) or a virtual one for tests
/// and demos. Implementations raise <see cref="Event"/> from a background thread.
/// </summary>
public interface IBleAdapter : IAsyncDisposable
{
    /// <summary>Description of the radio.</summary>
    string Description { get; }

    /// <summary>Advertisements, notifications and connection changes.</summary>
    event EventHandler<BleEvent>? Event;

    /// <summary>Starts scanning (optionally only for devices advertising one of <paramref name="services"/>).</summary>
    Task StartScanAsync(IReadOnlyList<Guid> services, CancellationToken ct);

    /// <summary>Stops scanning.</summary>
    Task StopScanAsync(CancellationToken ct);

    /// <summary>Connects and discovers services.</summary>
    Task ConnectAsync(string id, TimeSpan timeout, CancellationToken ct);

    /// <summary>Disconnects.</summary>
    Task DisconnectAsync(string id, CancellationToken ct);

    /// <summary>Services of a connected peripheral.</summary>
    Task<IReadOnlyList<GattService>> GetServicesAsync(string id, CancellationToken ct);

    /// <summary>Reads a characteristic.</summary>
    Task<byte[]> ReadAsync(string id, Guid characteristic, TimeSpan timeout, CancellationToken ct);

    /// <summary>Writes a characteristic.</summary>
    Task WriteAsync(string id, Guid characteristic, ReadOnlyMemory<byte> value, bool withResponse, TimeSpan timeout, CancellationToken ct);

    /// <summary>Enables or disables notifications.</summary>
    Task SetNotifyAsync(string id, Guid characteristic, bool enable, CancellationToken ct);
}
