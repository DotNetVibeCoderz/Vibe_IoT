using System.Collections.Concurrent;
using System.Net;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Matter;

/// <summary>Options for <see cref="MatterController"/>.</summary>
public sealed class MatterControllerOptions
{
    internal Func<IDatagramTransport>? TransportFactory { get; private set; }

    /// <summary>Uses UDP (an ephemeral local port by default).</summary>
    public MatterControllerOptions UseUdp(int port = 0, IPAddress? address = null)
    {
        TransportFactory = () => new UdpDatagramTransport(new IPEndPoint(address ?? IPAddress.Any, port));
        return this;
    }

    /// <summary>Uses an in-memory datagram network.</summary>
    public MatterControllerOptions UseInMemory(InMemoryDatagramNetwork network, IPEndPoint? address = null)
    {
        TransportFactory = () => network.Bind(address);
        return this;
    }

    /// <summary>Fabric id (default 1).</summary>
    public ulong FabricId { get; set; } = 1;

    /// <summary>The controller's own node id (default 112233, as chip-tool uses).</summary>
    public ulong NodeId { get; set; } = 112233;

    /// <summary>Admin vendor id.</summary>
    public ushort VendorId { get; set; } = 0xFFF1;

    /// <summary>Trusted PAA certificates (DER) for device attestation.</summary>
    public IList<byte[]> TrustedPaas { get; } = [];

    /// <summary>
    /// File that keeps the fabric (root key, root certificate, the controller's NOC and key, IPK) between runs. Created on
    /// first use. It holds secrets: keep it out of source control and readable only by the user.
    /// </summary>
    public string? FabricFile { get; set; }

    /// <summary>Skip device attestation (accepts any device; for development only).</summary>
    public bool SkipAttestation { get; set; }

    /// <summary>Read-only (default): no commissioning, writes or commands.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>MRP timing.</summary>
    public MatterReliability Reliability { get; } = new();

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Allows commissioning, writes and commands.</summary>
    public MatterControllerOptions AllowWrites()
    {
        ReadOnly = false;
        return this;
    }

    /// <summary>Trusts IoTCom's test PAA (devices created by <see cref="MatterDevice"/> with the default attestation).</summary>
    public MatterControllerOptions TrustTestDevices()
    {
        TrustedPaas.Add(MatterAttestation.TestPaa);
        return this;
    }
}

/// <summary>
/// A Matter controller (commissioner and administrator): owns a fabric (root CA, its own NOC, IPK), commissions devices
/// over PASE — fail-safe, device attestation, CSR, root and NOC — then talks to them over CASE: read, write, invoke and
/// subscribe. <b>Read-only by default</b>; commissioning, writes and commands need <see cref="MatterControllerOptions.AllowWrites"/>.
/// </summary>
public sealed class MatterController : EndpointBase, IClientEndpoint
{
    private readonly MatterControllerOptions _options;
    private readonly byte[] _rootKey;
    private readonly MatterFabric _fabric;
    private readonly ConcurrentDictionary<uint, Action<AttributeReport>> _subscriptions = new();
    private MatterTransport? _transport;

    private MatterController(MatterControllerOptions options) : base("matter", options.Logger)
    {
        _options = options;
        if (options.FabricFile is { } file && File.Exists(file))
        {
            (_rootKey, _fabric) = LoadFabric(File.ReadAllBytes(file));
            return;
        }

        (_rootKey, _) = MatterCrypto.GenerateKeyPair();
        var root = MatterCertificate.CreateRoot(_rootKey, rcacId: 1, fabricId: options.FabricId);
        var (opKey, opPub) = MatterCrypto.GenerateKeyPair();
        _fabric = new MatterFabric
        {
            Index = 1, FabricId = options.FabricId, NodeId = options.NodeId, Root = root,
            Noc = MatterCertificate.CreateNoc(opPub, options.NodeId, options.FabricId, root, _rootKey), OperationalKey = opKey,
            EpochKey = MatterCrypto.RandomBytes(16), CaseAdminSubject = options.NodeId, VendorId = options.VendorId,
        };
        if (options.FabricFile is { } path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(path, ExportFabric());
        }
    }

    /// <summary>The fabric as JSON (contains private keys).</summary>
    public byte[] ExportFabric()
    {
        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("note", "IoTCom.Net Matter fabric. Contains private keys: keep it secret.");
            w.WriteNumber("fabricId", _fabric.FabricId);
            w.WriteNumber("nodeId", _fabric.NodeId);
            w.WriteNumber("vendorId", _fabric.VendorId);
            w.WriteString("rootKey", Convert.ToBase64String(_rootKey));
            w.WriteString("root", Convert.ToBase64String(_fabric.Root.EncodeTlv()));
            w.WriteString("noc", Convert.ToBase64String(_fabric.Noc.EncodeTlv()));
            w.WriteString("operationalKey", Convert.ToBase64String(_fabric.OperationalKey));
            w.WriteString("epochKey", Convert.ToBase64String(_fabric.EpochKey));
            w.WriteEndObject();
        }

        return ms.ToArray();
    }

    private static (byte[] RootKey, MatterFabric Fabric) LoadFabric(byte[] json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var r = doc.RootElement;
        byte[] B(string name) => Convert.FromBase64String(r.GetProperty(name).GetString()!);
        var nodeId = r.GetProperty("nodeId").GetUInt64();
        return (B("rootKey"), new MatterFabric
        {
            Index = 1, FabricId = r.GetProperty("fabricId").GetUInt64(), NodeId = nodeId, Root = MatterCertificate.Parse(B("root")), Noc = MatterCertificate.Parse(B("noc")),
            OperationalKey = B("operationalKey"), EpochKey = B("epochKey"), CaseAdminSubject = nodeId, VendorId = r.GetProperty("vendorId").GetUInt16(),
        });
    }

    /// <summary>Creates a controller with a new fabric.</summary>
    public static MatterController Create(Action<MatterControllerOptions> configure)
    {
        var o = new MatterControllerOptions();
        configure(o);
        if (o.TransportFactory is null) throw new ArgumentException("Configure UseUdp(...) or UseInMemory(...).", nameof(configure));
        return new MatterController(o) { Name = o.Name };
    }

    /// <summary>Fabric id.</summary>
    public ulong FabricId => _fabric.FabricId;

    /// <summary>Compressed fabric id (hex), as used in operational DNS-SD names.</summary>
    public string CompressedFabricId => Convert.ToHexString(MatterCertificate.CompressedFabricId(_fabric.Root.PublicKey, _fabric.FabricId));

    /// <summary>The fabric's root certificate.</summary>
    public MatterCertificate RootCertificate => _fabric.Root;

    /// <summary>True when commissioning, writes and commands are allowed.</summary>
    public bool WritesAllowed => !_options.ReadOnly;

    /// <inheritdoc />
    public ValueTask ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_transport is not null) return ValueTask.CompletedTask;
        _transport = new MatterTransport(_options.TransportFactory!(), _options.Reliability, Logger, OnUnsolicitedAsync, (b, d, s) => Tap(d, b.Span, () => s));
        SetState(EndpointState.Connected);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        if (_transport is not null) await _transport.DisposeAsync().ConfigureAwait(false);
        _transport = null;
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override ValueTask DisposeAsyncCore() => DisconnectAsync();

    private MatterTransport T => _transport ?? throw new InvalidOperationException("Call ConnectAsync first.");

    internal void RequireWrites(string what)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException($"{what} changes the device; this controller is read-only until AllowWrites().");
    }

    // ---- request helper

    internal async Task<MatterMessage> RequestAsync(SecureSession? session, EndPoint peer, ushort protocol, byte opcode, byte[] payload, CancellationToken ct)
    {
        var ex = T.NewExchange(session, peer);
        try
        {
            await T.SendAsync(ex, protocol, opcode, payload, ct: ct).ConfigureAwait(false);
            return await T.ReceiveAsync(ex, ct).ConfigureAwait(false);
        }
        finally
        {
            T.Close(ex);
        }
    }

    internal async Task<(TlvElement? Fields, MatterStatus Status)> InvokeOnAsync(SecureSession session, CommandPath path, TlvElement? fields, CancellationToken ct)
    {
        var reply = await RequestAsync(session, session.Peer, MatterProtocol.InteractionModel, (byte)InteractionOpcode.InvokeRequest, InteractionModel.InvokeRequest(path, fields), ct).ConfigureAwait(false);
        if (reply.Opcode == (byte)InteractionOpcode.StatusResponse) return (null, InteractionModel.DecodeStatusResponse(reply.Payload));
        var (_, f, status, _) = InteractionModel.DecodeInvokeResponse(reply.Payload);
        return (f, status);
    }

    internal async Task<IReadOnlyList<AttributeReport>> ReadOnAsync(SecureSession session, IEnumerable<AttributePath> paths, CancellationToken ct)
    {
        var reply = await RequestAsync(session, session.Peer, MatterProtocol.InteractionModel, (byte)InteractionOpcode.ReadRequest, InteractionModel.ReadRequest(paths), ct).ConfigureAwait(false);
        if (reply.Opcode == (byte)InteractionOpcode.StatusResponse) throw new MatterException($"Read refused: {InteractionModel.DecodeStatusResponse(reply.Payload)}.", InteractionModel.DecodeStatusResponse(reply.Payload));
        return InteractionModel.DecodeReportData(reply.Payload).Reports;
    }

    // ---- PASE

    private async Task<SecureSession> PaseAsync(EndPoint device, uint passcode, CancellationToken ct)
    {
        var t = T;
        var ex = t.NewExchange(null, device);
        try
        {
            var localSession = t.AllocateSessionId();
            var request = Pase.ParamRequest(MatterCrypto.RandomBytes(32), localSession);
            await t.SendAsync(ex, MatterProtocol.SecureChannel, (byte)SecureChannelOpcode.PbkdfParamRequest, request, ct: ct).ConfigureAwait(false);
            var responseMsg = await t.ReceiveAsync(ex, ct).ConfigureAwait(false);
            if (responseMsg.Opcode == (byte)SecureChannelOpcode.StatusReport) throw new MatterException("The device refused PASE: its commissioning window is closed.", MatterStatus.Busy);
            var response = Tlv.Decode(responseMsg.Payload);
            var peerSession = (ushort)response.Required(3).AsUInt();
            var pbkdf = response.Required(4);
            var (w0, w1) = Spake2Plus.ComputeW(passcode, pbkdf.Required(2).AsBytes(), (int)pbkdf.Required(1).AsUInt());
            var prover = new Spake2PlusProver(Spake2Plus.Context(request, responseMsg.Payload), w0, w1);
            await t.SendAsync(ex, MatterProtocol.SecureChannel, (byte)SecureChannelOpcode.Pake1, Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous, TlvElement.Bytes(TlvTag.Context(1), prover.X))), ct: ct).ConfigureAwait(false);
            var pake2 = Tlv.Decode((await t.ReceiveAsync(ex, ct).ConfigureAwait(false)).Payload);
            byte[] cA;
            try
            {
                cA = prover.Finish(pake2.Required(1).AsBytes(), pake2.Required(2).AsBytes());
            }
            catch (ProtocolException)
            {
                throw new MatterException("PASE failed: wrong setup passcode.", MatterStatus.Failure);
            }

            await t.SendAsync(ex, MatterProtocol.SecureChannel, (byte)SecureChannelOpcode.Pake3, Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous, TlvElement.Bytes(TlvTag.Context(1), cA))), ct: ct).ConfigureAwait(false);
            var status = StatusReport.Decode((await t.ReceiveAsync(ex, ct).ConfigureAwait(false)).Payload);
            if (!status.IsSuccess) throw new MatterException($"PASE failed (status {status.GeneralCode}/{status.ProtocolCode}).", MatterStatus.Failure);
            var (i2r, r2i, challenge) = Spake2Plus.SessionKeys(prover.Ke!);
            var session = new SecureSession(localSession, peerSession, i2r, r2i, challenge, device, isPase: true, 0, 0);
            t.AddSession(session);
            return session;
        }
        finally
        {
            t.Close(ex);
        }
    }

    // ---- CASE

    private async Task<SecureSession> CaseAsync(EndPoint device, ulong nodeId, CancellationToken ct)
    {
        var t = T;
        var ex = t.NewExchange(null, device);
        try
        {
            var localSession = t.AllocateSessionId();
            var (ephPriv, ephPub) = MatterCrypto.GenerateKeyPair();
            var random = MatterCrypto.RandomBytes(32);
            var sigma1 = Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous, TlvElement.Bytes(TlvTag.Context(1), random), TlvElement.UInt(TlvTag.Context(2), localSession),
                TlvElement.Bytes(TlvTag.Context(3), _fabric.DestinationId(random, nodeId)), TlvElement.Bytes(TlvTag.Context(4), ephPub)));
            await t.SendAsync(ex, MatterProtocol.SecureChannel, (byte)SecureChannelOpcode.Sigma1, sigma1, ct: ct).ConfigureAwait(false);
            var sigma2Msg = await t.ReceiveAsync(ex, ct).ConfigureAwait(false);
            if (sigma2Msg.Opcode == (byte)SecureChannelOpcode.StatusReport) throw new MatterException($"Node 0x{nodeId:X} has no matching fabric (CASE refused).", MatterStatus.UnsupportedAccess);
            var sigma2 = Tlv.Decode(sigma2Msg.Payload);
            var responderRandom = sigma2.Required(1).AsBytes();
            var peerSession = (ushort)sigma2.Required(2).AsUInt();
            var responderEph = sigma2.Required(3).AsBytes();
            var shared = MatterCrypto.Ecdh(ephPriv, responderEph);
            var ipk = _fabric.Ipk;
            var tbe2 = Tlv.Decode(MatterCrypto.Decrypt(Case.S2K(shared, ipk, responderRandom, responderEph, sigma1), Case.Sigma2Nonce, sigma2.Required(4).AsBytes(), []));
            var peerNoc = Case.VerifyPeer(tbe2.Required(1).AsBytes(), _fabric, nodeId);
            if (!MatterCrypto.Verify(peerNoc.PublicKey, Case.Tbs(peerNoc, responderEph, ephPub), tbe2.Required(3).AsBytes())) throw new MatterException("The device's Sigma2 signature does not verify.", MatterStatus.Failure);
            var tbe3 = Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous, TlvElement.Bytes(TlvTag.Context(1), _fabric.Noc.EncodeTlv()),
                TlvElement.Bytes(TlvTag.Context(3), MatterCrypto.Sign(_fabric.OperationalKey, Case.Tbs(_fabric.Noc, ephPub, responderEph)))));
            var sigma3 = Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous, TlvElement.Bytes(TlvTag.Context(1),
                MatterCrypto.Encrypt(Case.S3K(shared, ipk, sigma1, sigma2Msg.Payload), Case.Sigma3Nonce, tbe3, []))));
            await t.SendAsync(ex, MatterProtocol.SecureChannel, (byte)SecureChannelOpcode.Sigma3, sigma3, ct: ct).ConfigureAwait(false);
            var status = StatusReport.Decode((await t.ReceiveAsync(ex, ct).ConfigureAwait(false)).Payload);
            if (!status.IsSuccess) throw new MatterException($"CASE failed (status {status.GeneralCode}/{status.ProtocolCode}).", MatterStatus.Failure);
            var (i2r, r2i, challenge) = Case.SessionKeys(shared, ipk, sigma1, sigma2Msg.Payload, sigma3);
            var session = new SecureSession(localSession, peerSession, i2r, r2i, challenge, device, isPase: false, _fabric.NodeId, nodeId) { FabricIndex = _fabric.Index };
            t.AddSession(session);
            return session;
        }
        finally
        {
            t.Close(ex);
        }
    }

    // ---- commissioning

    /// <summary>Commissions the device at <paramref name="device"/> with an onboarding payload (QR or manual code).</summary>
    public Task<MatterNode> CommissionAsync(EndPoint device, MatterSetupPayload payload, ulong nodeId, CancellationToken ct = default)
        => CommissionAsync(device, payload.Passcode, nodeId, null, ct);

    /// <summary>Commissions with an onboarding payload, reporting each step.</summary>
    public Task<MatterNode> CommissionAsync(EndPoint device, MatterSetupPayload payload, ulong nodeId, IProgress<MatterCommissioningStep>? progress, CancellationToken ct = default)
        => CommissionAsync(device, payload.Passcode, nodeId, progress, ct);

    /// <summary>Commissions the device at <paramref name="device"/> and gives it <paramref name="nodeId"/> on this fabric.</summary>
    /// <exception cref="MatterException">PASE, attestation or a commissioning step failed.</exception>
    public Task<MatterNode> CommissionAsync(EndPoint device, uint passcode, ulong nodeId, CancellationToken ct = default) => CommissionAsync(device, passcode, nodeId, null, ct);

    /// <summary>Commissions the device, reporting each step to <paramref name="progress"/>.</summary>
    public async Task<MatterNode> CommissionAsync(EndPoint device, uint passcode, ulong nodeId, IProgress<MatterCommissioningStep>? progress, CancellationToken ct = default)
    {
        RequireWrites("Commissioning");
        await ConnectAsync(ct).ConfigureAwait(false);
        void Step(MatterCommissioningStep s) => progress?.Report(s);
        var pase = await PaseAsync(device, passcode, ct).ConfigureAwait(false);
        Step(MatterCommissioningStep.Pase);
        Logger.LogInformation("PASE established with {Device}", device);
        try
        {
            var (armed, status) = await InvokeOnAsync(pase, new CommandPath(0, MatterIds.GeneralCommissioning, 0),
                TlvElement.Structure(TlvTag.Anonymous, TlvElement.UInt(TlvTag.Context(0), 60), TlvElement.UInt(TlvTag.Context(1), 1)), ct).ConfigureAwait(false);
            Check(status, armed, "ArmFailSafe");
            Step(MatterCommissioningStep.ArmFailSafe);

            var vendor = (ushort)(await ReadOnAsync(pase, [new AttributePath(0, MatterIds.BasicInformation, 2)], ct).ConfigureAwait(false))[0].Value!.AsUInt();
            var (dacF, _) = await InvokeOnAsync(pase, new CommandPath(0, MatterIds.OperationalCredentials, 2), TlvElement.Structure(TlvTag.Anonymous, TlvElement.UInt(TlvTag.Context(0), 1)), ct).ConfigureAwait(false);
            var (paiF, _) = await InvokeOnAsync(pase, new CommandPath(0, MatterIds.OperationalCredentials, 2), TlvElement.Structure(TlvTag.Anonymous, TlvElement.UInt(TlvTag.Context(0), 2)), ct).ConfigureAwait(false);
            var dac = dacF!.Required(0).AsBytes();
            var pai = paiF!.Required(0).AsBytes();
            var nonce = MatterCrypto.RandomBytes(32);
            var (att, attStatus) = await InvokeOnAsync(pase, new CommandPath(0, MatterIds.OperationalCredentials, 0), TlvElement.Structure(TlvTag.Anonymous, TlvElement.Bytes(TlvTag.Context(0), nonce)), ct).ConfigureAwait(false);
            if (attStatus != MatterStatus.Success || att is null) throw new MatterException($"AttestationRequest failed: {attStatus}.", attStatus);
            var elements = att.Required(0).AsBytes();
            if (!Tlv.Decode(elements).Required(2).AsBytes().AsSpan().SequenceEqual(nonce)) throw new MatterException("Attestation: the nonce does not match.", MatterStatus.Failure);
            if (!_options.SkipAttestation)
            {
                var problem = MatterAttestation.Verify(dac, pai, _options.TrustedPaas, elements, pase.AttestationChallenge, att.Required(1).AsBytes(), vendor);
                if (problem is not null) throw new MatterException($"Device attestation failed: {problem}.", MatterStatus.Failure);
            }

            Step(MatterCommissioningStep.Attestation);
            var csrNonce = MatterCrypto.RandomBytes(32);
            var (csrF, csrStatus) = await InvokeOnAsync(pase, new CommandPath(0, MatterIds.OperationalCredentials, 4), TlvElement.Structure(TlvTag.Anonymous, TlvElement.Bytes(TlvTag.Context(0), csrNonce)), ct).ConfigureAwait(false);
            if (csrStatus != MatterStatus.Success || csrF is null) throw new MatterException($"CSRRequest failed: {csrStatus}.", csrStatus);
            var nocsr = csrF.Required(0).AsBytes();
            if (!_options.SkipAttestation && !MatterCrypto.Verify(MatterAttestation.PublicKey(dac), [.. nocsr, .. pase.AttestationChallenge], csrF.Required(1).AsBytes()))
                throw new MatterException("The CSR is not signed by the device's attestation key.", MatterStatus.Failure);
            var nocsrElements = Tlv.Decode(nocsr);
            if (!nocsrElements.Required(2).AsBytes().AsSpan().SequenceEqual(csrNonce)) throw new MatterException("The CSR nonce does not match.", MatterStatus.Failure);
            var devicePub = MatterCertificate.PublicKeyFromCsr(nocsrElements.Required(1).AsBytes());

            Step(MatterCommissioningStep.Csr);
            var (_, rootStatus) = await InvokeOnAsync(pase, new CommandPath(0, MatterIds.OperationalCredentials, 0x0B), TlvElement.Structure(TlvTag.Anonymous, TlvElement.Bytes(TlvTag.Context(0), _fabric.Root.EncodeTlv())), ct).ConfigureAwait(false);
            if (rootStatus != MatterStatus.Success) throw new MatterException($"AddTrustedRootCertificate failed: {rootStatus}.", rootStatus);
            Step(MatterCommissioningStep.AddRoot);
            var noc = MatterCertificate.CreateNoc(devicePub, nodeId, _fabric.FabricId, _fabric.Root, _rootKey);
            var (nocF, nocStatus) = await InvokeOnAsync(pase, new CommandPath(0, MatterIds.OperationalCredentials, 6), TlvElement.Structure(TlvTag.Anonymous,
                TlvElement.Bytes(TlvTag.Context(0), noc.EncodeTlv()), TlvElement.Bytes(TlvTag.Context(2), _fabric.EpochKey),
                TlvElement.UInt(TlvTag.Context(3), _fabric.NodeId), TlvElement.UInt(TlvTag.Context(4), _fabric.VendorId)), ct).ConfigureAwait(false);
            if (nocStatus != MatterStatus.Success || nocF is null || nocF.Required(0).AsUInt() != 0)
                throw new MatterException($"AddNOC failed: {(nocF is null ? nocStatus.ToString() : $"NOC status {nocF.Required(0).AsUInt()}")}.", nocStatus);

            Step(MatterCommissioningStep.AddNoc);
            var caseSession = await CaseAsync(device, nodeId, ct).ConfigureAwait(false);
            Step(MatterCommissioningStep.Case);
            var (done, doneStatus) = await InvokeOnAsync(caseSession, new CommandPath(0, MatterIds.GeneralCommissioning, 4), null, ct).ConfigureAwait(false);
            Check(doneStatus, done, "CommissioningComplete");
            Step(MatterCommissioningStep.Complete);
            Logger.LogInformation("Commissioned node 0x{Node:X} at {Device}", nodeId, device);
            return new MatterNode(this, caseSession, nodeId);
        }
        finally
        {
            T.RemoveSession(pase.LocalId);
        }
    }

    private static void Check(MatterStatus status, TlvElement? response, string step)
    {
        if (status != MatterStatus.Success) throw new MatterException($"{step} failed: {status}.", status);
        if (response?[0] is { } code && code.AsUInt() != 0) throw new MatterException($"{step} failed: error code {code.AsUInt()} {response[1]?.AsString()}.", MatterStatus.Failure);
    }

    /// <summary>Opens a CASE session to an already commissioned node.</summary>
    public async Task<MatterNode> ConnectAsync(EndPoint device, ulong nodeId, CancellationToken ct = default)
    {
        await ConnectAsync(ct).ConfigureAwait(false);
        return new MatterNode(this, await CaseAsync(device, nodeId, ct).ConfigureAwait(false), nodeId);
    }

    // ---- subscriptions

    internal async Task<MatterSubscription> SubscribeOnAsync(SecureSession session, List<AttributePath> paths, ushort min, ushort max, Action<AttributeReport> onReport, CancellationToken ct)
    {
        var ex = T.NewExchange(session, session.Peer);
        try
        {
            await T.SendAsync(ex, MatterProtocol.InteractionModel, (byte)InteractionOpcode.SubscribeRequest, InteractionModel.SubscribeRequest(paths, min, max), ct: ct).ConfigureAwait(false);
            var priming = await T.ReceiveAsync(ex, ct).ConfigureAwait(false);
            if (priming.Opcode == (byte)InteractionOpcode.StatusResponse)
            {
                var refused = InteractionModel.DecodeStatusResponse(priming.Payload);
                throw new MatterException($"Subscribe refused: {refused}.", refused);
            }

            var (id, reports, _) = InteractionModel.DecodeReportData(priming.Payload);
            if (id is not { } subscriptionId) throw new MatterException("The priming report has no subscription id.", MatterStatus.InvalidSubscription);
            Track(subscriptionId, onReport);
            foreach (var r in reports) onReport(r);
            await T.SendAsync(ex, MatterProtocol.InteractionModel, (byte)InteractionOpcode.StatusResponse, InteractionModel.StatusResponse(MatterStatus.Success), ct: ct).ConfigureAwait(false);
            var response = Tlv.Decode((await T.ReceiveAsync(ex, ct).ConfigureAwait(false)).Payload);
            return new MatterSubscription(subscriptionId, (ushort)response.Required(2).AsUInt());
        }
        finally
        {
            T.Close(ex);
        }
    }

    internal void Track(uint subscriptionId, Action<AttributeReport> onReport) => _subscriptions[subscriptionId] = onReport;

    internal void Untrack(uint subscriptionId) => _subscriptions.TryRemove(subscriptionId, out _);

    private async Task OnUnsolicitedAsync(Exchange ex, MatterMessage m)
    {
        if (m.ProtocolId != MatterProtocol.InteractionModel || m.Opcode != (byte)InteractionOpcode.ReportData || ex.Session is null) return;
        var (id, reports, _) = InteractionModel.DecodeReportData(m.Payload);
        var known = id is { } sid && _subscriptions.TryGetValue(sid, out _);
        if (known && _subscriptions.TryGetValue(id!.Value, out var h))
            foreach (var r in reports) h(r);
        await T.SendAsync(ex, MatterProtocol.InteractionModel, (byte)InteractionOpcode.StatusResponse,
            InteractionModel.StatusResponse(known ? MatterStatus.Success : MatterStatus.InvalidSubscription)).ConfigureAwait(false);
    }
}

/// <summary>Commissioning steps, in order.</summary>
public enum MatterCommissioningStep
{
    /// <summary>PASE session (SPAKE2+ with the setup passcode).</summary>
    Pase,
    /// <summary>Fail-safe armed.</summary>
    ArmFailSafe,
    /// <summary>Device attestation verified (DAC → PAI → PAA, signature).</summary>
    Attestation,
    /// <summary>CSR received and verified.</summary>
    Csr,
    /// <summary>Trusted root installed.</summary>
    AddRoot,
    /// <summary>Operational certificate installed (fabric joined).</summary>
    AddNoc,
    /// <summary>CASE session with the new operational identity.</summary>
    Case,
    /// <summary>Commissioning complete; fail-safe disarmed.</summary>
    Complete,
}

/// <summary>A Matter error with the Interaction Model status.</summary>
public sealed class MatterException(string message, MatterStatus status) : IoTComException(message)
{
    /// <summary>Status.</summary>
    public MatterStatus Status { get; } = status;
}

/// <summary>A commissioned node reached over a CASE session.</summary>
public sealed class MatterNode
{
    private readonly MatterController _controller;
    private readonly SecureSession _session;

    internal MatterNode(MatterController controller, SecureSession session, ulong nodeId) => (_controller, _session, NodeId) = (controller, session, nodeId);

    /// <summary>Node id on the controller's fabric.</summary>
    public ulong NodeId { get; }

    /// <summary>Current address.</summary>
    public EndPoint Address => _session.Peer;

    /// <summary>Reads attributes (wildcards allowed).</summary>
    public Task<IReadOnlyList<AttributeReport>> ReadAsync(IEnumerable<AttributePath> paths, CancellationToken ct = default) => _controller.ReadOnAsync(_session, paths, ct);

    /// <summary>Reads one attribute.</summary>
    /// <exception cref="MatterException">The device answered with a status.</exception>
    public async Task<TlvElement> ReadAttributeAsync(ushort endpoint, uint cluster, uint attribute, CancellationToken ct = default)
    {
        var reports = await ReadAsync([new AttributePath(endpoint, cluster, attribute)], ct).ConfigureAwait(false);
        var r = reports.Count > 0 ? reports[0] : throw new MatterException($"No report for {endpoint}/0x{cluster:X4}/0x{attribute:X4}.", MatterStatus.Failure);
        return r.Value ?? throw new MatterException($"Reading {r.Path} failed: {r.Status}.", r.Status);
    }

    /// <summary>Writes one attribute. Needs <see cref="MatterControllerOptions.AllowWrites"/>.</summary>
    public async Task<MatterStatus> WriteAttributeAsync(ushort endpoint, uint cluster, uint attribute, TlvElement value, CancellationToken ct = default)
    {
        _controller.RequireWrites($"Writing {endpoint}/0x{cluster:X4}/0x{attribute:X4}");
        var path = new AttributePath(endpoint, cluster, attribute);
        var reply = await _controller.RequestAsync(_session, _session.Peer, MatterProtocol.InteractionModel, (byte)InteractionOpcode.WriteRequest, InteractionModel.WriteRequest([(path, value)]), ct).ConfigureAwait(false);
        if (reply.Opcode == (byte)InteractionOpcode.StatusResponse) return InteractionModel.DecodeStatusResponse(reply.Payload);
        var results = InteractionModel.DecodeWriteResponse(reply.Payload);
        return results.Count > 0 ? results[0].Status : MatterStatus.Failure;
    }

    /// <summary>Invokes a command. Needs <see cref="MatterControllerOptions.AllowWrites"/>.</summary>
    public Task<(TlvElement? Fields, MatterStatus Status)> InvokeAsync(ushort endpoint, uint cluster, uint command, TlvElement? fields = null, CancellationToken ct = default)
    {
        _controller.RequireWrites($"Invoking {endpoint}/0x{cluster:X4}/cmd 0x{command:X2}");
        return _controller.InvokeOnAsync(_session, new CommandPath(endpoint, cluster, command), fields, ct);
    }

    /// <summary>Subscribes to attributes; <paramref name="onReport"/> runs for the priming report and every change.</summary>
    public Task<MatterSubscription> SubscribeAsync(IEnumerable<AttributePath> paths, ushort minInterval, ushort maxInterval, Action<AttributeReport> onReport, CancellationToken ct = default)
        => _controller.SubscribeOnAsync(_session, [.. paths], minInterval, maxInterval, onReport, ct);
}

/// <summary>An active subscription.</summary>
public sealed class MatterSubscription
{
    internal MatterSubscription(uint id, ushort maxInterval) => (Id, MaxInterval) = (id, maxInterval);

    /// <summary>Subscription id.</summary>
    public uint Id { get; }

    /// <summary>Negotiated maximum interval (s).</summary>
    public ushort MaxInterval { get; }
}
