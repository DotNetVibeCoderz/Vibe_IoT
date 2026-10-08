using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Dlms;

/// <summary>A meter refused a request.</summary>
public sealed class DlmsException : DeviceException
{
    /// <summary>Creates the exception.</summary>
    public DlmsException() : base("The meter refused the request.") { }

    /// <summary>Creates the exception.</summary>
    public DlmsException(string message) : base(message) { }

    /// <summary>Creates the exception.</summary>
    public DlmsException(string message, Exception? inner) : base(message, inner) { }

    /// <summary>Creates the exception for a data access result.</summary>
    public DlmsException(string message, DataAccessResult result) : base(message) => Result = result;

    /// <summary>The meter's answer, when it was a Data-Access-Result.</summary>
    public DataAccessResult? Result { get; }
}

/// <summary>DLMS client options.</summary>
public sealed class DlmsClientOptions : ITransportBuilder<DlmsClientOptions>
{
    /// <summary>Transport (serial optical probe or RS-485, TCP).</summary>
    public TransportFactory? TransportFactory { get; set; }

    /// <summary>HDLC (serial, optical, many TCP gateways) or the wrapper (meters on TCP port 4059).</summary>
    public DlmsFraming Framing { get; set; } = DlmsFraming.Hdlc;

    /// <summary>Client SAP: 16 public client (read-only, no authentication), 1 management client.</summary>
    public ushort ClientAddress { get; set; } = DlmsServer.PublicClient;

    /// <summary>Server upper (logical device) address.</summary>
    public ushort ServerLogicalAddress { get; set; } = 1;

    /// <summary>Server lower (physical) address; often 16 + the last digits of the serial number.</summary>
    public ushort ServerPhysicalAddress { get; set; } = 17;

    /// <summary>Authentication.</summary>
    public DlmsAuthentication Authentication { get; set; }

    /// <summary>LLS password.</summary>
    public string? Password { get; set; }

    /// <summary>Client system title and suite 0 keys (HLS and ciphered APDUs).</summary>
    public DlmsSecurityKeys? Security { get; set; }

    /// <summary>Blocks SET and ACTION (default true; meters are billing devices).</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>Time to wait for each response.</summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Largest APDU the client accepts.</summary>
    public ushort MaxPduSize { get; set; } = 0xFFFF;

    /// <summary>HDLC parameters the client proposes.</summary>
    public HdlcParameters Hdlc { get; set; } = new();

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public DlmsClientOptions UseTransport(TransportFactory factory)
    {
        TransportFactory = factory;
        return this;
    }

    /// <summary>Management client with a password (low-level security).</summary>
    public DlmsClientOptions WithPassword(string password)
    {
        (ClientAddress, Authentication, Password) = (DlmsServer.ManagementClient, DlmsAuthentication.Low, password);
        return this;
    }

    /// <summary>Management client with HLS (GMAC) and ciphered APDUs.</summary>
    public DlmsClientOptions WithHighSecurity(DlmsSecurityKeys keys)
    {
        (ClientAddress, Authentication, Security) = (DlmsServer.ManagementClient, DlmsAuthentication.HighGmac, keys);
        return this;
    }
}

/// <summary>A register value: the scaled physical value and its unit.</summary>
/// <param name="Value">Physical value (raw × 10^scaler).</param>
/// <param name="Unit">Unit code.</param>
/// <param name="Raw">Raw attribute 2.</param>
/// <param name="Scaler">Scaler.</param>
public sealed record RegisterValue(double Value, byte Unit, CosemData Raw, sbyte Scaler)
{
    /// <summary>Unit symbol.</summary>
    public string UnitSymbol => CosemUnit.Symbol(Unit);

    /// <inheritdoc />
    public override string ToString() => $"{Value.ToString(Scaler < 0 ? "0." + new string('0', -Scaler) : "0", System.Globalization.CultureInfo.InvariantCulture)} {UnitSymbol}".TrimEnd();
}

/// <summary>An entry of the association's object list.</summary>
/// <param name="ClassId">Class.</param>
/// <param name="Version">Class version.</param>
/// <param name="LogicalName">OBIS code.</param>
public sealed record CosemObjectInfo(ushort ClassId, byte Version, ObisCode LogicalName)
{
    /// <summary>Catalog description.</summary>
    public string? Description => LogicalName.Description;
}

/// <summary>A profile read: the columns (capture objects) and the rows.</summary>
/// <param name="Columns">Capture objects.</param>
/// <param name="Rows">Rows.</param>
public sealed record ProfileTable(IReadOnlyList<CaptureObject> Columns, IReadOnlyList<CosemData[]> Rows);

/// <summary>
/// A DLMS/COSEM client (reads meters): HDLC or wrapper link, association with no, low or high (GMAC) security,
/// GET with block transfer and selective access, SET and ACTION (blocked in read-only mode).
/// </summary>
/// <example>
/// <code>
/// await using var meter = DlmsClient.Create(o => o.UseSerial("COM3", 9600));   // optical probe, public client
/// await meter.ConnectAsync();
/// var energy = await meter.ReadRegisterAsync(ObisCode.Parse("1.0.1.8.0.255"));  // 1234.567 kWh
/// </code>
/// </example>
public sealed class DlmsClient : EndpointBase, IClientEndpoint
{
    private readonly DlmsClientOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITransport? _transport;
    private DlmsLink? _link;
    private DlmsSecurity? _security;
    private int _invoke;

    private DlmsClient(DlmsClientOptions options) : base("dlms", options.Logger)
    {
        _options = options;
        Name = options.Name;
    }

    /// <summary>Creates a client.</summary>
    public static DlmsClient Create(Action<DlmsClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new DlmsClientOptions();
        configure(o);
        if (o.TransportFactory is null) throw new ArgumentException("A transport is required (UseSerial, UseTcp, UseInMemory).", nameof(configure));
        if (o.Authentication == DlmsAuthentication.Low && o.Password is null) throw new ArgumentException("Low-level security needs a password.", nameof(configure));
        if (o.Authentication == DlmsAuthentication.HighGmac && o.Security is null) throw new ArgumentException("HLS needs a system title and keys.", nameof(configure));
        return new DlmsClient(o);
    }

    /// <summary>The options.</summary>
    public DlmsClientOptions Options => _options;

    /// <summary>Conformance negotiated in the AARE.</summary>
    public DlmsConformance Conformance { get; private set; }

    /// <summary>Largest APDU the meter accepts.</summary>
    public ushort ServerMaxPduSize { get; private set; }

    /// <summary>The meter's system title (HLS).</summary>
    public byte[]? ServerSystemTitle { get; private set; }

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_link is not null) return;
        SetState(EndpointState.Connecting);
        var transport = _options.TransportFactory!();
        try
        {
            await transport.OpenAsync(ct).ConfigureAwait(false);
            _transport = transport;
            _link = _options.Framing == DlmsFraming.Hdlc
                ? new HdlcLink(transport.Pipe, TapLink, client: true, new HdlcAddress(_options.ClientAddress), new HdlcAddress(_options.ServerLogicalAddress, _options.ServerPhysicalAddress), _options.Hdlc)
                : new WrapperLink(transport.Pipe, TapLink, _options.ClientAddress, _options.ServerLogicalAddress);
            using var timeout = Timeout(ct);
            await _link.OpenAsync(timeout.Token).ConfigureAwait(false);
            await AssociateAsync(ct).ConfigureAwait(false);
            SetState(EndpointState.Connected);
        }
        catch (Exception ex)
        {
            await CloseTransportAsync().ConfigureAwait(false);
            SetState(EndpointState.Disconnected, ex);
            if (ex is OperationCanceledException && !ct.IsCancellationRequested) throw new IoTComTimeoutException("The meter did not answer.");
            throw;
        }
    }

    private async Task AssociateAsync(CancellationToken ct)
    {
        byte[]? ctos = null;
        var initiate = DlmsApdu.InitiateRequest(DlmsConformance.DefaultClient, _options.MaxPduSize);
        var aarq = new AarqPdu { Authentication = _options.Authentication };
        if (_options.Authentication == DlmsAuthentication.Low)
        {
            aarq = aarq with { AuthenticationValue = Encoding.ASCII.GetBytes(_options.Password!) };
        }
        else if (_options.Authentication == DlmsAuthentication.HighGmac)
        {
            var keys = _options.Security!;
            _security = new DlmsSecurity(keys.SystemTitle, keys.BlockCipherKey, keys.AuthenticationKey) { InvocationCounter = 1 };
            ctos = RandomNumberGenerator.GetBytes(16);
            aarq = aarq with
            {
                Ciphered = true,
                SystemTitle = keys.SystemTitle,
                AuthenticationValue = ctos,
                CipheredInitiate = _security.Protect(0x21, initiate),
            };
        }

        var reply = await ExchangeRawAsync(DlmsApdu.Encode(aarq), ct).ConfigureAwait(false);
        if (reply is not AarePdu aare) throw new ProtocolException($"Expected AARE, got {DlmsApdu.Describe(reply)}.");
        if (!aare.Accepted)
            throw new DlmsException(aare.Diagnostic switch
            {
                13 => "The meter rejected the association: authentication failure (wrong password or keys).",
                14 => "The meter rejected the association: authentication required (use the management client).",
                _ => $"The meter rejected the association (diagnostic {aare.Diagnostic}).",
            });
        ServerSystemTitle = aare.SystemTitle;
        if (_security is not null)
        {
            _security.PeerSystemTitle = aare.SystemTitle ?? throw new ProtocolException("HLS: the AARE carries no server system title.");
            var inner = aare.CipheredInitiate is { } ci ? _security.Unprotect((CipheredPdu)DlmsApdu.Decode(ci)) : null;
            if (inner is not null) (Conformance, ServerMaxPduSize) = DlmsApdu.ParseInitiate(inner, request: false);
        }
        else
        {
            (Conformance, ServerMaxPduSize) = (aare.Conformance, aare.MaxPduSize);
        }

        if (_options.Authentication == DlmsAuthentication.HighGmac)
        {
            // HLS step 2: prove we hold the keys (f(StoC)) and check the meter's proof (f(CtoS)).
            var stoc = aare.AuthenticationValue ?? throw new ProtocolException("HLS: the AARE carries no challenge.");
            var proof = DlmsSecurity.Gmac(_security!.SystemTitle, _security.BlockCipherKey, _security.AuthenticationKey, _security.InvocationCounter++, stoc);
            var answer = await ExchangeAsync(new ActionRequestPdu(new CosemReference(CosemClass.AssociationLn, ObisCode.Parse("0.0.40.0.0.255"), 1), CosemData.OctetString(proof)), ct).ConfigureAwait(false);
            if (answer is not ActionResponsePdu { Result: ActionResult.Success, ReturnValue: { } meterProof })
                throw new DlmsException("HLS authentication failed: the meter did not accept our response.");
            if (!_security.VerifyGmac(_security.PeerSystemTitle!, meterProof.AsBytes(), ctos))
                throw new DlmsException("HLS authentication failed: the meter's response is not valid (wrong keys or an impostor).");
        }
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        if (_link is null) return;
        SetState(EndpointState.Stopping);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await _link.SendAsync(DlmsApdu.Encode(new ReleasePdu(false)), timeout.Token).ConfigureAwait(false);
                await _link.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                await _link.CloseAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or TransportException or IOException or ObjectDisposedException)
        {
        }

        await CloseTransportAsync().ConfigureAwait(false);
        SetState(EndpointState.Disconnected);
    }

    private async Task CloseTransportAsync()
    {
        var t = Interlocked.Exchange(ref _transport, null);
        _link = null;
        _security = null;
        if (t is not null) await t.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private void TapLink(FrameDirection direction, ReadOnlySpan<byte> frame, string summary) => Tap(direction, frame, () => summary);

    private CancellationTokenSource Timeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.ResponseTimeout);
        return cts;
    }

    private byte NextInvoke() => (byte)(0xC0 | (Interlocked.Increment(ref _invoke) & 0x0F));

    private async Task<DlmsPdu> ExchangeRawAsync(byte[] apdu, CancellationToken ct)
    {
        var link = _link ?? throw new TransportException("Not connected.");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var timeout = Timeout(ct);
            await link.SendAsync(apdu, timeout.Token).ConfigureAwait(false);
            var reply = await link.ReceiveAsync(timeout.Token).ConfigureAwait(false) ?? throw new TransportException("The meter closed the link.");
            return DlmsApdu.TryDecode(reply, out var pdu, out var error) ? pdu! : throw new ProtocolException($"Invalid APDU from the meter: {error}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IoTComTimeoutException($"The meter did not answer within {_options.ResponseTimeout.TotalSeconds:0.#} s.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<DlmsPdu> ExchangeAsync(DlmsPdu request, CancellationToken ct)
    {
        var plain = DlmsApdu.Encode(request);
        if (_security is null) return await ExchangeRawAsync(plain, ct).ConfigureAwait(false);
        var reply = await ExchangeRawAsync(_security.Protect(DlmsSecurity.GloTag(plain[0]), plain), ct).ConfigureAwait(false);
        if (reply is not CipheredPdu c) return reply;
        try
        {
            return DlmsApdu.Decode(_security.Unprotect(c));
        }
        catch (CryptographicException ex)
        {
            throw new ProtocolException("A ciphered response failed authentication.", ex);
        }
    }

    private void EnsureConnected()
    {
        ThrowIfDisposed();
        if (_link is null) throw new TransportException("Not connected (call ConnectAsync).");
    }

    /// <summary>Reads an attribute (GET), following block transfer.</summary>
    /// <exception cref="DlmsException">The meter answered with a Data-Access-Result.</exception>
    public async Task<CosemData> GetAsync(ushort classId, ObisCode logicalName, sbyte attribute, CosemAccess? access = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var reference = new CosemReference(classId, logicalName, attribute);
        var invoke = NextInvoke();
        var reply = await ExchangeAsync(new GetRequestPdu(reference, access) { InvokeId = invoke }, ct).ConfigureAwait(false);
        var blocks = new List<byte>();
        while (true)
        {
            if (reply is not GetResponsePdu r) throw new ProtocolException($"Expected GET.response, got {DlmsApdu.Describe(reply)}.");
            if (!r.IsBlock)
                return r.Data ?? throw new DlmsException($"GET {reference}: {r.Result}", r.Result);
            if (r.Block is null) throw new DlmsException($"GET {reference} (block {r.BlockNumber}): {r.Result}", r.Result);
            blocks.AddRange(r.Block);
            if (r.LastBlock) return CosemData.Decode(blocks.ToArray());
            reply = await ExchangeAsync(new GetNextPdu(r.BlockNumber) { InvokeId = invoke }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Writes an attribute (SET). Requires <see cref="DlmsClientOptions.ReadOnly"/> = false.</summary>
    public async Task SetAsync(ushort classId, ObisCode logicalName, sbyte attribute, CosemData value, CancellationToken ct = default)
    {
        EnsureConnected();
        if (_options.ReadOnly) throw new ReadOnlyModeException($"SET {logicalName} is blocked: the DLMS client is read-only.");
        var reply = await ExchangeAsync(new SetRequestPdu(new CosemReference(classId, logicalName, attribute), value) { InvokeId = NextInvoke() }, ct).ConfigureAwait(false);
        if (reply is not SetResponsePdu r) throw new ProtocolException($"Expected SET.response, got {DlmsApdu.Describe(reply)}.");
        if (r.Result != DataAccessResult.Success) throw new DlmsException($"SET {logicalName} #{attribute}: {r.Result}", r.Result);
    }

    /// <summary>Invokes a method (ACTION). Requires <see cref="DlmsClientOptions.ReadOnly"/> = false.</summary>
    public async Task<CosemData?> ActionAsync(ushort classId, ObisCode logicalName, sbyte method, CosemData? parameter = null, CancellationToken ct = default)
    {
        EnsureConnected();
        if (_options.ReadOnly) throw new ReadOnlyModeException($"ACTION {logicalName} is blocked: the DLMS client is read-only.");
        var reply = await ExchangeAsync(new ActionRequestPdu(new CosemReference(classId, logicalName, method), parameter ?? CosemData.Int8(0)) { InvokeId = NextInvoke() }, ct).ConfigureAwait(false);
        if (reply is not ActionResponsePdu r) throw new ProtocolException($"Expected ACTION.response, got {DlmsApdu.Describe(reply)}.");
        if (r.Result != ActionResult.Success) throw new DlmsException($"ACTION {logicalName} #{method}: {r.Result}");
        return r.ReturnValue;
    }

    /// <summary>Reads a register (class 3): value and scaler_unit.</summary>
    public async Task<RegisterValue> ReadRegisterAsync(ObisCode logicalName, CancellationToken ct = default)
    {
        var raw = await GetAsync(CosemClass.Register, logicalName, 2, ct: ct).ConfigureAwait(false);
        var su = await GetAsync(CosemClass.Register, logicalName, 3, ct: ct).ConfigureAwait(false);
        var scaler = (sbyte)su[0].AsInt64();
        return new RegisterValue(raw.AsDouble() * Math.Pow(10, scaler), (byte)su[1].AsInt64(), raw, scaler);
    }

    /// <summary>Reads the meter clock.</summary>
    public async Task<DateTimeOffset> ReadClockAsync(CancellationToken ct = default)
    {
        var v = await GetAsync(CosemClass.Clock, ObisCode.Parse("0.0.1.0.0.255"), 2, ct: ct).ConfigureAwait(false);
        return v.AsDateTime() ?? throw new ProtocolException($"The clock value {v} is not a valid date-time.");
    }

    /// <summary>Reads the object list of the current association.</summary>
    public async Task<IReadOnlyList<CosemObjectInfo>> ReadObjectListAsync(CancellationToken ct = default)
    {
        var list = await GetAsync(CosemClass.AssociationLn, ObisCode.Parse("0.0.40.0.0.255"), 2, ct: ct).ConfigureAwait(false);
        return [.. list.Items.Select(i => new CosemObjectInfo((ushort)i[0].AsInt64(), (byte)i[1].AsInt64(), i[2].AsObis()))];
    }

    /// <summary>Reads a profile (class 7), optionally the rows between <paramref name="from"/> and <paramref name="to"/>.</summary>
    public async Task<ProfileTable> ReadProfileAsync(ObisCode logicalName, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default)
    {
        var columnsData = await GetAsync(CosemClass.ProfileGeneric, logicalName, 3, ct: ct).ConfigureAwait(false);
        var columns = columnsData.Items.Select(c => new CaptureObject((ushort)c[0].AsInt64(), c[1].AsObis(), (sbyte)c[2].AsInt64())).ToList();
        CosemAccess? access = null;
        if (from is not null || to is not null)
        {
            var clock = columns.FirstOrDefault() ?? new CaptureObject(CosemClass.Clock, ObisCode.Parse("0.0.1.0.0.255"));
            access = new CosemAccess(1, CosemData.Structure(
                CosemData.Structure(CosemData.UInt16(clock.ClassId), CosemData.OctetString(clock.LogicalName.ToBytes()), CosemData.Int8(clock.Attribute), CosemData.UInt16(0)),
                CosemData.DateTime(from ?? DateTimeOffset.MinValue.AddYears(2000)),
                CosemData.DateTime(to ?? new DateTimeOffset(2099, 12, 31, 23, 59, 59, TimeSpan.Zero)),
                CosemData.Array()));
        }

        var buffer = await GetAsync(CosemClass.ProfileGeneric, logicalName, 2, access, ct).ConfigureAwait(false);
        return new ProfileTable(columns, [.. buffer.Items.Select(r => r.Items.ToArray())]);
    }
}
