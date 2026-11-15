using System.Collections.Concurrent;
using System.Net;
using System.Text;
using IoTCom.Net.Transports;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Matter;

/// <summary>Options for <see cref="MatterDevice"/>.</summary>
public sealed class MatterDeviceOptions
{
    internal Func<IDatagramTransport>? TransportFactory { get; private set; }

    /// <summary>Listens on UDP (default port 5540).</summary>
    public MatterDeviceOptions UseUdp(int port = 5540, IPAddress? address = null)
    {
        TransportFactory = () => new UdpDatagramTransport(new IPEndPoint(address ?? IPAddress.Any, port));
        return this;
    }

    /// <summary>Uses an in-memory datagram network.</summary>
    public MatterDeviceOptions UseInMemory(InMemoryDatagramNetwork network, IPEndPoint address)
    {
        TransportFactory = () => network.Bind(address);
        return this;
    }

    /// <summary>Vendor id (default 0xFFF1, a test vendor).</summary>
    public ushort VendorId { get; set; } = 0xFFF1;

    /// <summary>Product id.</summary>
    public ushort ProductId { get; set; } = 0x8000;

    /// <summary>Vendor name.</summary>
    public string VendorName { get; set; } = "IoTCom";

    /// <summary>Product name.</summary>
    public string ProductName { get; set; } = "IoTCom Matter device";

    /// <summary>Serial number.</summary>
    public string SerialNumber { get; set; } = "IOTCOM-0001";

    /// <summary>12-bit discriminator.</summary>
    public ushort Discriminator { get; set; } = 3840;

    /// <summary>Setup passcode.</summary>
    public uint Passcode { get; set; } = 20202021;

    /// <summary>PBKDF iterations.</summary>
    public int Iterations { get; set; } = 1000;

    /// <summary>Attestation chain (default: a test chain under <see cref="MatterAttestation.TestPaa"/>).</summary>
    public MatterAttestationChain? Attestation { get; set; }

    /// <summary>MRP timing.</summary>
    public MatterReliability Reliability { get; } = new();

    /// <summary>Fail-safe expiry when a commissioner does not ask for less (seconds).</summary>
    public int FailSafeSeconds { get; set; } = 60;

    /// <summary>Endpoint name.</summary>
    public string? Name { get; set; }

    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }

    /// <summary>Advertises over DNS-SD with this (started) responder: commissionable while the window is open, operational per fabric.</summary>
    public IoTCom.Net.Protocols.Mdns.MdnsResponder? Mdns { get; set; }

    /// <summary>Addresses to advertise (default: the bound address when it is specific).</summary>
    public IList<IPAddress> AdvertisedAddresses { get; } = [];
}

/// <summary>A fabric the device belongs to, as other code may inspect it.</summary>
/// <param name="Index">Fabric index.</param>
/// <param name="FabricId">Fabric id.</param>
/// <param name="NodeId">The device's node id on that fabric.</param>
/// <param name="VendorId">Admin vendor id.</param>
/// <param name="Label">Label.</param>
public sealed record MatterFabricInfo(byte Index, ulong FabricId, ulong NodeId, ushort VendorId, string Label);

/// <summary>
/// A Matter device (commissionee and operational node): endpoint 0 with Descriptor, Basic Information, General
/// Commissioning, Operational Credentials and Access Control, application endpoints you add, PASE and CASE responders, the
/// Interaction Model server with subscriptions, and a fabric table. Commissionable while it has no fabric.
/// </summary>
public sealed class MatterDevice : EndpointBase, IServerEndpoint
{
    private readonly MatterDeviceOptions _options;
    private readonly byte[] _salt = MatterCrypto.RandomBytes(32);
    private readonly byte[] _verifier;
    private readonly MatterAttestationChain _attestation;
    private readonly List<MatterEndpoint> _endpoints = [];
    private readonly List<MatterFabric> _fabrics = [];
    private readonly ConcurrentDictionary<uint, Subscription> _subscriptions = new();
    private MatterTransport? _transport;
    private string _nodeLabel = "";

    // commissioning state
    private CancellationTokenSource? _failSafe;
    private byte[]? _pendingOpKey;
    private MatterCertificate? _pendingRoot;
    private byte? _pendingFabric;

    private sealed class Subscription
    {
        public required uint Id { get; init; }
        public required SecureSession Session { get; init; }
        public required IReadOnlyList<AttributePath> Paths { get; init; }
        public required ushort MinInterval { get; init; }
        public required ushort MaxInterval { get; init; }
        public readonly HashSet<(ushort, uint, uint)> Dirty = [];
        public DateTime LastReport = DateTime.UtcNow;
        public CancellationTokenSource Stop { get; } = new();
    }

    private MatterDevice(MatterDeviceOptions options) : base("matter", options.Logger)
    {
        _options = options;
        _verifier = Spake2Plus.ComputeVerifier(options.Passcode, _salt, options.Iterations);
        _attestation = options.Attestation ?? MatterAttestation.CreateTestChain(options.VendorId, options.ProductId);
        _endpoints.Add(CreateRootEndpoint());
    }

    /// <summary>Creates a device.</summary>
    public static MatterDevice Create(Action<MatterDeviceOptions> configure)
    {
        var o = new MatterDeviceOptions();
        configure(o);
        if (o.TransportFactory is null) throw new ArgumentException("Configure UseUdp(...) or UseInMemory(...).", nameof(configure));
        if (!MatterSetupPayload.IsValidPasscode(o.Passcode)) throw new ArgumentException("The passcode is not allowed by the Matter specification.", nameof(configure));
        return new MatterDevice(o) { Name = o.Name };
    }

    /// <summary>Onboarding payload for QR and manual codes (discovery: on the IP network).</summary>
    public MatterSetupPayload SetupPayload => new(_options.VendorId, _options.ProductId, CommissioningFlow.Standard, DiscoveryCapabilities.OnNetwork, _options.Discriminator, _options.Passcode);

    /// <summary>Endpoints.</summary>
    public IReadOnlyList<MatterEndpoint> Endpoints => _endpoints;

    /// <summary>Fabrics.</summary>
    public IReadOnlyList<MatterFabricInfo> Fabrics
    {
        get
        {
            lock (_fabrics) return [.. _fabrics.Select(f => new MatterFabricInfo(f.Index, f.FabricId, f.NodeId, f.VendorId, f.Label))];
        }
    }

    /// <summary>True when the commissioning window is open (no fabric yet, or opened explicitly).</summary>
    public bool CommissioningWindowOpen { get; private set; } = true;

    /// <summary>Raised when commissioning completes (fabric index).</summary>
    public event Action<MatterFabricInfo>? Commissioned;

    /// <summary>Node label (Basic Information).</summary>
    public string NodeLabel => _nodeLabel;

    /// <summary>The local endpoint (address to commission).</summary>
    public EndPoint? Address => _transport?.LocalEndPoint;

    /// <summary>Adds an application endpoint.</summary>
    public MatterEndpoint AddEndpoint(MatterEndpoint endpoint)
    {
        if (_endpoints.Any(e => e.Id == endpoint.Id)) throw new ArgumentException($"Endpoint {endpoint.Id} exists.", nameof(endpoint));
        void Hook(MatterCluster c) => c.Changed += (cluster, attribute) => OnChanged(endpoint.Id, cluster.Id, attribute);
        foreach (var c in endpoint.Clusters) Hook(c);
        endpoint.ClusterAdded += Hook;
        endpoint.Add(DescriptorCluster(endpoint));
        lock (_endpoints) _endpoints.Add(endpoint);
        return endpoint;
    }

    /// <summary>Opens the commissioning window (for an additional fabric).</summary>
    public void OpenCommissioningWindow() => CommissioningWindowOpen = true;

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_transport is not null) return ValueTask.CompletedTask;
        _transport = new MatterTransport(_options.TransportFactory!(), _options.Reliability, Logger, OnUnsolicitedAsync, (b, d, s) => Tap(d, b.Span, () => s));
        SetState(EndpointState.Listening);
        _ = AdvertiseAsync();
        Logger.LogInformation("Matter device {Product} on {Address}: QR {Qr}, manual code {Manual}", _options.ProductName, _transport.LocalEndPoint, SetupPayload.ToQrCode(), SetupPayload.ToManualCode());
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct = default)
    {
        foreach (var s in _subscriptions.Values) await s.Stop.CancelAsync().ConfigureAwait(false);
        _subscriptions.Clear();
        if (_transport is not null) await _transport.DisposeAsync().ConfigureAwait(false);
        _transport = null;
        SetState(EndpointState.Disconnected);
    }

    /// <inheritdoc />
    protected override ValueTask DisposeAsyncCore() => StopAsync();

    // ---- DNS-SD

    private readonly string _instance = Convert.ToHexString(MatterCrypto.RandomBytes(8));

    private async Task AdvertiseAsync()
    {
        if (_options.Mdns is not { } mdns || _transport is null) return;
        var local = _transport.LocalEndPoint as IPEndPoint;
        IReadOnlyList<IPAddress> addresses = _options.AdvertisedAddresses.Count > 0 ? [.. _options.AdvertisedAddresses]
            : local is not null && !local.Address.Equals(IPAddress.Any) ? [local.Address] : [];
        var port = (ushort)(local?.Port ?? 5540);
        try
        {
            var commissionable = MatterDiscovery.Commissionable(SetupPayload, _options.ProductName, port, addresses, _instance);
            if (CommissioningWindowOpen) await mdns.RegisterAsync(commissionable).ConfigureAwait(false);
            else await mdns.UnregisterAsync(commissionable.FullName).ConfigureAwait(false);
            List<MatterFabric> fabrics;
            lock (_fabrics) fabrics = [.. _fabrics];
            foreach (var f in fabrics)
                await mdns.RegisterAsync(MatterDiscovery.Operational(Convert.ToHexString(MatterCertificate.CompressedFabricId(f.Root.PublicKey, f.FabricId)), f.NodeId, port, addresses)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IoTComException or InvalidOperationException)
        {
            Logger.LogDebug(ex, "DNS-SD advertisement failed");
        }
    }

    // ---- root endpoint

    private MatterEndpoint CreateRootEndpoint()
    {
        var ep = new MatterEndpoint(0, (MatterIds.RootNode, 3));
        ep.Add(DescriptorCluster(ep));
        var basic = new MatterCluster(MatterIds.BasicInformation, "Basic Information", 3);
        basic.Attribute(0, () => TlvElement.UInt(TlvTag.Anonymous, 17));
        basic.Attribute(1, () => TlvElement.String(TlvTag.Anonymous, _options.VendorName));
        basic.Attribute(2, () => TlvElement.UInt(TlvTag.Anonymous, _options.VendorId));
        basic.Attribute(3, () => TlvElement.String(TlvTag.Anonymous, _options.ProductName));
        basic.Attribute(4, () => TlvElement.UInt(TlvTag.Anonymous, _options.ProductId));
        basic.Attribute(5, () => TlvElement.String(TlvTag.Anonymous, _nodeLabel), v =>
        {
            var s = v.AsString();
            if (s.Length > 32) return MatterStatus.ConstraintError;
            _nodeLabel = s;
            return MatterStatus.Success;
        });
        basic.Attribute(6, () => TlvElement.String(TlvTag.Anonymous, "XX"));
        basic.Attribute(7, () => TlvElement.UInt(TlvTag.Anonymous, 1));
        basic.Attribute(8, () => TlvElement.String(TlvTag.Anonymous, "1.0"));
        basic.Attribute(9, () => TlvElement.UInt(TlvTag.Anonymous, 1));
        basic.Attribute(0x0A, () => TlvElement.String(TlvTag.Anonymous, IoTComInfo.Version));
        basic.Attribute(0x0F, () => TlvElement.String(TlvTag.Anonymous, _options.SerialNumber));
        basic.Attribute(0x12, () => TlvElement.String(TlvTag.Anonymous, _options.SerialNumber + "-uid"));
        basic.Attribute(0x13, () => TlvElement.Structure(TlvTag.Anonymous, TlvElement.UInt(TlvTag.Context(0), 3), TlvElement.UInt(TlvTag.Context(1), 3)));
        ep.Add(basic);
        ep.Add(GeneralCommissioningCluster());
        ep.Add(OperationalCredentialsCluster());
        var acl = new MatterCluster(MatterIds.AccessControl, "Access Control", 1);
        acl.Attribute(0, ctx => TlvElement.Array(TlvTag.Anonymous, [.. FabricsFor(ctx).Select(f => TlvElement.Structure(TlvTag.Anonymous,
            TlvElement.UInt(TlvTag.Context(1), 5), TlvElement.UInt(TlvTag.Context(2), 2),
            TlvElement.Array(TlvTag.Context(3), TlvElement.UInt(TlvTag.Anonymous, f.CaseAdminSubject)), TlvElement.Null(TlvTag.Context(4)), TlvElement.UInt(TlvTag.Context(0xFE), f.Index)))]));
        ep.Add(acl);
        return ep;
    }

    private IEnumerable<MatterFabric> FabricsFor(MatterRequestContext? ctx)
    {
        lock (_fabrics) return ctx?.FabricIndex is { } i ? _fabrics.Where(f => f.Index == i).ToList() : [.. _fabrics];
    }

    private MatterCluster DescriptorCluster(MatterEndpoint ep)
    {
        var d = new MatterCluster(MatterIds.Descriptor, "Descriptor", 2);
        d.Attribute(0, () => TlvElement.Array(TlvTag.Anonymous, [.. ep.DeviceTypes.Select(t => TlvElement.Structure(TlvTag.Anonymous, TlvElement.UInt(TlvTag.Context(0), t.Type), TlvElement.UInt(TlvTag.Context(1), t.Revision)))]));
        d.Attribute(1, () => TlvElement.Array(TlvTag.Anonymous, [.. ep.Clusters.Select(c => TlvElement.UInt(TlvTag.Anonymous, c.Id)).OrderBy(x => x.AsUInt())]));
        d.Attribute(2, () => TlvElement.Array(TlvTag.Anonymous));
        d.Attribute(3, () =>
        {
            List<MatterEndpoint> all;
            lock (_endpoints) all = [.. _endpoints];
            var parts = ep.Id == 0 ? all.Where(e => e.Id != 0) : all.Where(e => e.Parent == ep.Id);
            return TlvElement.Array(TlvTag.Anonymous, [.. parts.Select(e => TlvElement.UInt(TlvTag.Anonymous, e.Id))]);
        });
        return d;
    }

    private MatterCluster GeneralCommissioningCluster()
    {
        ulong breadcrumb = 0;
        var c = new MatterCluster(MatterIds.GeneralCommissioning, "General Commissioning", 1);
        c.Attribute(0, () => TlvElement.UInt(TlvTag.Anonymous, breadcrumb), v =>
        {
            breadcrumb = v.AsUInt();
            return MatterStatus.Success;
        });
        c.Attribute(1, () => TlvElement.Structure(TlvTag.Anonymous, TlvElement.UInt(TlvTag.Context(0), (ulong)_options.FailSafeSeconds), TlvElement.UInt(TlvTag.Context(1), 900)));
        c.Attribute(2, () => TlvElement.UInt(TlvTag.Anonymous, 2));
        c.Attribute(3, () => TlvElement.UInt(TlvTag.Anonymous, 2));
        c.Attribute(4, () => TlvElement.Bool(TlvTag.Anonymous, true));
        TlvElement Response(byte code, string text = "") => TlvElement.Structure(TlvTag.Anonymous, TlvElement.UInt(TlvTag.Context(0), code), TlvElement.String(TlvTag.Context(1), text));
        c.Command(0, (f, ctx) =>
        {
            var expiry = (int)f.Required(0).AsUInt();
            breadcrumb = f[1]?.AsUInt() ?? breadcrumb;
            if (expiry == 0) DisarmFailSafe(revert: true);
            else ArmFailSafe(expiry);
            return new CommandResult(MatterStatus.Success, 1, Response(0));
        }, 1);
        c.Command(2, (f, _) => new CommandResult(MatterStatus.Success, 3, Response(0)), 3);
        c.Command(4, (f, ctx) =>
        {
            if (ctx.IsPase || ctx.FabricIndex != _pendingFabric || _failSafe is null) return new CommandResult(MatterStatus.Success, 5, Response(3, "No commissioning in progress on this fabric."));
            var fabric = FabricsFor(ctx).First();
            DisarmFailSafe(revert: false);
            CommissioningWindowOpen = false;
            Logger.LogInformation("Commissioned on fabric 0x{Fabric:X} as node 0x{Node:X}", fabric.FabricId, fabric.NodeId);
            Commissioned?.Invoke(new MatterFabricInfo(fabric.Index, fabric.FabricId, fabric.NodeId, fabric.VendorId, fabric.Label));
            _ = AdvertiseAsync();
            return new CommandResult(MatterStatus.Success, 5, Response(0));
        }, 5);
        return c;
    }

    private void ArmFailSafe(int seconds)
    {
        _failSafe?.Cancel();
        var cts = new CancellationTokenSource();
        _failSafe = cts;
        _ = Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) DisarmFailSafe(revert: true);
        }, TaskScheduler.Default);
    }

    private void DisarmFailSafe(bool revert)
    {
        _failSafe?.Cancel();
        _failSafe = null;
        if (revert && _pendingFabric is { } index)
        {
            lock (_fabrics) _fabrics.RemoveAll(f => f.Index == index);
            Logger.LogWarning("Fail-safe expired: fabric {Index} removed", index);
        }

        (_pendingOpKey, _pendingRoot, _pendingFabric) = (null, null, null);
    }

    private MatterCluster OperationalCredentialsCluster()
    {
        var c = new MatterCluster(MatterIds.OperationalCredentials, "Operational Credentials", 1);
        c.Attribute(0, ctx => TlvElement.Array(TlvTag.Anonymous, [.. FabricsFor(ctx).Select(f => TlvElement.Structure(TlvTag.Anonymous,
            TlvElement.Bytes(TlvTag.Context(1), f.Noc.EncodeTlv()), TlvElement.Null(TlvTag.Context(2)), TlvElement.UInt(TlvTag.Context(0xFE), f.Index)))]));
        c.Attribute(1, ctx => TlvElement.Array(TlvTag.Anonymous, [.. FabricsFor(ctx).Select(f => TlvElement.Structure(TlvTag.Anonymous,
            TlvElement.Bytes(TlvTag.Context(1), f.Root.PublicKey), TlvElement.UInt(TlvTag.Context(2), f.VendorId), TlvElement.UInt(TlvTag.Context(3), f.FabricId),
            TlvElement.UInt(TlvTag.Context(4), f.NodeId), TlvElement.String(TlvTag.Context(5), f.Label), TlvElement.UInt(TlvTag.Context(0xFE), f.Index)))]));
        c.Attribute(2, () => TlvElement.UInt(TlvTag.Anonymous, 5));
        c.Attribute(3, () =>
        {
            lock (_fabrics) return TlvElement.UInt(TlvTag.Anonymous, (ulong)_fabrics.Count);
        });
        c.Attribute(4, () =>
        {
            lock (_fabrics) return TlvElement.Array(TlvTag.Anonymous, [.. _fabrics.Select(f => TlvElement.Bytes(TlvTag.Anonymous, f.Root.EncodeTlv()))]);
        });
        c.Attribute(5, ctx => TlvElement.UInt(TlvTag.Anonymous, ctx?.FabricIndex ?? 0));

        c.Command(0, (f, ctx) =>   // AttestationRequest
        {
            var nonce = f.Required(0).AsBytes();
            if (nonce.Length != 32) return new CommandResult(MatterStatus.InvalidCommand);
            var elements = Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous, TlvElement.Bytes(TlvTag.Context(1), _attestation.CertificationDeclaration),
                TlvElement.Bytes(TlvTag.Context(2), nonce), TlvElement.UInt(TlvTag.Context(3), 0)));
            var sig = MatterCrypto.Sign(_attestation.DacPrivateKey, [.. elements, .. ctx.AttestationChallenge ?? []]);
            return CommandResult.Respond(1, TlvElement.Bytes(TlvTag.Context(0), elements), TlvElement.Bytes(TlvTag.Context(1), sig));
        }, 1);
        c.Command(2, (f, _) => CommandResult.Respond(3, TlvElement.Bytes(TlvTag.Context(0), f.Required(0).AsUInt() switch   // CertificateChainRequest
        {
            1 => _attestation.Dac,
            2 => _attestation.Pai,
            _ => throw new ProtocolException("Certificate type is 1 (DAC) or 2 (PAI)."),
        })), 3);
        c.Command(4, (f, ctx) =>   // CSRRequest
        {
            if (_failSafe is null) return new CommandResult(MatterStatus.FailsafeRequired);
            var nonce = f.Required(0).AsBytes();
            var (priv, _) = MatterCrypto.GenerateKeyPair();
            _pendingOpKey = priv;
            var elements = Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous, TlvElement.Bytes(TlvTag.Context(1), MatterCertificate.CreateCsr(priv)), TlvElement.Bytes(TlvTag.Context(2), nonce)));
            var sig = MatterCrypto.Sign(_attestation.DacPrivateKey, [.. elements, .. ctx.AttestationChallenge ?? []]);
            return CommandResult.Respond(5, TlvElement.Bytes(TlvTag.Context(0), elements), TlvElement.Bytes(TlvTag.Context(1), sig));
        }, 5);
        c.Command(0x0B, (f, _) =>   // AddTrustedRootCertificate
        {
            if (_failSafe is null) return new CommandResult(MatterStatus.FailsafeRequired);
            var root = MatterCertificate.Parse(f.Required(0).AsBytes());
            if (!root.IsCa || !root.VerifySignature(root.PublicKey)) return new CommandResult(MatterStatus.InvalidCommand);
            _pendingRoot = root;
            return CommandResult.Ok;
        });
        c.Command(6, (f, ctx) => AddNoc(f, ctx), 8);
        c.Command(9, (f, ctx) =>   // UpdateFabricLabel
        {
            var fabric = FabricsFor(ctx).FirstOrDefault();
            if (fabric is null) return new CommandResult(MatterStatus.UnsupportedAccess);
            fabric.Label = f.Required(0).AsString();
            return NocResponse(0, fabric.Index);
        }, 8);
        c.Command(0x0A, (f, ctx) =>   // RemoveFabric
        {
            var index = (byte)f.Required(0).AsUInt();
            int removed;
            lock (_fabrics) removed = _fabrics.RemoveAll(x => x.Index == index);
            if (removed == 0) return NocResponse(11, index);
            lock (_fabrics)
                if (_fabrics.Count == 0) CommissioningWindowOpen = true;
            return NocResponse(0, index);
        }, 8);
        return c;
    }

    private static CommandResult NocResponse(byte status, byte index) => CommandResult.Respond(8, TlvElement.UInt(TlvTag.Context(0), status), TlvElement.UInt(TlvTag.Context(1), index));

    private CommandResult AddNoc(TlvElement f, MatterRequestContext ctx)
    {
        if (_failSafe is null) return new CommandResult(MatterStatus.FailsafeRequired);
        if (_pendingOpKey is null || _pendingRoot is null) return NocResponse(2, 0);   // missing CSR or root
        MatterCertificate noc;
        try
        {
            noc = MatterCertificate.Parse(f.Required(0).AsBytes());
        }
        catch (ProtocolException)
        {
            return NocResponse(3, 0);
        }

        if (!noc.VerifySignature(_pendingRoot.PublicKey) || noc.NodeId is null || noc.FabricId is null) return NocResponse(3, 0);   // invalid NOC
        if (!noc.PublicKey.AsSpan().SequenceEqual(MatterCrypto.PublicKeyOf(_pendingOpKey))) return NocResponse(1, 0);   // invalid public key
        byte index;
        lock (_fabrics)
        {
            if (_fabrics.Any(x => x.FabricId == noc.FabricId && x.Root.PublicKey.AsSpan().SequenceEqual(_pendingRoot.PublicKey))) return NocResponse(9, 0);   // fabric conflict
            index = (byte)(_fabrics.Count == 0 ? 1 : _fabrics.Max(x => x.Index) + 1);
            _fabrics.Add(new MatterFabric
            {
                Index = index, FabricId = noc.FabricId.Value, NodeId = noc.NodeId.Value, Root = _pendingRoot, Noc = noc, OperationalKey = _pendingOpKey,
                EpochKey = f.Required(2).AsBytes(), CaseAdminSubject = f.Required(3).AsUInt(), VendorId = (ushort)f.Required(4).AsUInt(),
            });
        }

        _pendingFabric = index;
        ctx.FabricIndex = index;
        Logger.LogInformation("AddNOC: fabric 0x{Fabric:X} index {Index}, node 0x{Node:X}", noc.FabricId, index, noc.NodeId);
        return NocResponse(0, index);
    }

    // ---- unsolicited messages

    private async Task OnUnsolicitedAsync(Exchange ex, MatterMessage m)
    {
        var t = _transport!;
        if (m.ProtocolId == MatterProtocol.SecureChannel && ex.Session is null)
        {
            switch ((SecureChannelOpcode)m.Opcode)
            {
                case SecureChannelOpcode.PbkdfParamRequest:
                    await PaseResponderAsync(ex, m).ConfigureAwait(false);
                    return;
                case SecureChannelOpcode.Sigma1:
                    await CaseResponderAsync(ex, m).ConfigureAwait(false);
                    return;
            }

            return;
        }

        if (m.ProtocolId != MatterProtocol.InteractionModel || ex.Session is null) return;
        var ctx = ex.Session.Context;
        if (!Allowed(ex.Session))
        {
            await t.SendAsync(ex, MatterProtocol.InteractionModel, (byte)InteractionOpcode.StatusResponse, InteractionModel.StatusResponse(MatterStatus.UnsupportedAccess)).ConfigureAwait(false);
            return;
        }

        switch ((InteractionOpcode)m.Opcode)
        {
            case InteractionOpcode.ReadRequest:
                var paths = InteractionModel.DecodePaths(Tlv.Decode(m.Payload), 0);
                await t.SendAsync(ex, MatterProtocol.InteractionModel, (byte)InteractionOpcode.ReportData, InteractionModel.ReportData(Read(paths, ctx))).ConfigureAwait(false);
                break;
            case InteractionOpcode.WriteRequest:
                var results = InteractionModel.DecodeWriteRequest(m.Payload).Select(w => (w.Path, Write(w.Path, w.Value, ctx))).ToList();
                await t.SendAsync(ex, MatterProtocol.InteractionModel, (byte)InteractionOpcode.WriteResponse, InteractionModel.WriteResponse(results)).ConfigureAwait(false);
                break;
            case InteractionOpcode.InvokeRequest:
                var invokes = InteractionModel.DecodeInvokeRequest(m.Payload);
                if (invokes.Count == 0) break;
                var (path, fields) = invokes[0];
                var result = Invoke(path, fields, ctx);
                await t.SendAsync(ex, MatterProtocol.InteractionModel, (byte)InteractionOpcode.InvokeResponse,
                    InteractionModel.InvokeResponse(path, result.ResponseCommand, result.Fields, result.Status, result.ClusterStatus)).ConfigureAwait(false);
                break;
            case InteractionOpcode.SubscribeRequest:
                await SubscribeAsync(ex, m, ctx).ConfigureAwait(false);
                break;
            default:
                await t.SendAsync(ex, MatterProtocol.InteractionModel, (byte)InteractionOpcode.StatusResponse, InteractionModel.StatusResponse(MatterStatus.InvalidAction)).ConfigureAwait(false);
                break;
        }
    }

    private bool Allowed(SecureSession s)
    {
        if (s.IsPase) return true;
        lock (_fabrics) return _fabrics.Any(f => f.Index == s.FabricIndex && f.CaseAdminSubject == s.PeerNodeId);
    }

    // ---- PASE responder

    private async Task PaseResponderAsync(Exchange ex, MatterMessage request)
    {
        var t = _transport!;
        if (!CommissioningWindowOpen)
        {
            await t.SendAsync(ex, MatterProtocol.SecureChannel, (byte)SecureChannelOpcode.StatusReport, new StatusReport(1, MatterProtocol.SecureChannel, 4).Encode()).ConfigureAwait(false);
            return;
        }

        var req = Tlv.Decode(request.Payload);
        var initiatorRandom = req.Required(1).AsBytes();
        var peerSession = (ushort)req.Required(2).AsUInt();
        var localSession = t.AllocateSessionId();
        var response = Pase.ParamResponse(initiatorRandom, MatterCrypto.RandomBytes(32), localSession, _options.Iterations, _salt);
        await t.SendAsync(ex, MatterProtocol.SecureChannel, (byte)SecureChannelOpcode.PbkdfParamResponse, response).ConfigureAwait(false);
        var verifier = new Spake2PlusVerifier(Spake2Plus.Context(request.Payload, response), _verifier);
        var pake1 = await t.ReceiveAsync(ex).ConfigureAwait(false);
        var cB = verifier.Respond(Tlv.Decode(pake1.Payload).Required(1).AsBytes());
        await t.SendAsync(ex, MatterProtocol.SecureChannel, (byte)SecureChannelOpcode.Pake2, Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous,
            TlvElement.Bytes(TlvTag.Context(1), verifier.Y), TlvElement.Bytes(TlvTag.Context(2), cB)))).ConfigureAwait(false);
        var pake3 = await t.ReceiveAsync(ex).ConfigureAwait(false);
        byte[] ke;
        try
        {
            ke = verifier.Confirm(Tlv.Decode(pake3.Payload).Required(1).AsBytes());
        }
        catch (ProtocolException)
        {
            Logger.LogWarning("PASE from {Peer}: wrong passcode", ex.Peer);
            await t.SendAsync(ex, MatterProtocol.SecureChannel, (byte)SecureChannelOpcode.StatusReport, new StatusReport(1, MatterProtocol.SecureChannel, 2).Encode()).ConfigureAwait(false);
            return;
        }

        var (i2r, r2i, challenge) = Spake2Plus.SessionKeys(ke);
        t.AddSession(new SecureSession(localSession, peerSession, r2i, i2r, challenge, ex.Peer, isPase: true, 0, 0));
        await t.SendAsync(ex, MatterProtocol.SecureChannel, (byte)SecureChannelOpcode.StatusReport, StatusReport.Success.Encode()).ConfigureAwait(false);
        Logger.LogInformation("PASE session {Session} with {Peer}", localSession, ex.Peer);
    }

    // ---- CASE responder

    private async Task CaseResponderAsync(Exchange ex, MatterMessage request)
    {
        var t = _transport!;
        var sigma1 = Tlv.Decode(request.Payload);
        var initiatorRandom = sigma1.Required(1).AsBytes();
        var peerSession = (ushort)sigma1.Required(2).AsUInt();
        var destination = sigma1.Required(3).AsBytes();
        var initiatorEph = sigma1.Required(4).AsBytes();
        MatterFabric? fabric;
        lock (_fabrics) fabric = _fabrics.FirstOrDefault(f => f.DestinationId(initiatorRandom, f.NodeId).AsSpan().SequenceEqual(destination));
        if (fabric is null)
        {
            await t.SendAsync(ex, MatterProtocol.SecureChannel, (byte)SecureChannelOpcode.StatusReport, new StatusReport(1, MatterProtocol.SecureChannel, 1).Encode()).ConfigureAwait(false);
            return;
        }

        var (ephPriv, ephPub) = MatterCrypto.GenerateKeyPair();
        var shared = MatterCrypto.Ecdh(ephPriv, initiatorEph);
        var responderRandom = MatterCrypto.RandomBytes(32);
        var localSession = t.AllocateSessionId();
        var ipk = fabric.Ipk;
        var tbe2 = Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous,
            TlvElement.Bytes(TlvTag.Context(1), fabric.Noc.EncodeTlv()),
            TlvElement.Bytes(TlvTag.Context(3), MatterCrypto.Sign(fabric.OperationalKey, Case.Tbs(fabric.Noc, ephPub, initiatorEph))),
            TlvElement.Bytes(TlvTag.Context(4), MatterCrypto.RandomBytes(16))));
        var encrypted2 = MatterCrypto.Encrypt(Case.S2K(shared, ipk, responderRandom, ephPub, request.Payload), Case.Sigma2Nonce, tbe2, []);
        var sigma2 = Tlv.Encode(TlvElement.Structure(TlvTag.Anonymous, TlvElement.Bytes(TlvTag.Context(1), responderRandom), TlvElement.UInt(TlvTag.Context(2), localSession),
            TlvElement.Bytes(TlvTag.Context(3), ephPub), TlvElement.Bytes(TlvTag.Context(4), encrypted2)));
        await t.SendAsync(ex, MatterProtocol.SecureChannel, (byte)SecureChannelOpcode.Sigma2, sigma2).ConfigureAwait(false);
        var sigma3Msg = await t.ReceiveAsync(ex).ConfigureAwait(false);
        try
        {
            var tbe3 = Tlv.Decode(MatterCrypto.Decrypt(Case.S3K(shared, ipk, request.Payload, sigma2), Case.Sigma3Nonce, Tlv.Decode(sigma3Msg.Payload).Required(1).AsBytes(), []));
            var peerNoc = Case.VerifyPeer(tbe3.Required(1).AsBytes(), fabric, null);
            if (!MatterCrypto.Verify(peerNoc.PublicKey, Case.Tbs(peerNoc, initiatorEph, ephPub), tbe3.Required(3).AsBytes())) throw new ProtocolException("The initiator's Sigma3 signature does not verify.");
            var (i2r, r2i, challenge) = Case.SessionKeys(shared, ipk, request.Payload, sigma2, sigma3Msg.Payload);
            t.AddSession(new SecureSession(localSession, peerSession, r2i, i2r, challenge, ex.Peer, isPase: false, fabric.NodeId, peerNoc.NodeId!.Value) { FabricIndex = fabric.Index });
            await t.SendAsync(ex, MatterProtocol.SecureChannel, (byte)SecureChannelOpcode.StatusReport, StatusReport.Success.Encode()).ConfigureAwait(false);
            Logger.LogInformation("CASE session {Session} with node 0x{Node:X} on fabric {Fabric}", localSession, peerNoc.NodeId, fabric.Index);
        }
        catch (ProtocolException e)
        {
            Logger.LogWarning("CASE from {Peer} failed: {Message}", ex.Peer, e.Message);
            await t.SendAsync(ex, MatterProtocol.SecureChannel, (byte)SecureChannelOpcode.StatusReport, new StatusReport(1, MatterProtocol.SecureChannel, 2).Encode()).ConfigureAwait(false);
        }
    }

    // ---- data model access

    private IEnumerable<(MatterEndpoint Ep, MatterCluster Cluster, uint Attribute)> Expand(AttributePath p)
    {
        List<MatterEndpoint> eps;
        lock (_endpoints) eps = [.. _endpoints];
        foreach (var ep in eps.Where(e => p.Endpoint is null || e.Id == p.Endpoint))
            foreach (var c in ep.Clusters.Where(c => p.Cluster is null || c.Id == p.Cluster))
                foreach (var a in p.Attribute is { } attr ? (IEnumerable<uint>)[attr] : c.AttributeIds.Order())
                    yield return (ep, c, a);
    }

    private List<AttributeReport> Read(IReadOnlyList<AttributePath> paths, MatterRequestContext ctx)
    {
        var reports = new List<AttributeReport>();
        foreach (var p in paths)
        {
            var concrete = p.Endpoint is not null && p.Cluster is not null && p.Attribute is not null;
            if (concrete)
            {
                MatterEndpoint? ep;
                lock (_endpoints) ep = _endpoints.FirstOrDefault(e => e.Id == p.Endpoint);
                var cluster = ep?.Cluster(p.Cluster!.Value);
                if (ep is null || cluster is null)
                {
                    reports.Add(new AttributeReport(p, 0, null, ep is null ? MatterStatus.UnsupportedEndpoint : MatterStatus.UnsupportedCluster));
                    continue;
                }

                var (value, status) = cluster.Read(p.Attribute!.Value, ctx);
                reports.Add(new AttributeReport(p, cluster.DataVersion, value, status));
                continue;
            }

            foreach (var (ep, cluster, attribute) in Expand(p))
            {
                var (value, status) = cluster.Read(attribute, ctx);
                if (status == MatterStatus.Success) reports.Add(new AttributeReport(new AttributePath(ep.Id, cluster.Id, attribute), cluster.DataVersion, value));
            }
        }

        return reports;
    }

    private MatterStatus Write(AttributePath p, TlvElement value, MatterRequestContext ctx)
    {
        MatterEndpoint? ep;
        lock (_endpoints) ep = _endpoints.FirstOrDefault(e => e.Id == p.Endpoint);
        if (ep is null) return MatterStatus.UnsupportedEndpoint;
        var cluster = p.Cluster is { } c ? ep.Cluster(c) : null;
        if (cluster is null) return MatterStatus.UnsupportedCluster;
        return p.Attribute is { } a ? cluster.Write(a, value, ctx) : MatterStatus.UnsupportedAttribute;
    }

    private CommandResult Invoke(CommandPath path, TlvElement fields, MatterRequestContext ctx)
    {
        MatterEndpoint? ep;
        lock (_endpoints) ep = _endpoints.FirstOrDefault(e => e.Id == path.Endpoint);
        if (ep is null) return new CommandResult(MatterStatus.UnsupportedEndpoint);
        var cluster = ep.Cluster(path.Cluster);
        if (cluster is null) return new CommandResult(MatterStatus.UnsupportedCluster);
        return cluster.Invoke(path.Command, fields, ctx);
    }

    // ---- subscriptions

    private async Task SubscribeAsync(Exchange ex, MatterMessage m, MatterRequestContext ctx)
    {
        var t = _transport!;
        var req = Tlv.Decode(m.Payload);
        var minFloor = (ushort)req.Required(1).AsUInt();
        var maxCeiling = (ushort)req.Required(2).AsUInt();
        var paths = InteractionModel.DecodePaths(req, 3);
        if (!(req[0]?.AsBool() ?? false))
            foreach (var old in _subscriptions.Values.Where(s => s.Session.PeerNodeId == ex.Session!.PeerNodeId && s.Session.FabricIndex == ex.Session.FabricIndex).ToList())
            {
                await old.Stop.CancelAsync().ConfigureAwait(false);
                _subscriptions.TryRemove(old.Id, out _);
            }

        var sub = new Subscription { Id = (uint)Random.Shared.Next(1, int.MaxValue), Session = ex.Session!, Paths = paths, MinInterval = minFloor, MaxInterval = Math.Max(maxCeiling, (ushort)1) };
        await t.SendAsync(ex, MatterProtocol.InteractionModel, (byte)InteractionOpcode.ReportData, InteractionModel.ReportData(Read(paths, ctx), sub.Id, suppressResponse: false)).ConfigureAwait(false);
        var status = await t.ReceiveAsync(ex).ConfigureAwait(false);
        if (status.Opcode != (byte)InteractionOpcode.StatusResponse || InteractionModel.DecodeStatusResponse(status.Payload) != MatterStatus.Success) return;
        _subscriptions[sub.Id] = sub;   // before the response: the peer may change something the moment it has it
        _ = RunSubscriptionAsync(sub);
        await t.SendAsync(ex, MatterProtocol.InteractionModel, (byte)InteractionOpcode.SubscribeResponse, InteractionModel.SubscribeResponse(sub.Id, sub.MaxInterval)).ConfigureAwait(false);
    }

    private void OnChanged(ushort endpoint, uint cluster, uint attribute)
    {
        foreach (var s in _subscriptions.Values)
            if (s.Paths.Any(p => (p.Endpoint is null || p.Endpoint == endpoint) && (p.Cluster is null || p.Cluster == cluster) && (p.Attribute is null || p.Attribute == attribute)))
                lock (s.Dirty) s.Dirty.Add((endpoint, cluster, attribute));
    }

    private async Task RunSubscriptionAsync(Subscription s)
    {
        var t = _transport;
        try
        {
            while (!s.Stop.IsCancellationRequested && t is not null)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(50, s.MinInterval * 1000)), s.Stop.Token).ConfigureAwait(false);
                List<(ushort, uint, uint)> dirty;
                lock (s.Dirty)
                {
                    dirty = [.. s.Dirty];
                    s.Dirty.Clear();
                }

                var due = DateTime.UtcNow - s.LastReport >= TimeSpan.FromSeconds(s.MaxInterval);
                if (dirty.Count == 0 && !due) continue;
                var reports = Read([.. dirty.Select(d => new AttributePath(d.Item1, d.Item2, d.Item3))], s.Session.Context);
                var ex = t.NewExchange(s.Session, s.Session.Peer);
                try
                {
                    await t.SendAsync(ex, MatterProtocol.InteractionModel, (byte)InteractionOpcode.ReportData, InteractionModel.ReportData(reports, s.Id, suppressResponse: false), ct: s.Stop.Token).ConfigureAwait(false);
                    var answer = await t.ReceiveAsync(ex, s.Stop.Token).ConfigureAwait(false);
                    if (answer.Opcode == (byte)InteractionOpcode.StatusResponse && InteractionModel.DecodeStatusResponse(answer.Payload) != MatterStatus.Success) break;
                    s.LastReport = DateTime.UtcNow;
                }
                finally
                {
                    t.Close(ex);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IoTComException or ProtocolException)
        {
        }
        finally
        {
            _subscriptions.TryRemove(s.Id, out _);
        }
    }

    /// <summary>Active subscriptions.</summary>
    public int SubscriptionCount => _subscriptions.Count;

    /// <summary>Label text for logs.</summary>
    public override string ToString() => new StringBuilder().Append(_options.ProductName).Append(" (").Append(SetupPayload.ToManualCode()).Append(')').ToString();
}
