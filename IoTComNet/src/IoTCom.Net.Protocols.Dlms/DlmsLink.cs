using System.Buffers;
using System.IO.Pipelines;

namespace IoTCom.Net.Protocols.Dlms;

/// <summary>How APDUs travel: HDLC (serial, optical port, also TCP gateways) or the wrapper (TCP/UDP port 4059).</summary>
public enum DlmsFraming
{
    /// <summary>HDLC (IEC 62056-46).</summary>
    Hdlc,
    /// <summary>The TCP/UDP wrapper (IEC 62056-47).</summary>
    Wrapper,
}

/// <summary>A link-level event for taps and logs: direction, raw bytes, summary.</summary>
internal delegate void LinkTap(FrameDirection direction, ReadOnlySpan<byte> frame, string summary);

/// <summary>Moves whole APDUs over a transport pipe (one side of an association).</summary>
internal abstract class DlmsLink(IDuplexPipe pipe, LinkTap tap)
{
    protected IDuplexPipe Pipe { get; } = pipe;

    protected LinkTap TapFrame { get; } = tap;

    private byte[] _pending = [];

    /// <summary>Client: establish the link (HDLC SNRM/UA). Server: nothing.</summary>
    public abstract Task OpenAsync(CancellationToken ct);

    /// <summary>Sends one APDU (segmenting when needed).</summary>
    public abstract Task SendAsync(byte[] apdu, CancellationToken ct);

    /// <summary>Receives the next APDU, or null when the peer closed the link.</summary>
    public abstract Task<byte[]?> ReceiveAsync(CancellationToken ct);

    /// <summary>Client: release the link (HDLC DISC/UA).</summary>
    public abstract Task CloseAsync(CancellationToken ct);

    protected async Task WriteAsync(byte[] bytes, CancellationToken ct)
    {
        var r = await Pipe.Output.WriteAsync(bytes, ct).ConfigureAwait(false);
        if (r.IsCompleted) throw new TransportException("The DLMS link was closed.");
    }

    /// <summary>Reads bytes until <paramref name="parse"/> extracts something from the accumulated buffer.</summary>
    protected async Task<T?> ReadAsync<T>(Func<byte[], (T? Item, int Consumed)> parse, CancellationToken ct) where T : class
    {
        while (true)
        {
            if (_pending.Length > 0)
            {
                var (item, consumed) = parse(_pending);
                if (consumed > 0) _pending = _pending[consumed..];
                if (item is not null) return item;
                if (consumed > 0) continue;
            }

            var result = await Pipe.Input.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (!buffer.IsEmpty) _pending = [.. _pending, .. buffer.ToArray()];
            Pipe.Input.AdvanceTo(buffer.End);
            if (result.IsCompleted && buffer.IsEmpty)
            {
                var (item, _) = _pending.Length > 0 ? parse(_pending) : (null, 0);
                return item;
            }
        }
    }
}

/// <summary>The wrapper link: every APDU is one wrapper PDU.</summary>
internal sealed class WrapperLink(IDuplexPipe pipe, LinkTap tap, ushort ownPort, ushort peerPort, bool client = true) : DlmsLink(pipe, tap)
{
    /// <summary>The peer's wPort (the server learns it from each request).</summary>
    public ushort PeerPort { get; private set; } = peerPort;

    public override Task OpenAsync(CancellationToken ct) => Task.CompletedTask;

    public override Task CloseAsync(CancellationToken ct) => Task.CompletedTask;

    public override async Task SendAsync(byte[] apdu, CancellationToken ct)
    {
        var pdu = DlmsWrapper.Encode(ownPort, PeerPort, apdu);
        TapFrame(FrameDirection.Outbound, pdu, Summary(apdu));
        await WriteAsync(pdu, ct).ConfigureAwait(false);
    }

    public override async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        var apdu = await ReadAsync(buffer =>
        {
            if (!DlmsWrapper.TryRead(buffer, out var source, out _, out var a, out var n)) return ((byte[]?)null, 0);
            if (!client) PeerPort = source;
            TapFrame(FrameDirection.Inbound, buffer.AsSpan(0, n), Summary(a));
            return (a, n);
        }, ct).ConfigureAwait(false);
        return apdu;
    }

    internal static string Summary(byte[] apdu) => DlmsApdu.TryDecode(apdu, out var p, out var e) ? DlmsApdu.Describe(p!) : $"APDU ({e})";
}

/// <summary>
/// The HDLC link (IEC 62056-46), either side: SNRM/UA with parameter negotiation, I-frames with N(S)/N(R),
/// segmentation acknowledged frame by frame with RR (window 1), DISC/UA.
/// </summary>
internal sealed class HdlcLink : DlmsLink
{
    private readonly bool _client;
    private readonly HdlcAddress _own;
    private HdlcAddress _peer;
    private readonly HdlcParameters _proposal;
    private int _vs;
    private int _vr;

    public HdlcLink(IDuplexPipe pipe, LinkTap tap, bool client, HdlcAddress own, HdlcAddress peer, HdlcParameters? parameters = null) : base(pipe, tap)
    {
        _client = client;
        _own = own;
        _peer = peer;
        _proposal = parameters ?? new HdlcParameters();
        Negotiated = _proposal;
    }

    /// <summary>Parameters in force (after SNRM/UA).</summary>
    public HdlcParameters Negotiated { get; private set; }

    /// <summary>Server side: the address the client used for us (set when a frame arrives).</summary>
    public HdlcAddress? PeerSeen { get; private set; }

    private int MaxInfoOut => Math.Max(32, _client ? Negotiated.MaxInfoReceive : Negotiated.MaxInfoTransmit);

    private Task SendFrameAsync(byte control, byte[] info, bool segmented, CancellationToken ct)
    {
        var frame = new HdlcFrame(_peer, _own, control, info, segmented);
        var bytes = frame.Encode();
        TapFrame(FrameDirection.Outbound, bytes, Describe(frame));
        return WriteAsync(bytes, ct);
    }

    private async Task<HdlcFrame?> NextFrameAsync(CancellationToken ct)
    {
        while (true)
        {
            var frame = await ReadAsync(buffer =>
            {
                var status = HdlcFrame.TryRead(buffer, out var f, out var n, out _);
                if (status == HdlcFrame.ReadStatus.Frame) TapFrame(FrameDirection.Inbound, buffer.AsSpan(0, n), Describe(f!));
                return (f, n);
            }, ct).ConfigureAwait(false);
            if (frame is null) return null;
            if (frame.Destination.Upper != _own.Upper || (_own.Lower is { } lower && frame.Destination.Lower is { } l && l != lower)) continue;
            return frame;
        }
    }

    private static string Describe(HdlcFrame frame) =>
        !frame.Segmented && frame.Information is [0xE6, _, _, _, ..] info ? $"{WrapperLink.Summary(info[3..])} · {frame}" : frame.ToString();

    public override async Task OpenAsync(CancellationToken ct)
    {
        if (!_client) return;
        await SendFrameAsync(HdlcControl.Snrm, _proposal.Encode(), false, ct).ConfigureAwait(false);
        var reply = await NextFrameAsync(ct).ConfigureAwait(false) ?? throw new TransportException("The meter closed the link during SNRM.");
        if (reply.Control == HdlcControl.Dm) throw new ProtocolException("The meter answered SNRM with DM (disconnected mode).");
        if (reply.Control != HdlcControl.Ua) throw new ProtocolException($"Expected UA after SNRM, got {HdlcControl.Describe(reply.Control)}.");
        var server = HdlcParameters.Parse(reply.Information);
        // The server's transmit size is what we may receive and vice versa.
        Negotiated = new HdlcParameters(
            Math.Min(_proposal.MaxInfoTransmit, server.MaxInfoReceive),
            Math.Min(_proposal.MaxInfoReceive, server.MaxInfoTransmit),
            1, 1);
        (_vs, _vr) = (0, 0);
    }

    public override async Task CloseAsync(CancellationToken ct)
    {
        if (!_client) return;
        await SendFrameAsync(HdlcControl.Disc, [], false, ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await NextFrameAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }

    public override async Task SendAsync(byte[] apdu, CancellationToken ct)
    {
        byte[] info = [.. (_client ? HdlcFrame.LlcRequest : HdlcFrame.LlcResponse), .. apdu];
        var max = _client ? Negotiated.MaxInfoTransmit : Negotiated.MaxInfoTransmit;
        max = Math.Max(32, max);
        for (var offset = 0; offset < info.Length; offset += max)
        {
            var length = Math.Min(max, info.Length - offset);
            var last = offset + length >= info.Length;
            await SendFrameAsync(HdlcControl.I(_vs, _vr), info[offset..(offset + length)], !last, ct).ConfigureAwait(false);
            _vs = (_vs + 1) & 7;
            if (last) break;
            // Wait for RR before sending the next segment (window 1).
            while (true)
            {
                var ack = await NextFrameAsync(ct).ConfigureAwait(false) ?? throw new TransportException("The peer closed the link during segmentation.");
                if (HdlcControl.IsRr(ack.Control)) break;
                if (ack.Control is HdlcControl.Disc or HdlcControl.Dm) throw new TransportException("The peer disconnected during segmentation.");
            }
        }
    }

    public override async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        var assembled = new List<byte>();
        while (true)
        {
            var frame = await NextFrameAsync(ct).ConfigureAwait(false);
            if (frame is null) return null;
            if (!_client) (_peer, PeerSeen) = (frame.Source, frame.Source);
            if (!_client)
            {
                switch (frame.Control & 0xEF)
                {
                    case 0x83: // SNRM
                        var client = HdlcParameters.Parse(frame.Information);
                        var ours = new HdlcParameters(_proposal.MaxInfoTransmit, _proposal.MaxInfoReceive);
                        Negotiated = new HdlcParameters(Math.Min(ours.MaxInfoTransmit, client.MaxInfoReceive), Math.Min(ours.MaxInfoReceive, client.MaxInfoTransmit), 1, 1);
                        (_vs, _vr) = (0, 0);
                        assembled.Clear();
                        await SendFrameAsync(HdlcControl.Ua, new HdlcParameters(Negotiated.MaxInfoTransmit, Negotiated.MaxInfoReceive).Encode(), false, ct).ConfigureAwait(false);
                        continue;
                    case 0x43: // DISC
                        await SendFrameAsync(HdlcControl.Ua, [], false, ct).ConfigureAwait(false);
                        return null;
                }
            }

            if (!HdlcControl.IsI(frame.Control)) continue;
            if (HdlcControl.SendSequence(frame.Control) != _vr) continue; // duplicate or out of order: ignore
            _vr = (_vr + 1) & 7;
            assembled.AddRange(frame.Information);
            if (frame.Segmented)
            {
                await SendFrameAsync(HdlcControl.Rr(_vr), [], false, ct).ConfigureAwait(false);
                continue;
            }

            var bytes = assembled.ToArray();
            return bytes.Length >= 3 && bytes[0] == 0xE6 ? bytes[3..] : bytes;
        }
    }
}
