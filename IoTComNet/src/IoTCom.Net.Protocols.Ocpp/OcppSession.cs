using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using IoTCom.Net.Protocols.Ocpp.V16;
using IoTCom.Net.Protocols.Ocpp.V201;
using Microsoft.Extensions.Logging;

namespace IoTCom.Net.Protocols.Ocpp;

/// <summary>A handled or sent OCPP message, for logs and dashboards.</summary>
/// <param name="Direction">Inbound or outbound.</param>
/// <param name="Identity">Charge point identity.</param>
/// <param name="Frame">The parsed frame.</param>
/// <param name="Json">The raw JSON text.</param>
public readonly record struct OcppMessageEvent(FrameDirection Direction, string Identity, OcppFrame Frame, string Json);

/// <summary>
/// One OCPP-J connection: the RPC framework over a WebSocket (CALL / CALLRESULT / CALLERROR pairing, one outstanding
/// CALL per direction, timeouts, error answers) with typed and untyped handlers. Used by <see cref="OcppChargePoint"/>
/// and, per connected charge point, by <see cref="OcppCsms"/>.
/// </summary>
public sealed class OcppSession : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly ILogger? _logger;
    private readonly Action<FrameDirection, string, string>? _tap;
    private readonly Func<bool> _commandsAllowed;
    private readonly ConcurrentDictionary<string, Func<JsonObject, CancellationToken, ValueTask<JsonObject>>> _handlers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _callLock = new(1, 1);
    private readonly Channel<OcppFrame> _inbox = Channel.CreateUnbounded<OcppFrame>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IAsyncDisposable? _owner;
    private (string Id, TaskCompletionSource<OcppFrame> Tcs)? _pending;
    private long _nextId;
    private Task? _receive, _dispatch;
    private int _disposed;

    internal OcppSession(WebSocket socket, OcppVersion version, string identity, TimeSpan callTimeout, ILogger? logger,
        Action<FrameDirection, string, string>? tap, Func<bool>? commandsAllowed, IAsyncDisposable? owner)
    {
        _socket = socket;
        Version = version;
        Identity = identity;
        CallTimeout = callTimeout;
        _logger = logger;
        _tap = tap;
        _commandsAllowed = commandsAllowed ?? (() => true);
        _owner = owner;
    }

    /// <summary>Negotiated protocol version.</summary>
    public OcppVersion Version { get; }

    /// <summary>Charge point identity.</summary>
    public string Identity { get; }

    /// <summary>How long a CALL waits for its answer.</summary>
    public TimeSpan CallTimeout { get; set; }

    /// <summary>True until the WebSocket closes.</summary>
    public bool IsOpen => !_closed.Task.IsCompleted;

    /// <summary>Completes when the connection has closed.</summary>
    public Task Completion => _closed.Task;

    /// <summary>Raised for every message sent or received.</summary>
    public event Action<OcppMessageEvent>? Message;

    internal void Start()
    {
        _receive = Task.Run(ReceiveLoopAsync);
        _dispatch = Task.Run(DispatchLoopAsync);
    }

    // ---- handlers -----------------------------------------------------------------------------------------------------

    /// <summary>Handles an action with raw JSON payloads (replaces an earlier handler for the same action).</summary>
    public void On(string action, Func<JsonObject, CancellationToken, ValueTask<JsonObject>> handler) => _handlers[action] = handler;

    /// <summary>Handles a typed request.</summary>
    public void On<TRequest, TResponse>(Func<TRequest, CancellationToken, ValueTask<TResponse>> handler)
        where TRequest : class, IOcppRequest<TResponse>, new() where TResponse : class
    {
        var probe = new TRequest();
        if (probe.Version != Version)
            throw new InvalidOperationException($"{typeof(TRequest).FullName} is an {probe.Version.DisplayName()} message; this session speaks {Version.DisplayName()}.");
        _handlers[probe.Action] = async (payload, ct) =>
        {
            var request = (TRequest)Deserialize(payload, typeof(TRequest), Version);
            var response = await handler(request, ct).ConfigureAwait(false);
            return Serialize(response, typeof(TResponse), Version);
        };
    }

    /// <summary>Removes a handler; the action is then answered with NotImplemented.</summary>
    public bool Off(string action) => _handlers.TryRemove(action, out _);

    // ---- calls --------------------------------------------------------------------------------------------------------

    /// <summary>Sends a typed request and waits for its confirmation.</summary>
    /// <exception cref="OcppCallException">The peer answered with a CALLERROR.</exception>
    /// <exception cref="ReadOnlyModeException">A command was sent while commands are not allowed.</exception>
    public async Task<TResponse> CallAsync<TResponse>(IOcppRequest<TResponse> request, CancellationToken ct = default) where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Version != Version)
            throw new InvalidOperationException($"{request.GetType().FullName} is an {request.Version.DisplayName()} message; this session speaks {Version.DisplayName()}.");
        if (request.IsCommand && !_commandsAllowed())
            throw new ReadOnlyModeException($"{request.Action} changes the charger's state; the CSMS is read-only until commands are allowed.");
        var payload = Serialize(request, request.GetType(), Version);
        var answer = await CallCoreAsync(request.Action, payload, ct).ConfigureAwait(false);
        try
        {
            return (TResponse)Deserialize(answer, typeof(TResponse), Version);
        }
        catch (JsonException ex)
        {
            throw new ProtocolException($"{request.Action} confirmation does not match the schema: {ex.Message}", ex);
        }
    }

    /// <summary>Sends an untyped CALL and returns the CALLRESULT payload (for actions without a typed class).</summary>
    public Task<JsonObject> CallAsync(string action, JsonObject payload, CancellationToken ct = default) => CallCoreAsync(action, payload, ct);

    private async Task<JsonObject> CallCoreAsync(string action, JsonObject payload, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _callLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsOpen) throw new TransportException($"The OCPP connection of {Identity} is closed.");
            var id = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
            var tcs = new TaskCompletionSource<OcppFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = (id, tcs);
            await SendAsync(OcppFrame.Call(id, action, payload), ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            timeout.CancelAfter(CallTimeout);
            OcppFrame answer;
            try
            {
                answer = await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (!IsOpen) throw new TransportException($"The OCPP connection of {Identity} closed while waiting for {action}.");
                throw new IoTComTimeoutException($"{Identity} did not answer {action} within {CallTimeout.TotalSeconds:0.#} s.");
            }

            if (answer.Type == OcppMessageType.CallError)
                throw new OcppCallException(answer.ErrorCode!, answer.ErrorDescription ?? "", answer.ErrorDetails);
            return answer.Payload!;
        }
        finally
        {
            _pending = null;
            _callLock.Release();
        }
    }

    // ---- I/O ----------------------------------------------------------------------------------------------------------

    private async Task SendAsync(OcppFrame frame, CancellationToken ct)
    {
        var json = frame.ToJson();
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            throw new TransportException($"Sending to {Identity} failed: {ex.Message}", ex);
        }
        finally
        {
            _sendLock.Release();
        }

        Observe(FrameDirection.Outbound, frame, json);
    }

    private void Observe(FrameDirection direction, OcppFrame frame, string json)
    {
        _tap?.Invoke(direction, json, $"{Identity} {frame.Describe()}");
        Message?.Invoke(new OcppMessageEvent(direction, Identity, frame, json));
    }

    private async Task ReceiveLoopAsync()
    {
        Exception? error = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var text = await WebSocketText.ReceiveAsync(_socket, _cts.Token).ConfigureAwait(false);
                if (text is null)
                {
                    // Answer the peer's close frame so its close handshake completes.
                    if (_socket.State == WebSocketState.CloseReceived)
                    {
                        await _sendLock.WaitAsync(_cts.Token).ConfigureAwait(false);
                        try
                        {
                            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", _cts.Token).ConfigureAwait(false);
                        }
                        finally
                        {
                            _sendLock.Release();
                        }
                    }

                    break;
                }
                if (!OcppFrame.TryParse(text, Version, out var frame, out var bad))
                {
                    _tap?.Invoke(FrameDirection.Inbound, text, $"{Identity} invalid: {bad!.Message}");
                    _logger?.LogWarning("OCPP {Identity}: invalid message ({Error})", Identity, bad.Message);
                    if (bad.MessageId is not null)
                        await SendAsync(OcppFrame.Error(bad.MessageId, bad.ErrorCode, bad.Message), _cts.Token).ConfigureAwait(false);
                    continue;
                }

                Observe(FrameDirection.Inbound, frame!, text);
                if (frame!.Type == OcppMessageType.Call)
                {
                    _inbox.Writer.TryWrite(frame);
                }
                else if (_pending is { } p && p.Id == frame.MessageId)
                {
                    p.Tcs.TrySetResult(frame);
                }
                else
                {
                    _logger?.LogDebug("OCPP {Identity}: answer #{Id} matches no outstanding call", Identity, frame.MessageId);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ProtocolException or TransportException or ObjectDisposedException)
        {
            error = ex;
            _logger?.LogDebug(ex, "OCPP {Identity}: receive loop ended", Identity);
        }
        finally
        {
            _inbox.Writer.TryComplete();
            _pending?.Tcs.TrySetException(new TransportException($"The OCPP connection of {Identity} closed.", error));
            _closed.TrySetResult();
        }
    }

    /// <summary>Requests are handled one at a time, off the receive loop, so handlers may call back to the peer.</summary>
    private async Task DispatchLoopAsync()
    {
        try
        {
            await foreach (var call in _inbox.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                OcppFrame answer;
                if (!_handlers.TryGetValue(call.Action!, out var handler))
                {
                    answer = OcppFrame.Error(call.MessageId, OcppErrorCode.NotImplemented, $"{call.Action} is not implemented");
                }
                else
                {
                    try
                    {
                        answer = OcppFrame.Result(call.MessageId, await handler(call.Payload!, _cts.Token).ConfigureAwait(false));
                    }
                    catch (OcppCallException ex)
                    {
                        answer = OcppFrame.Error(call.MessageId, ex.ErrorCode, ex.Description, ex.Details);
                    }
                    catch (JsonException ex)
                    {
                        answer = OcppFrame.Error(call.MessageId, ex.Message.Contains("missing required", StringComparison.OrdinalIgnoreCase)
                            ? OcppErrorCode.Occurrence(Version) : OcppErrorCode.Format(Version), ex.Message);
                    }
                    catch (ReadOnlyModeException ex)
                    {
                        answer = OcppFrame.Error(call.MessageId, OcppErrorCode.SecurityError, ex.Message);
                    }
                    catch (OperationCanceledException) when (_cts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "OCPP {Identity}: handler for {Action} failed", Identity, call.Action);
                        answer = OcppFrame.Error(call.MessageId, OcppErrorCode.InternalError, ex.Message);
                    }
                }

                try
                {
                    await SendAsync(answer, _cts.Token).ConfigureAwait(false);
                }
                catch (TransportException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Closes the WebSocket gracefully.</summary>
    public async Task CloseAsync(string reason = "", CancellationToken ct = default)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, timeout.Token).ConfigureAwait(false);
                await _closed.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }

        await DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Drops the connection without a close handshake (what a lost network does).</summary>
    public ValueTask AbortAsync()
    {
        _socket.Abort();
        return DisposeAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _socket.Abort();
        if (_receive is not null) await _receive.ConfigureAwait(false);
        if (_dispatch is not null) await _dispatch.ConfigureAwait(false);
        _socket.Dispose();
        if (_owner is not null) await _owner.DisposeAsync().ConfigureAwait(false);
        _closed.TrySetResult();
        _cts.Dispose();
        _sendLock.Dispose();
        _callLock.Dispose();
    }

    // ---- JSON ---------------------------------------------------------------------------------------------------------

    private static JsonSerializerContext Context(OcppVersion version) => version == OcppVersion.Ocpp16 ? Ocpp16JsonContext.Default : Ocpp201JsonContext.Default;

    /// <summary>Serializes a typed message to its JSON payload.</summary>
    public static JsonObject Serialize(object message, Type type, OcppVersion version) =>
        JsonSerializer.SerializeToNode(message, type, Context(version)) as JsonObject ?? [];

    /// <summary>Deserializes a JSON payload to a typed message.</summary>
    public static object Deserialize(JsonObject payload, Type type, OcppVersion version) =>
        payload.Deserialize(type, Context(version)) ?? throw new JsonException($"{type.Name}: empty payload");
}
