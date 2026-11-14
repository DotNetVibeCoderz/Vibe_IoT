using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace IoTCom.Net.Protocols.Ocpp;

/// <summary>What the server learned from a charge point's WebSocket upgrade request.</summary>
public sealed class OcppUpgradeRequest
{
    internal OcppUpgradeRequest(string path, string identity, IReadOnlyList<string> subProtocols, string? user, string? password, string remote) =>
        (Path, Identity, SubProtocols, User, Password, RemoteAddress) = (path, identity, subProtocols, user, password, remote);

    /// <summary>Request path, e.g. <c>/ocpp/CP-001</c>.</summary>
    public string Path { get; }

    /// <summary>Charge point identity (last path segment, URL-decoded).</summary>
    public string Identity { get; }

    /// <summary>Subprotocols offered by the charge point, in its order.</summary>
    public IReadOnlyList<string> SubProtocols { get; }

    /// <summary>HTTP Basic user (OCPP security profile 1 uses the identity).</summary>
    public string? User { get; }

    /// <summary>HTTP Basic password (never logged).</summary>
    public string? Password { get; }

    /// <summary>Peer address.</summary>
    public string RemoteAddress { get; }
}

/// <summary>The minimal HTTP/1.1 WebSocket upgrade (RFC 6455 §4) used over IoTCom transports.</summary>
internal static class OcppWebSocketHandshake
{
    private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxHeaderBytes = 16 * 1024;

#pragma warning disable CA5350 // RFC 6455 defines Sec-WebSocket-Accept with SHA-1; it is not used for security.
    public static string AcceptKey(string key) => Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + Guid)));
#pragma warning restore CA5350

    public static async Task<WebSocket> ConnectAsync(Stream stream, Uri url, OcppVersion version, string? user, string? password, TimeSpan keepAlive, CancellationToken ct)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"GET {url.PathAndQuery} HTTP/1.1\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Host: {url.Authority}\r\n");
        sb.Append("Upgrade: websocket\r\nConnection: Upgrade\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"Sec-WebSocket-Protocol: {version.SubProtocol()}\r\n");
        if (password is not null)
            sb.Append(CultureInfo.InvariantCulture, $"Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"))}\r\n");
        sb.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var (status, headers) = await ReadHeadAsync(stream, ct).ConfigureAwait(false);
        var parts = status.Split(' ', 3);
        if (parts.Length < 2 || parts[1] != "101")
            throw new TransportException($"WebSocket upgrade refused: {status}" + (parts.Length > 1 && parts[1] == "401" ? " (check the identity and password)" : ""));
        if (!headers.TryGetValue("sec-websocket-accept", out var accept) || accept != AcceptKey(key))
            throw new ProtocolException("WebSocket upgrade answered with a wrong Sec-WebSocket-Accept.");
        headers.TryGetValue("sec-websocket-protocol", out var chosen);
        if (OcppVersions.FromSubProtocol(chosen) != version)
            throw new ProtocolException($"The server did not accept {version.SubProtocol()} (answered '{chosen ?? "no subprotocol"}').");

        return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = false, SubProtocol = version.SubProtocol(), KeepAliveInterval = keepAlive });
    }

    /// <summary>Reads the upgrade request; returns null after answering an error status.</summary>
    public static async Task<(WebSocket Socket, OcppUpgradeRequest Request, OcppVersion Version)?> AcceptAsync(
        Stream stream, string remote, IReadOnlyList<OcppVersion> supported, Func<OcppUpgradeRequest, bool>? authorize, TimeSpan keepAlive, CancellationToken ct)
    {
        string status;
        Dictionary<string, string> headers;
        try
        {
            (status, headers) = await ReadHeadAsync(stream, ct).ConfigureAwait(false);
        }
        catch (ProtocolException)
        {
            await RespondAsync(stream, "400 Bad Request", null, ct).ConfigureAwait(false);
            return null;
        }

        var parts = status.Split(' ');
        if (parts.Length < 3 || parts[0] != "GET" || !headers.TryGetValue("sec-websocket-key", out var key)
            || !headers.TryGetValue("upgrade", out var upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase))
        {
            await RespondAsync(stream, "400 Bad Request", null, ct).ConfigureAwait(false);
            return null;
        }

        var path = parts[1].Split('?')[0];
        var identity = Uri.UnescapeDataString(path.TrimEnd('/').Split('/')[^1]);
        var offered = headers.TryGetValue("sec-websocket-protocol", out var p)
            ? p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        string? user = null, password = null;
        if (headers.TryGetValue("authorization", out var auth) && auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim()));
                var colon = decoded.IndexOf(':', StringComparison.Ordinal);
                (user, password) = colon < 0 ? (decoded, "") : (decoded[..colon], decoded[(colon + 1)..]);
            }
            catch (FormatException)
            {
            }
        }

        var request = new OcppUpgradeRequest(path, identity, offered, user, password, remote);
        if (identity.Length == 0)
        {
            await RespondAsync(stream, "404 Not Found", null, ct).ConfigureAwait(false);
            return null;
        }

        if (authorize is not null && !authorize(request))
        {
            await RespondAsync(stream, "401 Unauthorized", "WWW-Authenticate: Basic realm=\"OCPP\"\r\n", ct).ConfigureAwait(false);
            return null;
        }

        // The server's preference order wins (RFC 6455 lets the server pick).
        OcppVersion? version = null;
        foreach (var v in supported)
        {
            if (offered.Contains(v.SubProtocol(), StringComparer.OrdinalIgnoreCase))
            {
                version = v;
                break;
            }
        }

        if (version is null)
        {
            await RespondAsync(stream, "400 Bad Request", null, ct).ConfigureAwait(false);
            return null;
        }

        await RespondAsync(stream, "101 Switching Protocols",
            $"Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {AcceptKey(key)}\r\nSec-WebSocket-Protocol: {version.Value.SubProtocol()}\r\n", ct).ConfigureAwait(false);
        var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, SubProtocol = version.Value.SubProtocol(), KeepAliveInterval = keepAlive });
        return (socket, request, version.Value);
    }

    private static async Task RespondAsync(Stream stream, string status, string? headers, CancellationToken ct)
    {
        var text = $"HTTP/1.1 {status}\r\n{headers}" + (status.StartsWith("101", StringComparison.Ordinal) ? "" : "Content-Length: 0\r\nConnection: close\r\n") + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads the status/request line and headers byte by byte, so nothing after the blank line is consumed.</summary>
    private static async Task<(string Status, Dictionary<string, string> Headers)> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[1];
        var head = new List<byte>(512);
        while (true)
        {
            var n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) throw new TransportException("The connection closed during the WebSocket upgrade.");
            head.Add(buffer[0]);
            var c = head.Count;
            if (c >= 4 && head[c - 4] == '\r' && head[c - 3] == '\n' && head[c - 2] == '\r' && head[c - 1] == '\n') break;
            if (c > MaxHeaderBytes) throw new ProtocolException("WebSocket upgrade headers are too long.");
        }

        var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0) continue;
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var prior) ? prior + ", " + value : value;
        }

        return (lines[0], headers);
    }
}

/// <summary>A <see cref="Stream"/> over an <see cref="IDuplexPipe"/>, so WebSockets run on any IoTCom transport.</summary>
internal sealed class DuplexPipeStream(IDuplexPipe pipe, IAsyncDisposable? owner) : Stream
{
    private readonly Stream _input = pipe.Input.AsStream();
    private readonly Stream _output = pipe.Output.AsStream();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _input.ReadAsync(buffer, offset, count, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _output.WriteAsync(buffer, cancellationToken);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _output.WriteAsync(buffer, offset, count, cancellationToken);
    public override void Flush() => _output.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _output.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private int _disposed;

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await _output.DisposeAsync().ConfigureAwait(false); } catch (InvalidOperationException) { }
        try { await _input.DisposeAsync().ConfigureAwait(false); } catch (InvalidOperationException) { }
        if (owner is not null) await owner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.Dispose(disposing);
    }
}

/// <summary>Reads whole text messages from a WebSocket.</summary>
internal static class WebSocketText
{
    public const int MaxMessageBytes = 1024 * 1024;

    public static async Task<string?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (ms.Length > MaxMessageBytes) throw new ProtocolException("OCPP message larger than 1 MiB.");
            if (r.EndOfMessage)
            {
                if (r.MessageType != WebSocketMessageType.Text) return "";
                return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            }
        }
    }

    public static string Describe(EndPoint? ep) => ep?.ToString() ?? "?";
}
