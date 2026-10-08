using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Dlms;

/// <summary>DLMS server (meter) options.</summary>
public sealed class DlmsServerOptions : IListenerBuilder<DlmsServerOptions>
{
    /// <summary>Accepts connections (TCP listener, in-memory listener).</summary>
    public TransportListenerFactory? ListenerFactory { get; set; }

    /// <summary>A single transport (serial port / optical probe).</summary>
    public TransportFactory? TransportFactory { get; set; }

    /// <summary>HDLC or wrapper.</summary>
    public DlmsFraming Framing { get; set; } = DlmsFraming.Hdlc;

    /// <summary>HDLC upper (logical device) address; also the wrapper port of the logical device.</summary>
    public ushort LogicalAddress { get; set; } = 1;

    /// <summary>HDLC lower (physical) address.</summary>
    public ushort PhysicalAddress { get; set; } = 17;

    /// <summary>Password of the management client (SAP 1) for low-level security; null disables LLS.</summary>
    public string? Password { get; set; } = "12345678";

    /// <summary>Suite 0 keys of the meter for HLS (GMAC) and ciphering of the management client; null disables HLS.</summary>
    public DlmsSecurityKeys? Security { get; set; }

    /// <summary>Largest APDU the meter sends before switching to block transfer.</summary>
    public ushort MaxPduSize { get; set; } = 512;

    /// <summary>HDLC parameters the meter proposes.</summary>
    public HdlcParameters Hdlc { get; set; } = new();

    /// <summary>Friendly name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <inheritdoc />
    public DlmsServerOptions UseListener(TransportListenerFactory factory)
    {
        ListenerFactory = factory;
        TransportFactory = null;
        return this;
    }

    /// <inheritdoc />
    public DlmsServerOptions UseTransport(TransportFactory factory)
    {
        TransportFactory = factory;
        ListenerFactory = null;
        return this;
    }
}

/// <summary>System title and suite 0 keys (AES-128 EK and AK).</summary>
/// <param name="SystemTitle">8-byte system title (3-letter FLAG id + serial).</param>
/// <param name="BlockCipherKey">Global unicast encryption key.</param>
/// <param name="AuthenticationKey">Authentication key.</param>
public sealed record DlmsSecurityKeys(byte[] SystemTitle, byte[] BlockCipherKey, byte[] AuthenticationKey)
{
    /// <summary>Keys from hex strings.</summary>
    public static DlmsSecurityKeys FromHex(string systemTitle, string blockCipherKey, string authenticationKey) =>
        new(Convert.FromHexString(systemTitle), Convert.FromHexString(blockCipherKey), Convert.FromHexString(authenticationKey));

    /// <inheritdoc />
    public override string ToString() => $"DlmsSecurityKeys {{ SystemTitle = {Convert.ToHexString(SystemTitle)}, keys = **** }}";
}

/// <summary>A request the server handled (for logs and demos).</summary>
/// <param name="Client">Client SAP.</param>
/// <param name="Request">Description of the request.</param>
/// <param name="Response">Description of the response.</param>
public sealed record DlmsServerEvent(int Client, string Request, string Response);

/// <summary>
/// A DLMS/COSEM server (the meter side): accepts associations from a public client (SAP 16, no authentication,
/// read-only), and a management client (SAP 1) with a password (LLS) or GMAC (HLS, ciphered APDUs); serves GET with
/// block transfer and selective access, SET and ACTION on registered <see cref="CosemObject"/>s.
/// </summary>
public sealed class DlmsServer : EndpointBase, IServerEndpoint
{
    /// <summary>Public client SAP.</summary>
    public const ushort PublicClient = 16;

    /// <summary>Management client SAP.</summary>
    public const ushort ManagementClient = 1;

    private readonly DlmsServerOptions _options;
    private readonly ConcurrentDictionary<ObisCode, CosemObject> _objects = new();
    private readonly ConcurrentDictionary<Guid, ITransport> _peers = new();
    private ITransportListener? _listener;
    private CancellationTokenSource? _cts;

    private DlmsServer(DlmsServerOptions options) : base("dlms", options.Logger)
    {
        _options = options;
        Name = options.Name;
        Add(new CosemAssociationLn(ObisCode.Parse("0.0.40.0.0.255"), () => _objects.Values.OrderBy(o => o.LogicalName.ToString(), StringComparer.Ordinal)));
    }

    /// <summary>Creates a server (call <see cref="StartAsync"/>).</summary>
    public static DlmsServer Create(Action<DlmsServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new DlmsServerOptions();
        configure(o);
        if (o.ListenerFactory is null && o.TransportFactory is null) throw new ArgumentException("A listener (UseTcp/ListenInMemory) or a transport (UseSerial) is required.", nameof(configure));
        return new DlmsServer(o);
    }

    /// <summary>Raised for every request served.</summary>
    public event EventHandler<DlmsServerEvent>? RequestHandled;

    /// <summary>The options.</summary>
    public DlmsServerOptions Options => _options;

    /// <summary>Registered objects.</summary>
    public IReadOnlyCollection<CosemObject> Objects => [.. _objects.Values];

    /// <summary>Local address after start.</summary>
    public string? LocalAddress => _listener?.LocalAddress;

    /// <summary>Registers (or replaces) an object.</summary>
    public T Add<T>(T cosemObject) where T : CosemObject
    {
        ArgumentNullException.ThrowIfNull(cosemObject);
        _objects[cosemObject.LogicalName] = cosemObject;
        return cosemObject;
    }

    /// <summary>Finds an object.</summary>
    public CosemObject? Find(ObisCode logicalName) => _objects.GetValueOrDefault(logicalName);

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_cts is not null) return;
        var cts = _cts = new CancellationTokenSource();
        if (_options.ListenerFactory is { } factory)
        {
            var listener = _listener = factory();
            await listener.StartAsync(ct).ConfigureAwait(false);
            _ = Task.Run(() => AcceptLoopAsync(listener, cts.Token), CancellationToken.None);
        }
        else
        {
            var t = _options.TransportFactory!();
            await t.OpenAsync(ct).ConfigureAwait(false);
            _ = Task.Run(() => ServeAsync(t, cts.Token), CancellationToken.None);
        }

        SetState(EndpointState.Listening);
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null) return;
        SetState(EndpointState.Stopping);
        await cts.CancelAsync().ConfigureAwait(false);
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        foreach (var p in _peers.Values) await p.DisposeAsync().ConfigureAwait(false);
        _peers.Clear();
        _listener = null;
        cts.Dispose();
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(ITransportListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ITransport peer;
            try
            {
                peer = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "DLMS accept failed");
                break;
            }

            _ = Task.Run(() => ServeAsync(peer, ct), CancellationToken.None);
        }
    }

    private sealed class Session
    {
        public ushort Client;
        public bool Associated;
        public bool Authenticated;
        public bool ReadOnly = true;
        public bool Ciphered;
        public DlmsSecurity? Security;
        public byte[]? StoC;
        public byte[]? CtoS;
        public ushort MaxPdu = 0xFFFF;
        public DlmsConformance Conformance;
        public byte[]? PendingBlocks;
        public uint BlockNumber;
    }

    private async Task ServeAsync(ITransport transport, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _peers[id] = transport;
        DlmsLink link = _options.Framing == DlmsFraming.Hdlc
            ? new HdlcLink(transport.Pipe, TapLink, client: false, new HdlcAddress(_options.LogicalAddress, _options.PhysicalAddress), new HdlcAddress(PublicClient), _options.Hdlc)
            : new WrapperLink(transport.Pipe, TapLink, _options.LogicalAddress, PublicClient, client: false);
        var session = new Session();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var apdu = await link.ReceiveAsync(ct).ConfigureAwait(false);
                if (apdu is null)
                {
                    if (link is HdlcLink) continue; // DISC: the client may reconnect with SNRM on the same port
                    break;
                }

                if (link is HdlcLink h && h.PeerSeen is { } peer) session.Client = peer.Upper;
                else if (link is WrapperLink w) session.Client = w.PeerPort;
                var response = Handle(session, apdu, link);
                if (response is not null) await link.SendAsync(response, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is TransportException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            Logger.LogDebug(ex, "DLMS session ended");
        }
        finally
        {
            _peers.TryRemove(id, out _);
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void TapLink(FrameDirection direction, ReadOnlySpan<byte> frame, string summary) => Tap(direction, frame, () => summary);

    private byte[]? Handle(Session s, byte[] apdu, DlmsLink link)
    {
        if (!DlmsApdu.TryDecode(apdu, out var pdu, out var error))
            return DlmsApdu.Encode(new ErrorPdu(error ?? "invalid APDU"));

        var ciphered = false;
        if (pdu is CipheredPdu c)
        {
            if (s.Security is null || !s.Associated) return DlmsApdu.Encode(new ErrorPdu("no ciphered association"));
            try
            {
                pdu = DlmsApdu.Decode(s.Security.Unprotect(c));
                ciphered = true;
            }
            catch (CryptographicException)
            {
                Report(s, "ciphered APDU", "rejected: authentication tag or counter invalid");
                return DlmsApdu.Encode(new ErrorPdu("deciphering error"));
            }
        }

        DlmsPdu reply = pdu switch
        {
            AarqPdu q => Associate(s, q, link),
            ReleasePdu { Response: false } => Release(s),
            _ when !s.Associated => new ErrorPdu("no association"),
            GetRequestPdu g => Get(s, g),
            GetNextPdu n => Next(s, n),
            SetRequestPdu set => Set(s, set),
            ActionRequestPdu a => Action(s, a),
            _ => new ErrorPdu("service not supported"),
        };
        Report(s, DlmsApdu.Describe(pdu!), DlmsApdu.Describe(reply));
        var plain = DlmsApdu.Encode(reply);
        return ciphered && s.Security is { } sec ? sec.Protect(DlmsSecurity.GloTag(plain[0]), plain) : plain;
    }

    private void Report(Session s, string request, string response) => RequestHandled?.Invoke(this, new DlmsServerEvent(s.Client, request, response));

    private AarePdu Associate(Session s, AarqPdu q, DlmsLink link)
    {
        AarePdu Reject(byte diagnostic) => new() { Result = 1, Diagnostic = diagnostic, Ciphered = q.Ciphered, Authentication = q.Authentication };
        (s.Associated, s.Authenticated, s.ReadOnly, s.Ciphered, s.Security, s.StoC) = (false, false, true, false, null, null);
        var supported = DlmsConformance.Get | DlmsConformance.Set | DlmsConformance.Action | DlmsConformance.SelectiveAccess | DlmsConformance.BlockTransferWithGet;
        byte[]? initiate = null;
        switch (q.Authentication)
        {
            case DlmsAuthentication.None:
                if (s.Client != PublicClient || q.Ciphered) return Reject(14);
                break;
            case DlmsAuthentication.Low:
                if (_options.Password is not { } password || q.AuthenticationValue is null
                    || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(password), q.AuthenticationValue))
                    return Reject(13);
                s.ReadOnly = false;
                break;
            case DlmsAuthentication.HighGmac:
                if (_options.Security is not { } keys || q.SystemTitle is not { Length: 8 } clientTitle || q.AuthenticationValue is not { Length: >= 8 } ctos)
                    return Reject(13);
                s.Security = new DlmsSecurity(keys.SystemTitle, keys.BlockCipherKey, keys.AuthenticationKey) { PeerSystemTitle = clientTitle, InvocationCounter = 1 };
                s.CtoS = ctos;
                s.StoC = RandomNumberGenerator.GetBytes(16);
                s.Ciphered = q.Ciphered;
                if (q.CipheredInitiate is { } ci)
                {
                    try
                    {
                        var inner = s.Security.Unprotect((CipheredPdu)DlmsApdu.Decode(ci));
                        var (c, m) = DlmsApdu.ParseInitiate(inner, request: true);
                        (q, initiate) = (q with { Conformance = c, MaxPduSize = m }, inner);
                    }
                    catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidCastException)
                    {
                        return Reject(13);
                    }
                }

                break;
            default:
                return Reject(11);
        }

        s.Conformance = q.Conformance & supported;
        s.MaxPdu = Math.Min(q.MaxPduSize, _options.MaxPduSize);
        s.Associated = true;
        s.Authenticated = q.Authentication != DlmsAuthentication.HighGmac;
        var response = DlmsApdu.InitiateResponse(s.Conformance, _options.MaxPduSize);
        return new AarePdu
        {
            Result = 0,
            Diagnostic = q.Authentication == DlmsAuthentication.HighGmac ? (byte)14 : (byte)0,
            Ciphered = q.Ciphered,
            Authentication = q.Authentication,
            SystemTitle = s.Security?.SystemTitle,
            AuthenticationValue = s.StoC,
            Conformance = s.Conformance,
            MaxPduSize = _options.MaxPduSize,
            CipheredInitiate = initiate is not null && s.Security is { } sec ? sec.Protect(0x28, response) : null,
        };
    }

    private static ReleasePdu Release(Session s)
    {
        (s.Associated, s.Authenticated, s.PendingBlocks) = (false, false, null);
        return new ReleasePdu(true);
    }

    private GetResponsePdu Get(Session s, GetRequestPdu g)
    {
        if (!s.Authenticated) return new GetResponsePdu { InvokeId = g.InvokeId, Result = DataAccessResult.ReadWriteDenied };
        if (Find(g.Attribute.LogicalName) is not { } obj) return new GetResponsePdu { InvokeId = g.InvokeId, Result = DataAccessResult.ObjectUndefined };
        if (obj.ClassId != g.Attribute.ClassId) return new GetResponsePdu { InvokeId = g.InvokeId, Result = DataAccessResult.ObjectClassInconsistent };
        var (value, result) = obj.Get(g.Attribute.Index, g.Access);
        if (value is null) return new GetResponsePdu { InvokeId = g.InvokeId, Result = result };
        var encoded = value.Encode();
        if (encoded.Length + 4 <= s.MaxPdu || !s.Conformance.HasFlag(DlmsConformance.BlockTransferWithGet))
            return new GetResponsePdu { InvokeId = g.InvokeId, Data = value };
        s.PendingBlocks = encoded;
        s.BlockNumber = 0;
        return NextBlock(s, g.InvokeId);
    }

    private GetResponsePdu Next(Session s, GetNextPdu n)
    {
        if (s.PendingBlocks is null) return new GetResponsePdu { InvokeId = n.InvokeId, IsBlock = true, LastBlock = true, BlockNumber = n.BlockNumber, Result = DataAccessResult.NoLongGetInProgress };
        if (n.BlockNumber != s.BlockNumber) return new GetResponsePdu { InvokeId = n.InvokeId, IsBlock = true, LastBlock = true, BlockNumber = n.BlockNumber, Result = DataAccessResult.DataBlockNumberInvalid };
        return NextBlock(s, n.InvokeId);
    }

    private static GetResponsePdu NextBlock(Session s, byte invokeId)
    {
        var size = Math.Max(16, s.MaxPdu - 16);
        var offset = (int)s.BlockNumber * size;
        var data = s.PendingBlocks!;
        var length = Math.Min(size, data.Length - offset);
        var last = offset + length >= data.Length;
        s.BlockNumber++;
        var block = data[offset..(offset + length)];
        if (last) s.PendingBlocks = null;
        return new GetResponsePdu { InvokeId = invokeId, IsBlock = true, LastBlock = last, BlockNumber = s.BlockNumber, Block = block };
    }

    private SetResponsePdu Set(Session s, SetRequestPdu set)
    {
        if (!s.Authenticated || s.ReadOnly) return new SetResponsePdu(DataAccessResult.ReadWriteDenied) { InvokeId = set.InvokeId };
        if (Find(set.Attribute.LogicalName) is not { } obj) return new SetResponsePdu(DataAccessResult.ObjectUndefined) { InvokeId = set.InvokeId };
        return new SetResponsePdu(obj.Set(set.Attribute.Index, set.Value)) { InvokeId = set.InvokeId };
    }

    private ActionResponsePdu Action(Session s, ActionRequestPdu a)
    {
        // HLS step 2: the client proves it holds the keys by answering our challenge; we answer theirs.
        if (a.Method.ClassId == CosemClass.AssociationLn && a.Method.Index == 1 && s.Security is { } sec && s.StoC is { } stoc)
        {
            if (a.Parameter?.Type != CosemDataType.OctetString || !sec.VerifyGmac(sec.PeerSystemTitle!, a.Parameter.AsBytes(), stoc))
            {
                s.Associated = false;
                return new ActionResponsePdu(ActionResult.ReadWriteDenied) { InvokeId = a.InvokeId };
            }

            s.Authenticated = true;
            s.ReadOnly = false;
            var proof = DlmsSecurity.Gmac(sec.SystemTitle, sec.BlockCipherKey, sec.AuthenticationKey, sec.InvocationCounter++, s.CtoS);
            return new ActionResponsePdu(ActionResult.Success, CosemData.OctetString(proof)) { InvokeId = a.InvokeId };
        }

        if (!s.Authenticated || s.ReadOnly) return new ActionResponsePdu(ActionResult.ReadWriteDenied) { InvokeId = a.InvokeId };
        if (Find(a.Method.LogicalName) is not { } obj) return new ActionResponsePdu(ActionResult.ObjectUndefined) { InvokeId = a.InvokeId };
        var (result, ret) = obj.Invoke(a.Method.Index, a.Parameter);
        return new ActionResponsePdu(result, ret) { InvokeId = a.InvokeId };
    }
}
