using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using FellowOakDicom;
using FellowOakDicom.Network;
using FellowOakDicom.Network.Client;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Adapters.Dicom;

/// <summary>A DICOM object received by <see cref="DicomStoreServer"/>.</summary>
/// <param name="File">The DICOM file (dataset + file meta information).</param>
/// <param name="CallingAe">AE title of the sender (modality).</param>
/// <param name="ReceivedAt">Receive time (UTC).</param>
public sealed record DicomReceived(DicomFile File, string CallingAe, DateTimeOffset ReceivedAt)
{
    /// <summary>Modality (0008,0060), e.g. <c>CT</c>, <c>MR</c>, <c>DX</c>.</summary>
    public string Modality => File.Dataset.GetSingleValueOrDefault(DicomTag.Modality, string.Empty);
    /// <summary>Patient name (0010,0010).</summary>
    public string PatientName => File.Dataset.GetSingleValueOrDefault(DicomTag.PatientName, string.Empty).Replace('^', ' ');
    /// <summary>Study description (0008,1030).</summary>
    public string StudyDescription => File.Dataset.GetSingleValueOrDefault(DicomTag.StudyDescription, string.Empty);
    /// <summary>SOP instance UID.</summary>
    public string SopInstanceUid => File.Dataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty);
}

/// <summary>Options for <see cref="DicomStoreServer"/>.</summary>
public sealed class DicomStoreServerOptions
{
    /// <summary>TCP port (104 is the well-known port; 11112 needs no privileges).</summary>
    public int Port { get; set; } = 11112;
    /// <summary>Our AE title.</summary>
    public string AeTitle { get; set; } = "IOTCOM-SCP";
    /// <summary>Accept any called AE title (default true); otherwise only <see cref="AeTitle"/>.</summary>
    public bool AcceptAnyCalledAe { get; set; } = true;
    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }
}

/// <summary>
/// DICOM Storage SCP (C-STORE + C-ECHO) as an IoTCom server endpoint: modalities and PACS push images to it, and
/// the application consumes them with <see cref="ReceiveAsync"/> or <see cref="ImageReceived"/>.
/// </summary>
public sealed class DicomStoreServer : EndpointBase, IServerEndpoint, ISubscriber<DicomReceived>
{
    private readonly DicomStoreServerOptions _options;
    private readonly List<Channel<DicomReceived>> _subscribers = [];
    private readonly Lock _gate = new();
    private IDicomServer? _server;
    private long _received;

    private DicomStoreServer(DicomStoreServerOptions options) : base("dicom", options.Logger) => _options = options;

    /// <summary>Creates a server.</summary>
    public static DicomStoreServer Create(Action<DicomStoreServerOptions>? configure = null)
    {
        var o = new DicomStoreServerOptions();
        configure?.Invoke(o);
        return new DicomStoreServer(o);
    }

    /// <summary>Options.</summary>
    public DicomStoreServerOptions Options => _options;

    /// <summary>Objects received.</summary>
    public long ImagesReceived => Interlocked.Read(ref _received);

    /// <summary>Raised for every stored object (on an I/O thread).</summary>
    public event Action<DicomReceived>? ImageReceived;

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_server is not null) return ValueTask.CompletedTask;
        SetState(EndpointState.Connecting);
        try
        {
            _server = DicomServerFactory.Create<StoreService>(_options.Port, userState: this);
        }
        catch (Exception ex)
        {
            SetState(EndpointState.Faulted, ex);
            throw new TransportException($"Cannot start DICOM SCP on port {_options.Port}: {ex.Message}", ex);
        }
        SetState(EndpointState.Listening);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken ct = default)
    {
        var server = Interlocked.Exchange(ref _server, null);
        if (server is null) return ValueTask.CompletedTask;
        SetState(EndpointState.Stopping);
        server.Stop();
        server.Dispose();
        lock (_gate) foreach (var s in _subscribers) s.Writer.TryComplete();
        SetState(EndpointState.Disconnected);
        return ValueTask.CompletedTask;
    }

    /// <summary>Streams received objects.</summary>
    public async IAsyncEnumerable<DicomReceived> ReceiveAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var ch = Channel.CreateBounded<DicomReceived>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_gate) _subscribers.Add(ch);
        try
        {
            await foreach (var r in ch.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return r;
        }
        finally
        {
            lock (_gate) _subscribers.Remove(ch);
        }
    }

    /// <summary>Subscribes by modality (<c>"CT"</c>, <c>"MR"</c>, <c>"DX"</c>) or <c>"*"</c>.</summary>
    public async IAsyncEnumerable<Message<DicomReceived>> SubscribeAsync(string filter, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var r in ReceiveAsync(ct).ConfigureAwait(false))
            if (filter is "*" or "#" || string.Equals(r.Modality, filter, StringComparison.OrdinalIgnoreCase))
                yield return new Message<DicomReceived>(r.Modality, r, r.ReceivedAt);
    }

    internal void OnStored(DicomFile file, string callingAe)
    {
        Interlocked.Increment(ref _received);
        var r = new DicomReceived(file, callingAe, DateTimeOffset.UtcNow);
        Tap(FrameDirection.Inbound, Summary(file), () => $"C-STORE {r.Modality} from {callingAe}: {r.StudyDescription}");
        ImageReceived?.Invoke(r);
        Channel<DicomReceived>[] subs;
        lock (_gate) subs = [.. _subscribers];
        foreach (var s in subs) s.Writer.TryWrite(r);
    }

    internal static byte[] Summary(DicomFile file)
    {
        // The tap carries a compact header summary rather than megabytes of pixel data.
        var ds = file.Dataset;
        var text = $"{ds.GetSingleValueOrDefault(DicomTag.SOPClassUID, "")} {ds.GetSingleValueOrDefault(DicomTag.Modality, "")} " +
                   $"{ds.GetSingleValueOrDefault(DicomTag.Rows, (ushort)0)}x{ds.GetSingleValueOrDefault(DicomTag.Columns, (ushort)0)}";
        return Encoding.ASCII.GetBytes(text);
    }

    internal bool AcceptsCalledAe(string calledAe) => _options.AcceptAnyCalledAe || string.Equals(calledAe, _options.AeTitle, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    protected override async ValueTask DisposeAsyncCore() => await StopAsync().ConfigureAwait(false);

    /// <summary>fo-dicom service instance (one per association).</summary>
    private sealed class StoreService : DicomService, IDicomServiceProvider, IDicomCStoreProvider, IDicomCEchoProvider
    {
        private static readonly DicomTransferSyntax[] AcceptedTransferSyntaxes =
        [
            DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.ImplicitVRLittleEndian,
            DicomTransferSyntax.JPEGLSLossless, DicomTransferSyntax.JPEG2000Lossless, DicomTransferSyntax.RLELossless,
        ];

        private string _callingAe = string.Empty;

        public StoreService(INetworkStream stream, Encoding fallbackEncoding, Microsoft.Extensions.Logging.ILogger log, DicomServiceDependencies dependencies)
            : base(stream, fallbackEncoding, log, dependencies)
        {
        }

        private DicomStoreServer Owner => (DicomStoreServer)UserState;

        public Task OnReceiveAssociationRequestAsync(DicomAssociation association)
        {
            if (!Owner.AcceptsCalledAe(association.CalledAE))
                return SendAssociationRejectAsync(DicomRejectResult.Permanent, DicomRejectSource.ServiceUser, DicomRejectReason.CalledAENotRecognized);
            _callingAe = association.CallingAE;
            foreach (var pc in association.PresentationContexts)
            {
                if (pc.AbstractSyntax == DicomUID.Verification) pc.AcceptTransferSyntaxes(DicomTransferSyntax.ExplicitVRLittleEndian, DicomTransferSyntax.ImplicitVRLittleEndian);
                else if (pc.AbstractSyntax.StorageCategory != DicomStorageCategory.None) pc.AcceptTransferSyntaxes(AcceptedTransferSyntaxes);
            }
            return SendAssociationAcceptAsync(association);
        }

        public Task OnReceiveAssociationReleaseRequestAsync() => SendAssociationReleaseResponseAsync();

        public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) { }

        public void OnConnectionClosed(Exception exception) { }

        public Task<DicomCStoreResponse> OnCStoreRequestAsync(DicomCStoreRequest request)
        {
            Owner.OnStored(request.File, _callingAe);
            return Task.FromResult(new DicomCStoreResponse(request, DicomStatus.Success));
        }

        public Task OnCStoreRequestExceptionAsync(string tempFileName, Exception e) => Task.CompletedTask;

        public Task<DicomCEchoResponse> OnCEchoRequestAsync(DicomCEchoRequest request) => Task.FromResult(new DicomCEchoResponse(request, DicomStatus.Success));
    }
}

/// <summary>Options for <see cref="DicomStoreClient"/>.</summary>
public sealed class DicomStoreClientOptions
{
    /// <summary>PACS / SCP host.</summary>
    public string Host { get; set; } = "127.0.0.1";
    /// <summary>PACS / SCP port.</summary>
    public int Port { get; set; } = 11112;
    /// <summary>Our AE title.</summary>
    public string CallingAe { get; set; } = "IOTCOM-SCU";
    /// <summary>Remote AE title.</summary>
    public string CalledAe { get; set; } = "IOTCOM-SCP";
    /// <summary>Logger.</summary>
    public ILogger? Logger { get; set; }
}

/// <summary>DICOM Storage SCU (C-STORE / C-ECHO): sends images from a modality or gateway to a PACS.</summary>
public sealed class DicomStoreClient : EndpointBase, IClientEndpoint
{
    private readonly DicomStoreClientOptions _options;

    private DicomStoreClient(DicomStoreClientOptions options) : base("dicom", options.Logger) => _options = options;

    /// <summary>Creates a client.</summary>
    public static DicomStoreClient Create(Action<DicomStoreClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var o = new DicomStoreClientOptions();
        configure(o);
        return new DicomStoreClient(o);
    }

    /// <summary>DICOM associations are opened per send; this verifies the peer with C-ECHO.</summary>
    public async ValueTask ConnectAsync(CancellationToken ct = default)
    {
        SetState(EndpointState.Connecting);
        try
        {
            await EchoAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetState(EndpointState.Disconnected, ex);
            throw;
        }
        SetState(EndpointState.Connected);
    }

    /// <inheritdoc />
    public ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        SetState(EndpointState.Disconnected);
        return ValueTask.CompletedTask;
    }

    /// <summary>C-ECHO (verification).</summary>
    /// <exception cref="TransportException">The peer did not answer with Success.</exception>
    public async Task EchoAsync(CancellationToken ct = default)
    {
        DicomStatus? status = null;
        var request = new DicomCEchoRequest { OnResponseReceived = (_, r) => status = r.Status };
        await SendAsync(request, ct).ConfigureAwait(false);
        if (status != DicomStatus.Success) throw new TransportException($"C-ECHO to {_options.CalledAe}@{_options.Host}:{_options.Port} failed: {status}");
    }

    /// <summary>C-STORE one object; returns the DICOM status.</summary>
    public async Task<DicomStatus> StoreAsync(DicomFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        DicomStatus status = DicomStatus.ProcessingFailure;
        var request = new DicomCStoreRequest(file) { OnResponseReceived = (_, r) => status = r.Status };
        Tap(FrameDirection.Outbound, DicomStoreServer.Summary(file), () => $"C-STORE {file.Dataset.GetSingleValueOrDefault(DicomTag.Modality, "")} to {_options.CalledAe}");
        await SendAsync(request, ct).ConfigureAwait(false);
        if (status.State != DicomState.Success) throw new DeviceException($"C-STORE rejected: {status}", status.Code);
        return status;
    }

    private async Task SendAsync(DicomRequest request, CancellationToken ct)
    {
        var client = DicomClientFactory.Create(_options.Host, _options.Port, false, _options.CallingAe, _options.CalledAe);
        await client.AddRequestAsync(request).ConfigureAwait(false);
        try
        {
            await client.SendAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IoTComException)
        {
            throw new TransportException($"DICOM association with {_options.CalledAe}@{_options.Host}:{_options.Port} failed: {ex.Message}", ex);
        }
    }
}
