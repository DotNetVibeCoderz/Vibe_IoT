using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using IoTCom.Net.Native;

namespace IoTCom.Net.Transport.Ble;

/// <summary>Source-generated P/Invoke bindings for <c>iotcom_ble</c> (AOT and trimming friendly).</summary>
internal static unsafe partial class BleNativeMethods
{
    public const string Library = "iotcom_ble";

    /// <summary>ABI version this binding was written for (iotcom-ffi-support ABI_VERSION).</summary>
    public const uint ExpectedAbiVersion = 1;

    public const int ErrBufferTooSmall = -4;
    public const int ErrUnavailable = -20;
    public const int ErrUnknownDevice = -21;
    public const int ErrUnknownCharacteristic = -22;
    public const int ErrTimeout = -23;

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "Registers the native resolver before the first P/Invoke.")]
    internal static void Init() => NativeLibraryLoader.Register(typeof(BleNativeMethods).Assembly);

    [LibraryImport(Library, EntryPoint = "iotcom_abi_version")]
    public static partial uint AbiVersion();

    [LibraryImport(Library, EntryPoint = "iotcom_last_error")]
    public static partial int LastError(byte* buf, nuint len);

    [LibraryImport(Library, EntryPoint = "iotcom_ble_new")]
    public static partial int New(out nint handle);

    [LibraryImport(Library, EntryPoint = "iotcom_ble_free")]
    public static partial void Free(nint handle);

    [LibraryImport(Library, EntryPoint = "iotcom_ble_adapter_info")]
    public static partial int AdapterInfo(BleHandle handle, byte* buf, nuint len, out nuint written);

    [LibraryImport(Library, EntryPoint = "iotcom_ble_start_scan")]
    public static partial int StartScan(BleHandle handle, byte* services, nuint len);

    [LibraryImport(Library, EntryPoint = "iotcom_ble_stop_scan")]
    public static partial int StopScan(BleHandle handle);

    [LibraryImport(Library, EntryPoint = "iotcom_ble_poll_event")]
    public static partial int PollEvent(BleHandle handle, byte* buf, nuint len, out nuint written);

    [LibraryImport(Library, EntryPoint = "iotcom_ble_connect")]
    public static partial int Connect(BleHandle handle, byte* id, nuint idLen, uint timeoutMs);

    [LibraryImport(Library, EntryPoint = "iotcom_ble_disconnect")]
    public static partial int Disconnect(BleHandle handle, byte* id, nuint idLen);

    [LibraryImport(Library, EntryPoint = "iotcom_ble_services")]
    public static partial int Services(BleHandle handle, byte* id, nuint idLen, byte* buf, nuint len, out nuint written);

    [LibraryImport(Library, EntryPoint = "iotcom_ble_read")]
    public static partial int Read(BleHandle handle, byte* id, nuint idLen, byte* ch, nuint chLen, uint timeoutMs, byte* buf, nuint len, out nuint written);

    [LibraryImport(Library, EntryPoint = "iotcom_ble_write")]
    public static partial int Write(BleHandle handle, byte* id, nuint idLen, byte* ch, nuint chLen, byte* data, nuint dataLen, byte withResponse, uint timeoutMs);

    [LibraryImport(Library, EntryPoint = "iotcom_ble_subscribe")]
    public static partial int Subscribe(BleHandle handle, byte* id, nuint idLen, byte* ch, nuint chLen, byte enable);

    public static string LastErrorMessage()
    {
        Span<byte> buf = stackalloc byte[512];
        fixed (byte* p = buf)
        {
            var n = LastError(p, (nuint)buf.Length);
            return n <= 0 ? "unknown native error" : Encoding.UTF8.GetString(buf[..Math.Min(n, buf.Length)]);
        }
    }

    public static void Check(int status)
    {
        if (status >= 0) return;
        var message = LastErrorMessage();
        throw status switch
        {
            ErrUnavailable => new TransportException(message),
            ErrTimeout => new TimeoutException(message),
            ErrUnknownDevice or ErrUnknownCharacteristic => new DeviceException(message),
            -2 => new ArgumentException(message),
            _ => new TransportException($"iotcom_ble error {status}: {message}"),
        };
    }
}

/// <summary>Owns a native central handle.</summary>
internal sealed class BleHandle : SafeHandle
{
    public BleHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public BleHandle(nint handle) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(handle);

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        BleNativeMethods.Free(handle);
        return true;
    }
}

/// <summary>
/// The platform's Bluetooth radio through the Rust <c>iotcom_ble</c> library (btleplug: WinRT on Windows, BlueZ on Linux,
/// CoreBluetooth on macOS). Native calls run on the thread pool; events are drained by a background loop.
/// </summary>
public sealed class NativeBleAdapter : IBleAdapter
{
    private readonly BleHandle _handle;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    private NativeBleAdapter(BleHandle handle)
    {
        _handle = handle;
        Description = Query();
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>True when the native library can be loaded on this machine.</summary>
    public static bool IsSupported => NativeLibraryLoader.IsAvailable(BleNativeMethods.Library, typeof(BleNativeMethods).Assembly);

    /// <summary>Opens the first Bluetooth adapter; throws <see cref="TransportException"/> when there is none.</summary>
    public static NativeBleAdapter Open()
    {
        uint abi;
        try
        {
            abi = BleNativeMethods.AbiVersion();
        }
        catch (DllNotFoundException ex)
        {
            throw new PlatformNotSupportedException(
                $"Native library '{BleNativeMethods.Library}' for {NativeLibraryLoader.RuntimeIdentifier} was not found. Set {NativeLibraryLoader.OverrideVariable} or use a supported RID.", ex);
        }

        if (abi != BleNativeMethods.ExpectedAbiVersion)
            throw new PlatformNotSupportedException($"Native ABI mismatch: library reports {abi}, binding expects {BleNativeMethods.ExpectedAbiVersion}.");
        BleNativeMethods.Check(BleNativeMethods.New(out var raw));
        return new NativeBleAdapter(new BleHandle(raw));
    }

    /// <inheritdoc />
    public string Description { get; }

    /// <inheritdoc />
    public event EventHandler<BleEvent>? Event;

    private unsafe string Query()
    {
        var buf = new byte[256];
        fixed (byte* p = buf)
        {
            return BleNativeMethods.AdapterInfo(_handle, p, (nuint)buf.Length, out var n) == 0 ? Encoding.UTF8.GetString(buf, 0, (int)n) : "Bluetooth adapter";
        }
    }

    private async Task PumpAsync()
    {
        var buf = new byte[4096];
        while (!_cts.IsCancellationRequested)
        {
            var status = Poll(buf, out var written);
            if (status == BleNativeMethods.ErrBufferTooSmall)
            {
                buf = new byte[(int)written];
                continue;
            }

            if (status == 1)
            {
                if (Parse(buf.AsSpan(0, (int)written)) is { } e) Event?.Invoke(this, e);
                continue;
            }

            try
            {
                await Task.Delay(20, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private unsafe int Poll(byte[] buf, out nuint written)
    {
        fixed (byte* p = buf) return BleNativeMethods.PollEvent(_handle, p, (nuint)buf.Length, out written);
    }

    /// <summary>Parses one JSON event as produced by the native library (exposed for tests and tools).</summary>
    public static BleEvent? Parse(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var r = doc.RootElement;
        var id = r.GetProperty("id").GetString() ?? "";
        switch (r.GetProperty("type").GetString())
        {
            case "advertisement":
                return new BleAdvertisementEvent(new BleAdvertisement
                {
                    Id = id,
                    Address = r.TryGetProperty("address", out var a) ? a.GetString() : null,
                    Name = r.TryGetProperty("name", out var n) ? n.GetString() : null,
                    Rssi = r.TryGetProperty("rssi", out var rssi) ? rssi.GetInt32() : null,
                    TxPower = r.TryGetProperty("tx_power", out var tx) ? tx.GetInt32() : null,
                    Services = [.. r.GetProperty("services").EnumerateArray().Select(s => Guid.Parse(s.GetString()!))],
                    ManufacturerData = r.GetProperty("manufacturer").EnumerateObject().ToDictionary(p => ushort.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), p => Convert.FromHexString(p.Value.GetString()!)),
                    ServiceData = r.GetProperty("service_data").EnumerateObject().ToDictionary(p => Guid.Parse(p.Name), p => Convert.FromHexString(p.Value.GetString()!)),
                });
            case "notification":
                return new BleNotificationEvent(id, Guid.Parse(r.GetProperty("uuid").GetString()!), Convert.FromHexString(r.GetProperty("value").GetString()!));
            case "connected":
                return new BleConnectionEvent(id, true);
            case "disconnected":
                return new BleConnectionEvent(id, false);
            default:
                return null;
        }
    }

    private static uint Ms(TimeSpan t) => (uint)Math.Clamp(t.TotalMilliseconds, 1, uint.MaxValue);

    /// <inheritdoc />
    public unsafe Task StartScanAsync(IReadOnlyList<Guid> services, CancellationToken ct) => Task.Run(() =>
    {
        var filter = Encoding.UTF8.GetBytes(string.Join(",", services ?? []));
        fixed (byte* p = filter) BleNativeMethods.Check(BleNativeMethods.StartScan(_handle, p, (nuint)filter.Length));
    }, ct);

    /// <inheritdoc />
    public unsafe Task StopScanAsync(CancellationToken ct) => Task.Run(() => BleNativeMethods.Check(BleNativeMethods.StopScan(_handle)), ct);

    /// <inheritdoc />
    public unsafe Task ConnectAsync(string id, TimeSpan timeout, CancellationToken ct) => Task.Run(() =>
    {
        var bytes = Encoding.UTF8.GetBytes(id);
        fixed (byte* p = bytes) BleNativeMethods.Check(BleNativeMethods.Connect(_handle, p, (nuint)bytes.Length, Ms(timeout)));
    }, ct);

    /// <inheritdoc />
    public unsafe Task DisconnectAsync(string id, CancellationToken ct) => Task.Run(() =>
    {
        var bytes = Encoding.UTF8.GetBytes(id);
        fixed (byte* p = bytes) BleNativeMethods.Check(BleNativeMethods.Disconnect(_handle, p, (nuint)bytes.Length));
    }, ct);

    /// <inheritdoc />
    public unsafe Task<IReadOnlyList<GattService>> GetServicesAsync(string id, CancellationToken ct) => Task.Run<IReadOnlyList<GattService>>(() =>
    {
        var idBytes = Encoding.UTF8.GetBytes(id);
        var json = Fetch((buf, len, written) =>
        {
            fixed (byte* p = idBytes) return BleNativeMethods.Services(_handle, p, (nuint)idBytes.Length, buf, len, out *written);
        });
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(s => new GattService(
            Guid.Parse(s.GetProperty("uuid").GetString()!), s.GetProperty("primary").GetBoolean(),
            [.. s.GetProperty("characteristics").EnumerateArray().Select(c => new GattCharacteristic(Guid.Parse(c.GetProperty("uuid").GetString()!),
                c.GetProperty("properties").EnumerateArray().Aggregate(GattProperties.None, (acc, p) => acc | p.GetString() switch
                {
                    "broadcast" => GattProperties.Broadcast, "read" => GattProperties.Read, "write_without_response" => GattProperties.WriteWithoutResponse,
                    "write" => GattProperties.Write, "notify" => GattProperties.Notify, "indicate" => GattProperties.Indicate, _ => GattProperties.None,
                })))]))];
    }, ct);

    private unsafe delegate int Fetcher(byte* buf, nuint len, nuint* written);

    private static unsafe byte[] Fetch(Fetcher call)
    {
        var buf = new byte[1024];
        while (true)
        {
            nuint written;
            int status;
            fixed (byte* p = buf) status = call(p, (nuint)buf.Length, &written);
            if (status == BleNativeMethods.ErrBufferTooSmall)
            {
                buf = new byte[(int)written];
                continue;
            }

            BleNativeMethods.Check(status);
            return buf[..(int)written];
        }
    }

    /// <inheritdoc />
    public unsafe Task<byte[]> ReadAsync(string id, Guid characteristic, TimeSpan timeout, CancellationToken ct) => Task.Run(() =>
    {
        var idBytes = Encoding.UTF8.GetBytes(id);
        var ch = Encoding.UTF8.GetBytes(characteristic.ToString());
        return Fetch((buf, len, written) =>
        {
            fixed (byte* pi = idBytes)
            fixed (byte* pc = ch)
                return BleNativeMethods.Read(_handle, pi, (nuint)idBytes.Length, pc, (nuint)ch.Length, Ms(timeout), buf, len, out *written);
        });
    }, ct);

    /// <inheritdoc />
    public unsafe Task WriteAsync(string id, Guid characteristic, ReadOnlyMemory<byte> value, bool withResponse, TimeSpan timeout, CancellationToken ct) => Task.Run(() =>
    {
        var idBytes = Encoding.UTF8.GetBytes(id);
        var ch = Encoding.UTF8.GetBytes(characteristic.ToString());
        var data = value.ToArray();
        fixed (byte* pi = idBytes)
        fixed (byte* pc = ch)
        fixed (byte* pd = data)
            BleNativeMethods.Check(BleNativeMethods.Write(_handle, pi, (nuint)idBytes.Length, pc, (nuint)ch.Length, pd, (nuint)data.Length, withResponse ? (byte)1 : (byte)0, Ms(timeout)));
    }, ct);

    /// <inheritdoc />
    public unsafe Task SetNotifyAsync(string id, Guid characteristic, bool enable, CancellationToken ct) => Task.Run(() =>
    {
        var idBytes = Encoding.UTF8.GetBytes(id);
        var ch = Encoding.UTF8.GetBytes(characteristic.ToString());
        fixed (byte* pi = idBytes)
        fixed (byte* pc = ch)
            BleNativeMethods.Check(BleNativeMethods.Subscribe(_handle, pi, (nuint)idBytes.Length, pc, (nuint)ch.Length, enable ? (byte)1 : (byte)0));
    }, ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _handle.Dispose();
        _cts.Dispose();
    }
}
