namespace IoTCom.Net.Protocols.Dmx;

/// <summary>A received DMX frame.</summary>
/// <param name="Universe">Universe number (Art-Net 15-bit port-address or sACN 1–63999).</param>
/// <param name="Data">Channel values (slot 1 at index 0).</param>
/// <param name="Sequence">Sequence number (0 = disabled).</param>
/// <param name="Source">Sender address.</param>
/// <param name="Priority">sACN priority (Art-Net frames report 100).</param>
public readonly record struct DmxFrame(int Universe, ReadOnlyMemory<byte> Data, byte Sequence, string Source, byte Priority = 100);

/// <summary>
/// A 512-channel DMX universe buffer with helpers for fixtures and timed fades. Thread-safe.
/// </summary>
public sealed class DmxUniverse
{
    /// <summary>Channels per universe.</summary>
    public const int Channels = 512;

    private readonly byte[] _data = new byte[Channels];
    private readonly Lock _gate = new();

    /// <summary>Creates a universe.</summary>
    public DmxUniverse(int number) => Number = number;

    /// <summary>Universe number.</summary>
    public int Number { get; }

    /// <summary>Gets or sets a channel (1-based, as on lighting consoles).</summary>
    public byte this[int channel]
    {
        get { lock (_gate) return _data[channel - 1]; }
        set { lock (_gate) _data[channel - 1] = value; }
    }

    /// <summary>Sets consecutive channels starting at <paramref name="channel"/> (1-based), e.g. an RGB fixture.</summary>
    public void Set(int channel, params ReadOnlySpan<byte> values)
    {
        lock (_gate) values.CopyTo(_data.AsSpan(channel - 1));
    }

    /// <summary>Sets every channel to <paramref name="value"/>.</summary>
    public void Fill(byte value)
    {
        lock (_gate) _data.AsSpan().Fill(value);
    }

    /// <summary>Copies the current values.</summary>
    public byte[] Snapshot()
    {
        lock (_gate) return (byte[])_data.Clone();
    }

    /// <summary>Copies values into <paramref name="destination"/>.</summary>
    public void CopyTo(Span<byte> destination)
    {
        lock (_gate) _data.AsSpan(0, Math.Min(destination.Length, Channels)).CopyTo(destination);
    }

    /// <summary>Linear crossfade of every channel from the current state to <paramref name="target"/>.</summary>
    /// <param name="target">Target levels.</param>
    /// <param name="progress">0..1.</param>
    /// <param name="from">Start state captured with <see cref="Snapshot"/>.</param>
    public void Crossfade(ReadOnlySpan<byte> from, ReadOnlySpan<byte> target, double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        lock (_gate)
        {
            var n = Math.Min(Math.Min(from.Length, target.Length), Channels);
            for (var i = 0; i < n; i++) _data[i] = (byte)Math.Round(from[i] + (target[i] - from[i]) * progress);
        }
    }
}
