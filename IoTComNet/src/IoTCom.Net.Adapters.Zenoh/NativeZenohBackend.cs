using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using IoTCom.Net.Native;

namespace IoTCom.Net.Adapters.Zenoh;

/// <summary>Source-generated P/Invoke bindings for <c>iotcom_zenoh</c> (AOT and trimming friendly).</summary>
internal static unsafe partial class ZenohNativeMethods
{
    public const string Library = "iotcom_zenoh";

    /// <summary>ABI version this binding was written for (iotcom-ffi-support ABI_VERSION).</summary>
    public const uint ExpectedAbiVersion = 1;

    public const int ErrInvalidArg = -2;
    public const int ErrBufferTooSmall = -4;
    public const int ErrOpen = -20;
    public const int ErrZenoh = -21;
    public const int ErrUnknownId = -22;
    public const int ErrClosed = -23;

    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "Registers the native resolver before the first P/Invoke.")]
    internal static void Init() => NativeLibraryLoader.Register(typeof(ZenohNativeMethods).Assembly);

    [LibraryImport(Library, EntryPoint = "iotcom_abi_version")]
    public static partial uint AbiVersion();

    [LibraryImport(Library, EntryPoint = "iotcom_last_error")]
    public static partial int LastError(byte* buf, nuint len);

    [LibraryImport(Library, EntryPoint = "iotcom_zenoh_open")]
    public static partial int Open(byte* mode, nuint modeLen, byte* connect, nuint connectLen, byte* listen, nuint listenLen, byte multicast, out nint handle);

    [LibraryImport(Library, EntryPoint = "iotcom_zenoh_close")]
    public static partial int Close(ZenohHandle handle);

    [LibraryImport(Library, EntryPoint = "iotcom_zenoh_free")]
    public static partial void Free(nint handle);

    [LibraryImport(Library, EntryPoint = "iotcom_zenoh_info")]
    public static partial int Info(ZenohHandle handle, byte* buf, nuint len, out nuint written);

    [LibraryImport(Library, EntryPoint = "iotcom_zenoh_put")]
    public static partial int Put(ZenohHandle handle, byte* key, nuint keyLen, byte* data, nuint dataLen, byte* encoding, nuint encodingLen);

    [LibraryImport(Library, EntryPoint = "iotcom_zenoh_delete")]
    public static partial int Delete(ZenohHandle handle, byte* key, nuint keyLen);

    [LibraryImport(Library, EntryPoint = "iotcom_zenoh_declare_subscriber")]
    public static partial int DeclareSubscriber(ZenohHandle handle, byte* key, nuint keyLen, out ulong id);

    [LibraryImport(Library, EntryPoint = "iotcom_zenoh_undeclare_subscriber")]
    public static partial int UndeclareSubscriber(ZenohHandle handle, ulong id);

    [LibraryImport(Library, EntryPoint = "iotcom_zenoh_declare_queryable")]
    public static partial int DeclareQueryable(ZenohHandle handle, byte* key, nuint keyLen, out ulong id);

    [LibraryImport(Library, EntryPoint = "iotcom_zenoh_undeclare_queryable")]
    public static partial int UndeclareQueryable(ZenohHandle handle, ulong id);

    [LibraryImport(Library, EntryPoint = "iotcom_zenoh_reply")]
    public static partial int Reply(ZenohHandle handle, ulong query, byte* key, nuint keyLen, byte* data, nuint dataLen, byte* encoding, nuint encodingLen);

    [LibraryImport(Library, EntryPoint = "iotcom_zenoh_reply_err")]
    public static partial int ReplyErr(ZenohHandle handle, ulong query, byte* data, nuint dataLen);

    [LibraryImport(Library, EntryPoint = "iotcom_zenoh_finish_query")]
    public static partial int FinishQuery(ZenohHandle handle, ulong query);

    [LibraryImport(Library, EntryPoint = "iotcom_zenoh_get")]
    public static partial int Get(ZenohHandle handle, byte* selector, nuint selectorLen, byte* data, nuint dataLen, uint timeoutMs, out ulong id);

    [LibraryImport(Library, EntryPoint = "iotcom_zenoh_poll_event")]
    public static partial int PollEvent(ZenohHandle handle, byte* buf, nuint len, out nuint written);

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
            ErrOpen or ErrZenoh => new TransportException(message),
            ErrClosed => new ObjectDisposedException(nameof(ZenohSession), message),
            ErrInvalidArg => new ArgumentException(message),
            ErrUnknownId => new InvalidOperationException(message),
            _ => new TransportException($"iotcom_zenoh error {status}: {message}"),
        };
    }
}

/// <summary>Owns a native session handle.</summary>
internal sealed class ZenohHandle : SafeHandle
{
    public ZenohHandle() : base(IntPtr.Zero, ownsHandle: true)
    {
    }

    public ZenohHandle(nint handle) : base(IntPtr.Zero, ownsHandle: true) => SetHandle(handle);

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        ZenohNativeMethods.Free(handle);
        return true;
    }
}

/// <summary>
/// A Zenoh session through the Rust <c>iotcom_zenoh</c> library (the <c>zenoh</c> crate, TCP and UDP transports). Native
/// calls run on the thread pool; events (samples, queries, replies) are drained by a background loop.
/// </summary>
public sealed class NativeZenohBackend : IZenohBackend
{
    private readonly ZenohHandle _handle;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, Action<ZenohSample>> _subscribers = [];
    private readonly Dictionary<ulong, Action<string, string, byte[]?, IZenohQueryResponder>> _queryables = [];
    private readonly Dictionary<ulong, GetState> _gets = [];

    private NativeZenohBackend(ZenohHandle handle, string description)
    {
        _handle = handle;
        Description = description;
        Zid = ReadInfo().Zid;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>True when the native library can be loaded on this machine.</summary>
    public static bool IsSupported => NativeLibraryLoader.IsAvailable(ZenohNativeMethods.Library, typeof(ZenohNativeMethods).Assembly);

    /// <summary>Opens a session with the given options; throws <see cref="TransportException"/> when the configuration or endpoints are refused.</summary>
    public static unsafe NativeZenohBackend Open(ZenohOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        uint abi;
        try
        {
            abi = ZenohNativeMethods.AbiVersion();
        }
        catch (DllNotFoundException ex)
        {
            throw new PlatformNotSupportedException(
                $"Native library '{ZenohNativeMethods.Library}' for {NativeLibraryLoader.RuntimeIdentifier} was not found. Set {NativeLibraryLoader.OverrideVariable} or use a supported RID.", ex);
        }

        if (abi != ZenohNativeMethods.ExpectedAbiVersion)
            throw new PlatformNotSupportedException($"Native ABI mismatch: library reports {abi}, binding expects {ZenohNativeMethods.ExpectedAbiVersion}.");

        var mode = Encoding.UTF8.GetBytes(options.Mode == ZenohMode.Client ? "client" : "peer");
        var connect = Encoding.UTF8.GetBytes(string.Join(",", options.ConnectEndpoints));
        var listen = Encoding.UTF8.GetBytes(string.Join(",", options.ListenEndpoints));
        nint raw;
        fixed (byte* pm = mode)
        fixed (byte* pc = connect)
        fixed (byte* pl = listen)
            ZenohNativeMethods.Check(ZenohNativeMethods.Open(pm, (nuint)mode.Length, pc, (nuint)connect.Length, pl, (nuint)listen.Length, options.MulticastScouting ? (byte)1 : (byte)0, out raw));
        return new NativeZenohBackend(new ZenohHandle(raw), $"zenoh {(options.Mode == ZenohMode.Client ? "client" : "peer")} (native)");
    }

    /// <inheritdoc />
    public string Zid { get; }

    /// <inheritdoc />
    public string Description { get; }

    /// <summary>Session info as reported by Zenoh: own id plus the peers and routers currently connected.</summary>
    public unsafe (string Zid, IReadOnlyList<string> Peers, IReadOnlyList<string> Routers) ReadInfo()
    {
        var json = Fetch((buf, len, written) => ZenohNativeMethods.Info(_handle, buf, len, out *written));
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return (r.GetProperty("zid").GetString() ?? "", [.. r.GetProperty("peers").EnumerateArray().Select(p => p.GetString() ?? "")], [.. r.GetProperty("routers").EnumerateArray().Select(p => p.GetString() ?? "")]);
    }

    private unsafe delegate int Fetcher(byte* buf, nuint len, nuint* written);

    private static unsafe byte[] Fetch(Fetcher call)
    {
        var buf = new byte[1024];
        while (true)
        {
            nuint written;
            int status;
            fixed (byte* p = buf) status = call(p, (nuint)buf.Length, &written);
            if (status == ZenohNativeMethods.ErrBufferTooSmall)
            {
                buf = new byte[(int)written];
                continue;
            }

            ZenohNativeMethods.Check(status);
            return buf[..(int)written];
        }
    }

    private async Task PumpAsync()
    {
        var buf = new byte[8192];
        while (!_cts.IsCancellationRequested)
        {
            var status = Poll(buf, out var written);
            if (status == ZenohNativeMethods.ErrBufferTooSmall)
            {
                buf = new byte[(int)written];
                continue;
            }

            if (status == 1)
            {
                try
                {
                    Dispatch(buf.AsSpan(0, (int)written));
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException)
                {
                    System.Diagnostics.Debug.WriteLine($"Zenoh: dropped a malformed event: {ex.Message}");
                }

                continue;
            }

            if (status < 0) return;   // the handle is gone
            try
            {
                await Task.Delay(10, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private unsafe int Poll(byte[] buf, out nuint written)
    {
        fixed (byte* p = buf) return ZenohNativeMethods.PollEvent(_handle, p, (nuint)buf.Length, out written);
    }

    private static ZenohSample ParseSample(JsonElement r) => new(
        r.GetProperty("key").GetString() ?? "",
        r.GetProperty("kind").GetString() == "delete" ? ZenohSampleKind.Delete : ZenohSampleKind.Put,
        Convert.FromBase64String(r.GetProperty("payload").GetString() ?? ""),
        r.TryGetProperty("encoding", out var e) && e.GetString() is { Length: > 0 } enc ? enc : null,
        DateTimeOffset.UtcNow);

    /// <summary>Handles one JSON event from the native queue (exposed for tests).</summary>
    internal void Dispatch(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var r = doc.RootElement;
        switch (r.GetProperty("type").GetString())
        {
            case "sample":
            {
                Action<ZenohSample>? handler;
                lock (_gate) _subscribers.TryGetValue(r.GetProperty("sub").GetUInt64(), out handler);
                handler?.Invoke(ParseSample(r));
                break;
            }

            case "query":
            {
                Action<string, string, byte[]?, IZenohQueryResponder>? handler;
                lock (_gate) _queryables.TryGetValue(r.GetProperty("queryable").GetUInt64(), out handler);
                var queryId = r.GetProperty("query").GetUInt64();
                var responder = new NativeResponder(this, queryId);
                if (handler is null)
                {
                    responder.Finish();
                    break;
                }

                var key = r.GetProperty("key").GetString() ?? "";
                var parameters = r.GetProperty("parameters").GetString() ?? "";
                var payload = Convert.FromBase64String(r.GetProperty("payload").GetString() ?? "");
                // Handlers may be slow or asynchronous: keep the event pump moving.
                _ = Task.Run(() =>
                {
                    try
                    {
                        handler(key, parameters, payload.Length == 0 ? null : payload, responder);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        responder.Finish();
                        System.Diagnostics.Debug.WriteLine($"Zenoh queryable failed: {ex.Message}");
                    }
                });
                break;
            }

            case "reply" or "reply_error" or "get_done":
            {
                GetState? state;
                var id = r.GetProperty("get").GetUInt64();
                lock (_gate) _gets.TryGetValue(id, out state);
                if (state is null) break;
                switch (r.GetProperty("type").GetString())
                {
                    case "reply":
                        state.OnReply(new ZenohReply { Sample = ParseSample(r) });
                        break;
                    case "reply_error":
                        state.OnReply(new ZenohReply { Error = Convert.FromBase64String(r.GetProperty("payload").GetString() ?? "") });
                        break;
                    default:
                        lock (_gate) _gets.Remove(id);
                        state.Done.TrySetResult();
                        break;
                }

                break;
            }
        }
    }

    private sealed class GetState(Action<ZenohReply> onReply)
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnReply(ZenohReply reply) => onReply(reply);
    }

    private sealed class NativeResponder(NativeZenohBackend owner, ulong query) : IZenohQueryResponder
    {
        private int _finished;

        public unsafe ValueTask ReplyAsync(string key, ReadOnlyMemory<byte> payload, string? encoding, CancellationToken ct)
        {
            var k = Encoding.UTF8.GetBytes(key);
            var e = encoding is null ? [] : Encoding.UTF8.GetBytes(encoding);
            var data = payload.ToArray();
            fixed (byte* pk = k)
            fixed (byte* pd = data)
            fixed (byte* pe = e)
                ZenohNativeMethods.Check(ZenohNativeMethods.Reply(owner._handle, query, pk, (nuint)k.Length, pd, (nuint)data.Length, pe, (nuint)e.Length));
            return ValueTask.CompletedTask;
        }

        public unsafe ValueTask ReplyErrorAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            var data = payload.ToArray();
            fixed (byte* pd = data) ZenohNativeMethods.Check(ZenohNativeMethods.ReplyErr(owner._handle, query, pd, (nuint)data.Length));
            return ValueTask.CompletedTask;
        }

        public void Finish()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0 || owner._handle.IsClosed) return;
            // The status is not interesting: an already-finished or closed session simply has nothing left to finish.
            _ = ZenohNativeMethods.FinishQuery(owner._handle, query);
        }
    }

    /// <inheritdoc />
    public unsafe Task PutAsync(string key, ReadOnlyMemory<byte> payload, string? encoding, CancellationToken ct) => Task.Run(() =>
    {
        var k = Encoding.UTF8.GetBytes(key);
        var e = encoding is null ? [] : Encoding.UTF8.GetBytes(encoding);
        var data = payload.ToArray();
        fixed (byte* pk = k)
        fixed (byte* pd = data)
        fixed (byte* pe = e)
            ZenohNativeMethods.Check(ZenohNativeMethods.Put(_handle, pk, (nuint)k.Length, pd, (nuint)data.Length, pe, (nuint)e.Length));
    }, ct);

    /// <inheritdoc />
    public unsafe Task DeleteAsync(string key, CancellationToken ct) => Task.Run(() =>
    {
        var k = Encoding.UTF8.GetBytes(key);
        fixed (byte* pk = k) ZenohNativeMethods.Check(ZenohNativeMethods.Delete(_handle, pk, (nuint)k.Length));
    }, ct);

    /// <inheritdoc />
    public unsafe IDisposable DeclareSubscriber(string keyExpr, Action<ZenohSample> handler)
    {
        var k = Encoding.UTF8.GetBytes(keyExpr);
        ulong id;
        // Hold the gate across declare + register so the event pump cannot see a sample before its handler exists.
        lock (_gate)
        {
            fixed (byte* pk = k) ZenohNativeMethods.Check(ZenohNativeMethods.DeclareSubscriber(_handle, pk, (nuint)k.Length, out id));
            _subscribers[id] = handler;
        }

        return new Undeclare(() =>
        {
            lock (_gate) _subscribers.Remove(id);
            if (!_handle.IsClosed) _ = ZenohNativeMethods.UndeclareSubscriber(_handle, id);
        });
    }

    /// <inheritdoc />
    public unsafe IDisposable DeclareQueryable(string keyExpr, Action<string, string, byte[]?, IZenohQueryResponder> handler)
    {
        var k = Encoding.UTF8.GetBytes(keyExpr);
        ulong id;
        lock (_gate)
        {
            fixed (byte* pk = k) ZenohNativeMethods.Check(ZenohNativeMethods.DeclareQueryable(_handle, pk, (nuint)k.Length, out id));
            _queryables[id] = handler;
        }

        return new Undeclare(() =>
        {
            lock (_gate) _queryables.Remove(id);
            if (!_handle.IsClosed) _ = ZenohNativeMethods.UndeclareQueryable(_handle, id);
        });
    }

    /// <inheritdoc />
    public async Task GetAsync(string selector, ReadOnlyMemory<byte> payload, TimeSpan timeout, Action<ZenohReply> onReply, CancellationToken ct)
    {
        var state = new GetState(onReply);
        var id = await Task.Run(() => StartGet(selector, payload.ToArray(), timeout, state), ct).ConfigureAwait(false);
        try
        {
            await state.Done.Task.WaitAsync(timeout + TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _gets.Remove(id);
        }
    }

    private unsafe ulong StartGet(string selector, byte[] payload, TimeSpan timeout, GetState state)
    {
        var s = Encoding.UTF8.GetBytes(selector);
        var ms = (uint)Math.Clamp(timeout.TotalMilliseconds, 1, uint.MaxValue);
        ulong id;
        lock (_gate)
        {
            fixed (byte* ps = s)
            fixed (byte* pd = payload)
                ZenohNativeMethods.Check(ZenohNativeMethods.Get(_handle, ps, (nuint)s.Length, pd, (nuint)payload.Length, ms, out id));
            _gets[id] = state;
        }

        return id;
    }

    private sealed class Undeclare(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }

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

        _ = await Task.Run(() => ZenohNativeMethods.Close(_handle)).ConfigureAwait(false);
        await Task.Run(_handle.Dispose).ConfigureAwait(false);
        _cts.Dispose();
    }
}
