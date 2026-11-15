using System.Threading.Channels;

namespace IoTCom.Net.Protocols.Knx;

/// <summary>A group telegram as an application sees it.</summary>
/// <param name="Source">Sender's individual address.</param>
/// <param name="Destination">Group address.</param>
/// <param name="Service">Read, response or write.</param>
/// <param name="Value">Value (empty for reads).</param>
/// <param name="Time">Receive time.</param>
public readonly record struct KnxGroupEvent(KnxIndividualAddress Source, KnxGroupAddress Destination, KnxApci Service, KnxGroupValue Value, DateTimeOffset Time)
{
    /// <inheritdoc />
    public override string ToString() => $"{Source} → {Destination} {Service switch { KnxApci.GroupValueRead => "Read", KnxApci.GroupValueResponse => "Response", _ => "Write" }}" + (Service == KnxApci.GroupValueRead ? "" : $" {Value}");
}

/// <summary>Group communication shared by <see cref="KnxTunnelClient"/> and <see cref="KnxRouter"/>.</summary>
public interface IKnxGroupClient
{
    /// <summary>Refuses group writes when true.</summary>
    bool ReadOnly { get; }

    /// <summary>Raised for every group telegram received (writes, responses and reads by others).</summary>
    event Action<KnxGroupEvent>? GroupValueReceived;

    /// <summary>Sends A_GroupValue_Write.</summary>
    /// <exception cref="ReadOnlyModeException">The client is read-only.</exception>
    Task WriteAsync(KnxGroupAddress address, KnxGroupValue value, CancellationToken ct = default);

    /// <summary>Sends A_GroupValue_Read and waits for the first A_GroupValue_Response.</summary>
    /// <exception cref="IoTComTimeoutException">No device answered.</exception>
    Task<KnxGroupValue> ReadAsync(KnxGroupAddress address, TimeSpan? timeout = null, CancellationToken ct = default);
}

/// <summary>Typed helpers for <see cref="IKnxGroupClient"/>.</summary>
public static class KnxGroupClientExtensions
{
    /// <summary>Writes a value encoded with a datapoint type (e.g. <c>"9.001", 21.5</c>).</summary>
    public static Task WriteAsync(this IKnxGroupClient client, string address, string dpt, object value, CancellationToken ct = default) =>
        client.WriteAsync(KnxGroupAddress.Parse(address), KnxDpt.Encode(dpt, value), ct);

    /// <summary>Reads a value and decodes it with a datapoint type.</summary>
    public static async Task<object> ReadAsync(this IKnxGroupClient client, string address, string dpt, TimeSpan? timeout = null, CancellationToken ct = default) =>
        KnxDpt.Decode(dpt, await client.ReadAsync(KnxGroupAddress.Parse(address), timeout, ct).ConfigureAwait(false));
}

/// <summary>A named group address with its datapoint type (one row of an ETS group address export).</summary>
/// <param name="Address">Group address.</param>
/// <param name="Dpt">Datapoint type id.</param>
/// <param name="Name">Name.</param>
public sealed record KnxGroupAddressInfo(KnxGroupAddress Address, string Dpt, string Name);

/// <summary>An in-process KNX TP1 line: every telegram sent reaches every attached participant, in order.</summary>
public sealed class KnxBus : IAsyncDisposable
{
    private readonly Channel<(KnxCemiFrame Frame, object? Origin)> _queue = Channel.CreateUnbounded<(KnxCemiFrame, object?)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;
    private long _telegrams;

    /// <summary>Creates the line and starts delivering.</summary>
    public KnxBus() => _pump = Task.Run(PumpAsync);

    /// <summary>Raised for every telegram, as an L_Data.ind, with the participant that sent it.</summary>
    public event Action<KnxCemiFrame, object?>? Telegram;

    /// <summary>Telegrams carried so far.</summary>
    public long Telegrams => Interlocked.Read(ref _telegrams);

    /// <summary>Puts a telegram on the line (delivered asynchronously, in order).</summary>
    public void Send(KnxCemiFrame frame, object? origin = null) =>
        _queue.Writer.TryWrite((frame with { MessageCode = KnxMessageCode.DataIndication, ConfirmError = false }, origin));

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var (frame, origin) in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _telegrams);
                Telegram?.Invoke(frame, origin);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        await _pump.ConfigureAwait(false);
        _cts.Dispose();
    }
}
