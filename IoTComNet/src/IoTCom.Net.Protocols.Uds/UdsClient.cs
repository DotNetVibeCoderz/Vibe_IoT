using System.Buffers.Binary;
using System.Text;
using IoTCom.Net.Protocols.IsoTp;
using IoTCom.Net.Transport.Can;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Uds;

/// <summary>UDS tester options.</summary>
public sealed class UdsClientOptions
{
    /// <summary>Request identifier (physical addressing), e.g. 0x7E0.</summary>
    public uint RequestId { get; set; } = 0x7E0;

    /// <summary>Response identifier, e.g. 0x7E8.</summary>
    public uint ResponseId { get; set; } = 0x7E8;

    /// <summary>P2 client: time to wait for the first response.</summary>
    public TimeSpan P2 { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>P2* client: time to wait after a "response pending" (NRC 0x78).</summary>
    public TimeSpan P2Extended { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a "busy, repeat request" (NRC 0x21) is retried.</summary>
    public int BusyRetries { get; set; } = 3;

    /// <summary>Blocks state-changing services (reset, write DID, clear DTC, routines, download).</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Further ISO-TP settings (identifiers are taken from this class).</summary>
    public Action<IsoTpOptions>? IsoTp { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Enables read-only mode.</summary>
    public UdsClientOptions AsReadOnly()
    {
        ReadOnly = true;
        return this;
    }
}

/// <summary>
/// UDS (ISO 14229) tester over ISO-TP. Handles response-pending (0x78) and busy (0x21) answers, suppressed
/// positive responses and read-only mode; negative responses throw <see cref="UdsNegativeResponseException"/>.
/// </summary>
/// <example>
/// <code>
/// await using var bus = await CanBus.OpenAsync("socketcan:can0");
/// await using var uds = UdsClient.Create(bus, o => { o.RequestId = 0x7E0; o.ResponseId = 0x7E8; });
/// await uds.ConnectAsync();
/// string vin = await uds.ReadVinAsync();
/// var dtcs = await uds.ReadDtcsAsync();
/// </code>
/// </example>
public sealed class UdsClient : EndpointBase, IClientEndpoint
{
    private readonly IsoTpChannel _channel;
    private readonly UdsClientOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private UdsClient(IsoTpChannel channel, UdsClientOptions options) : base("uds", options.Logger)
    {
        _channel = channel;
        _options = options;
        Name = $"{options.RequestId:X3}→{options.ResponseId:X3}";
    }

    /// <summary>Creates a tester on <paramref name="bus"/>.</summary>
    public static UdsClient Create(ICanBus bus, Action<UdsClientOptions>? configure = null)
    {
        var options = new UdsClientOptions();
        configure?.Invoke(options);
        var channel = IsoTpChannel.Create(bus, o =>
        {
            options.IsoTp?.Invoke(o);
            o.TxId = options.RequestId;
            o.RxId = options.ResponseId;
            o.Logger ??= options.Logger;
        });
        return new UdsClient(channel, options);
    }

    /// <summary>Options.</summary>
    public UdsClientOptions Options => _options;

    /// <summary>The ISO-TP channel.</summary>
    public IsoTpChannel Channel => _channel;

    /// <summary>Current session as last confirmed by the ECU.</summary>
    public UdsSession Session { get; private set; } = UdsSession.Default;

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        SetState(EndpointState.Connecting);
        try
        {
            await _channel.ConnectAsync(ct).ConfigureAwait(false);
            SetState(EndpointState.Connected);
        }
        catch (Exception ex)
        {
            SetState(EndpointState.Faulted, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        await _channel.DisconnectAsync(ct).ConfigureAwait(false);
        SetState(EndpointState.Disconnected);
    }

    /// <summary>
    /// Sends a raw request and returns the positive response (including its SID byte), or null when the request
    /// suppressed the positive response and none arrived.
    /// </summary>
    public async Task<byte[]?> RequestAsync(ReadOnlyMemory<byte> request, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (request.IsEmpty) throw new ArgumentException("Empty request.", nameof(request));
        var sid = request.Span[0];
        if (_options.ReadOnly && UdsService.IsWrite(sid))
            throw new ReadOnlyModeException($"{UdsService.Name(sid)} is blocked: the UDS client is in read-only mode.");
        var suppress = request.Length >= 2 && HasSubFunction(sid) && (request.Span[1] & UdsService.SuppressPositiveResponse) != 0;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                _channel.DiscardPending();
                Tap(FrameDirection.Outbound, request.Span, () => UdsService.Name(sid));
                await _channel.SendAsync(request, ct).ConfigureAwait(false);
                var timeout = _options.P2;
                while (true)
                {
                    var response = await _channel.ReceiveAsync(suppress ? TimeSpan.FromMilliseconds(Math.Min(timeout.TotalMilliseconds, 200)) : timeout, ct).ConfigureAwait(false);
                    if (response is null)
                    {
                        if (suppress) return null;
                        throw new IoTComTimeoutException($"No response to {UdsService.Name(sid)} within {timeout.TotalMilliseconds:0} ms.");
                    }
                    if (response.Length == 0) continue;
                    var mine = response[0] == (byte)(sid + UdsService.PositiveOffset) || (response[0] == UdsService.NegativeResponse && response.Length >= 3 && response[1] == sid);
                    if (!mine)
                    {
                        // Another tester (e.g. an OBD scan tool) shares the response identifier.
                        Logger.LogDebug("UDS: ignoring unrelated response {Sid:X2}", response[0]);
                        continue;
                    }
                    Tap(FrameDirection.Inbound, response, () => UdsService.Name(response[0]));
                    if (response[0] == UdsService.NegativeResponse)
                    {
                        var nrc = (UdsNrc)response[2];
                        if (nrc == UdsNrc.ResponsePending)
                        {
                            timeout = _options.P2Extended;
                            continue;
                        }
                        if (nrc == UdsNrc.BusyRepeatRequest && attempt < _options.BusyRetries)
                        {
                            await Task.Delay(_options.P2 / 4, ct).ConfigureAwait(false);
                            break;
                        }
                        throw new UdsNegativeResponseException(sid, nrc);
                    }
                    return response;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool HasSubFunction(byte sid) => sid is UdsService.DiagnosticSessionControl or UdsService.EcuReset
        or UdsService.SecurityAccess or UdsService.CommunicationControl or UdsService.TesterPresent
        or UdsService.ControlDtcSetting or UdsService.ReadDtcInformation or UdsService.RoutineControl;

    /// <summary>0x10: switches session; returns the ECU's P2 / P2* timings.</summary>
    public async Task<(TimeSpan P2, TimeSpan P2Extended)> StartSessionAsync(UdsSession session, CancellationToken ct = default)
    {
        var r = await RequestAsync(new byte[] { UdsService.DiagnosticSessionControl, (byte)session }, ct).ConfigureAwait(false);
        Session = session;
        if (r is { Length: >= 6 })
            return (TimeSpan.FromMilliseconds(BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(2))), TimeSpan.FromMilliseconds(BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(4)) * 10));
        return (_options.P2, _options.P2Extended);
    }

    /// <summary>0x11: resets the ECU (write service).</summary>
    public Task EcuResetAsync(UdsResetType type = UdsResetType.Hard, CancellationToken ct = default) =>
        RequestAsync(new byte[] { UdsService.EcuReset, (byte)type }, ct);

    /// <summary>0x3E: keeps a non-default session alive (positive response suppressed by default).</summary>
    public Task TesterPresentAsync(bool suppressResponse = true, CancellationToken ct = default) =>
        RequestAsync(new byte[] { UdsService.TesterPresent, (byte)(suppressResponse ? 0x80 : 0x00) }, ct);

    /// <summary>0x22: reads one data identifier; returns the data record.</summary>
    public async Task<byte[]> ReadDataByIdentifierAsync(ushort did, CancellationToken ct = default)
    {
        var r = await RequestAsync(new byte[] { UdsService.ReadDataByIdentifier, (byte)(did >> 8), (byte)did }, ct).ConfigureAwait(false);
        if (r is null || r.Length < 3 || BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(1)) != did)
            throw new ProtocolException($"Unexpected response to ReadDataByIdentifier 0x{did:X4}.");
        return r[3..];
    }

    /// <summary>0x22: reads a data identifier as ASCII text (trailing NUL/space/0xFF removed).</summary>
    public async Task<string> ReadStringAsync(ushort did, CancellationToken ct = default)
    {
        var data = await ReadDataByIdentifierAsync(did, ct).ConfigureAwait(false);
        return Encoding.ASCII.GetString(data).TrimEnd('\0', ' ', '\xFF');
    }

    /// <summary>0x22 F190: vehicle identification number.</summary>
    public Task<string> ReadVinAsync(CancellationToken ct = default) => ReadStringAsync(UdsDid.Vin, ct);

    /// <summary>0x2E: writes a data identifier (write service; usually needs an extended session and security access).</summary>
    public Task WriteDataByIdentifierAsync(ushort did, ReadOnlySpan<byte> data, CancellationToken ct = default)
    {
        var req = new byte[3 + data.Length];
        req[0] = UdsService.WriteDataByIdentifier;
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(1), did);
        data.CopyTo(req.AsSpan(3));
        return RequestAsync(req, ct);
    }

    /// <summary>0x27: requests a seed for <paramref name="level"/> (odd number) and sends the key computed by <paramref name="computeKey"/>.</summary>
    /// <returns>False when the ECU was already unlocked (zero seed).</returns>
    public async Task<bool> SecurityAccessAsync(byte level, Func<byte[], byte[]> computeKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(computeKey);
        if (level % 2 == 0) throw new ArgumentException("Security levels requesting a seed are odd (0x01, 0x03…).", nameof(level));
        var seedResponse = await RequestAsync(new byte[] { UdsService.SecurityAccess, level }, ct).ConfigureAwait(false);
        var seed = seedResponse is { Length: > 2 } s ? s[2..] : [];
        if (seed.All(b => b == 0)) return false;
        var key = computeKey(seed);
        var req = new byte[2 + key.Length];
        req[0] = UdsService.SecurityAccess;
        req[1] = (byte)(level + 1);
        key.CopyTo(req, 2);
        await RequestAsync(req, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>0x19 02: DTCs whose status matches <paramref name="statusMask"/>.</summary>
    public async Task<IReadOnlyList<Dtc>> ReadDtcsAsync(DtcStatus statusMask = (DtcStatus)0xFF, CancellationToken ct = default)
    {
        var r = await RequestAsync(new byte[] { UdsService.ReadDtcInformation, 0x02, (byte)statusMask }, ct).ConfigureAwait(false);
        var list = new List<Dtc>();
        if (r is null || r.Length < 3) return list;
        for (var i = 3; i + 4 <= r.Length; i += 4)
            list.Add(new Dtc((uint)(r[i] << 16 | r[i + 1] << 8 | r[i + 2]), (DtcStatus)r[i + 3]));
        return list;
    }

    /// <summary>0x14: clears DTCs (all groups by default; write service).</summary>
    public Task ClearDtcsAsync(uint group = 0xFFFFFF, CancellationToken ct = default) =>
        RequestAsync(new byte[] { UdsService.ClearDiagnosticInformation, (byte)(group >> 16), (byte)(group >> 8), (byte)group }, ct);

    /// <summary>0x31: starts (0x01), stops (0x02) or reads results of (0x03) a routine; returns the status record.</summary>
    public async Task<byte[]> RoutineControlAsync(byte type, ushort routineId, ReadOnlyMemory<byte> options = default, CancellationToken ct = default)
    {
        var req = new byte[4 + options.Length];
        req[0] = UdsService.RoutineControl;
        req[1] = type;
        BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(2), routineId);
        options.Span.CopyTo(req.AsSpan(4));
        var r = await RequestAsync(req, ct).ConfigureAwait(false);
        return r is { Length: > 4 } ? r[4..] : [];
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await _channel.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

/// <summary>Common data identifiers (ISO 14229-1 Annex C).</summary>
public static class UdsDid
{
    /// <summary>0xF186 active diagnostic session.</summary>
    public const ushort ActiveSession = 0xF186;
    /// <summary>0xF187 spare part number.</summary>
    public const ushort SparePartNumber = 0xF187;
    /// <summary>0xF189 ECU software version.</summary>
    public const ushort SoftwareVersion = 0xF189;
    /// <summary>0xF18C ECU serial number.</summary>
    public const ushort SerialNumber = 0xF18C;
    /// <summary>0xF190 VIN.</summary>
    public const ushort Vin = 0xF190;
    /// <summary>0xF191 ECU hardware number.</summary>
    public const ushort HardwareNumber = 0xF191;
    /// <summary>0xF197 system name.</summary>
    public const ushort SystemName = 0xF197;
    /// <summary>0xF198 repair shop code / tester serial.</summary>
    public const ushort RepairShopCode = 0xF198;
}
