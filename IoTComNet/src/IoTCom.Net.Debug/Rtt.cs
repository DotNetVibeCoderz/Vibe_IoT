using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace IoTCom.Net.Debug;

/// <summary>Read and write access to a target's memory (a debug probe, or a simulated MCU).</summary>
public interface ITargetMemory
{
    /// <summary>Reads <paramref name="length"/> bytes.</summary>
    Task<byte[]> ReadMemoryAsync(ulong address, int length, CancellationToken ct = default);

    /// <summary>Writes bytes (RAM; flash goes through flashing).</summary>
    Task WriteMemoryAsync(ulong address, ReadOnlyMemory<byte> data, CancellationToken ct = default);
}

/// <summary>One RTT buffer descriptor as it sits in target RAM (24 bytes, 32-bit targets).</summary>
/// <param name="NamePointer">Address of the channel name (C string), 0 = unnamed.</param>
/// <param name="Buffer">Ring buffer address.</param>
/// <param name="Size">Ring buffer size.</param>
/// <param name="WriteOffset">Write offset (target writes up buffers, host writes down buffers).</param>
/// <param name="ReadOffset">Read offset (host reads up buffers, target reads down buffers).</param>
/// <param name="Flags">Mode in bits 0–1: 0 skip, 1 trim, 2 block when full.</param>
public readonly record struct RttDescriptor(uint NamePointer, uint Buffer, uint Size, uint WriteOffset, uint ReadOffset, uint Flags)
{
    /// <summary>Descriptor size.</summary>
    public const int Length = 24;

    /// <summary>True when the descriptor points at a usable buffer.</summary>
    public bool IsValid => Buffer != 0 && Size > 1 && WriteOffset < Size && ReadOffset < Size;

    /// <summary>Bytes waiting to be read.</summary>
    public uint Available => RttRing.Available(WriteOffset, ReadOffset, Size);

    /// <summary>Bytes that can be written.</summary>
    public uint Free => RttRing.Free(WriteOffset, ReadOffset, Size);

    internal static RttDescriptor Parse(ReadOnlySpan<byte> b) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(b), BinaryPrimitives.ReadUInt32LittleEndian(b[4..]), BinaryPrimitives.ReadUInt32LittleEndian(b[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(b[12..]), BinaryPrimitives.ReadUInt32LittleEndian(b[16..]), BinaryPrimitives.ReadUInt32LittleEndian(b[20..]));
}

/// <summary>Ring-buffer arithmetic shared by the host and the target (one slot always stays free).</summary>
public static class RttRing
{
    /// <summary>Bytes between the read and the write offset.</summary>
    public static uint Available(uint write, uint read, uint size) => write >= read ? write - read : size - read + write;

    /// <summary>Bytes the writer may add without reaching the reader.</summary>
    public static uint Free(uint write, uint read, uint size) => size - 1 - Available(write, read, size);

    /// <summary>The (offset, length) pieces to read: one, or two when the data wraps.</summary>
    public static IReadOnlyList<(uint Offset, uint Length)> ReadSpans(uint write, uint read, uint size)
    {
        if (write == read) return [];
        if (write > read) return [(read, write - read)];
        return write == 0 ? [(read, size - read)] : [(read, size - read), (0, write)];
    }
}

/// <summary>The SEGGER RTT control block: the 16-byte ID "SEGGER RTT", buffer counts and descriptors.</summary>
/// <param name="Address">Where the block sits.</param>
/// <param name="Up">Target → host buffers.</param>
/// <param name="Down">Host → target buffers.</param>
public sealed record RttControlBlock(ulong Address, IReadOnlyList<RttDescriptor> Up, IReadOnlyList<RttDescriptor> Down)
{
    /// <summary>The ID at the start of the block.</summary>
    public static ReadOnlySpan<byte> Id => "SEGGER RTT\0\0\0\0\0\0"u8;

    /// <summary>Header: ID (16) and the two counts (8).</summary>
    public const int HeaderLength = 24;

    /// <summary>Largest buffer count accepted (SEGGER's default configuration uses 3).</summary>
    public const int MaxBuffers = 16;

    /// <summary>Bytes the block occupies.</summary>
    public int Length => HeaderLength + ((Up.Count + Down.Count) * RttDescriptor.Length);

    /// <summary>Address of up descriptor <paramref name="channel"/>.</summary>
    public ulong UpDescriptorAddress(int channel) => Address + HeaderLength + ((ulong)channel * RttDescriptor.Length);

    /// <summary>Address of down descriptor <paramref name="channel"/>.</summary>
    public ulong DownDescriptorAddress(int channel) => Address + HeaderLength + ((ulong)(Up.Count + channel) * RttDescriptor.Length);

    /// <summary>Counts from a header; null when the ID does not match or the counts are implausible.</summary>
    public static (int Up, int Down)? ParseHeader(ReadOnlySpan<byte> b)
    {
        if (b.Length < HeaderLength || !b[..16].SequenceEqual(Id)) return null;
        var up = BinaryPrimitives.ReadInt32LittleEndian(b[16..]);
        var down = BinaryPrimitives.ReadInt32LittleEndian(b[20..]);
        return up is < 0 or > MaxBuffers || down is < 0 or > MaxBuffers ? null : (up, down);
    }

    /// <summary>Parses a complete block (header and all descriptors).</summary>
    public static RttControlBlock? Parse(ReadOnlySpan<byte> b, ulong address)
    {
        if (ParseHeader(b) is not { } counts) return null;
        var need = HeaderLength + ((counts.Up + counts.Down) * RttDescriptor.Length);
        if (b.Length < need) return null;
        var up = new List<RttDescriptor>();
        var down = new List<RttDescriptor>();
        for (var i = 0; i < counts.Up + counts.Down; i++)
        {
            var d = RttDescriptor.Parse(b.Slice(HeaderLength + (i * RttDescriptor.Length), RttDescriptor.Length));
            (i < counts.Up ? up : down).Add(d);
        }

        return new RttControlBlock(address, up, down);
    }

    /// <summary>Normalised description shared with the Rust twin.</summary>
    public string Describe()
    {
        static string D(RttDescriptor d) => string.Create(CultureInfo.InvariantCulture,
            $"{d.Buffer:X8}:{d.Size}:w{d.WriteOffset}:r{d.ReadOffset}:f{d.Flags & 3}:a{d.Available}:{(d.IsValid ? "ok" : "bad")}");
        return $"up={string.Join(',', Up.Select(D))} down={string.Join(',', Down.Select(D))}";
    }
}

/// <summary>A named RTT channel.</summary>
/// <param name="Index">Channel number.</param>
/// <param name="Name">Name from the target ("Terminal", "defmt"…).</param>
/// <param name="Size">Buffer size.</param>
/// <param name="Up">True for target → host.</param>
public sealed record RttChannel(int Index, string Name, uint Size, bool Up);

/// <summary>
/// An RTT host over any <see cref="ITargetMemory"/>: finds the control block in RAM, reads up channels (advancing only
/// the read offset in the control block) and writes down channels. The target keeps running; RTT needs no halt.
/// </summary>
public sealed class RttClient
{
    private readonly ITargetMemory _memory;
    private readonly bool _allowDown;

    private RttClient(ITargetMemory memory, RttControlBlock block, IReadOnlyList<RttChannel> up, IReadOnlyList<RttChannel> down, bool allowDown)
        => (_memory, Block, UpChannels, DownChannels, _allowDown) = (memory, block, up, down, allowDown);

    /// <summary>Control block found at attach time.</summary>
    public RttControlBlock Block { get; }

    /// <summary>Up channels.</summary>
    public IReadOnlyList<RttChannel> UpChannels { get; }

    /// <summary>Down channels.</summary>
    public IReadOnlyList<RttChannel> DownChannels { get; }

    /// <summary>Address of the control block.</summary>
    public ulong Address => Block.Address;

    /// <summary>
    /// Scans <paramref name="ramStart"/>…+<paramref name="ramLength"/> for the control block (or reads it at
    /// <paramref name="knownAddress"/>, e.g. from the ELF symbol <c>_SEGGER_RTT</c>).
    /// </summary>
    /// <exception cref="DeviceException">No control block: the firmware does not use RTT, or has not initialised it yet.</exception>
    public static async Task<RttClient> AttachAsync(ITargetMemory memory, ulong ramStart, int ramLength, ulong? knownAddress = null, bool allowDownWrites = false, CancellationToken ct = default)
    {
        ulong? found = knownAddress;
        if (found is null)
        {
            const int Chunk = 4096;
            for (var offset = 0; offset < ramLength && found is null; offset += Chunk - 16)
            {
                var length = Math.Min(Chunk, ramLength - offset);
                if (length < 16) break;
                var data = await memory.ReadMemoryAsync(ramStart + (ulong)offset, length, ct).ConfigureAwait(false);
                var at = data.AsSpan().IndexOf(RttControlBlock.Id);
                if (at >= 0) found = ramStart + (ulong)offset + (ulong)at;
            }
        }

        if (found is not { } address) throw new DeviceException($"No RTT control block between 0x{ramStart:X8} and 0x{ramStart + (ulong)ramLength:X8}: the firmware does not use RTT or has not started it yet.");
        var header = await memory.ReadMemoryAsync(address, RttControlBlock.HeaderLength, ct).ConfigureAwait(false);
        var counts = RttControlBlock.ParseHeader(header) ?? throw new DeviceException($"0x{address:X8} does not hold a valid RTT control block.");
        var full = await memory.ReadMemoryAsync(address, RttControlBlock.HeaderLength + ((counts.Up + counts.Down) * RttDescriptor.Length), ct).ConfigureAwait(false);
        var block = RttControlBlock.Parse(full, address)!;
        async Task<string> Name(uint pointer)
        {
            if (pointer == 0) return "";
            var bytes = await memory.ReadMemoryAsync(pointer, 32, ct).ConfigureAwait(false);
            var nul = Array.IndexOf(bytes, (byte)0);
            return Encoding.ASCII.GetString(bytes, 0, nul < 0 ? bytes.Length : nul);
        }

        var up = new List<RttChannel>();
        for (var i = 0; i < block.Up.Count; i++)
            if (block.Up[i].IsValid) up.Add(new RttChannel(i, await Name(block.Up[i].NamePointer).ConfigureAwait(false), block.Up[i].Size, true));
        var down = new List<RttChannel>();
        for (var i = 0; i < block.Down.Count; i++)
            if (block.Down[i].IsValid) down.Add(new RttChannel(i, await Name(block.Down[i].NamePointer).ConfigureAwait(false), block.Down[i].Size, false));
        return new RttClient(memory, block, up, down, allowDownWrites);
    }

    /// <summary>Reads what is waiting on up channel <paramref name="channel"/> (empty when nothing).</summary>
    public async Task<byte[]> ReadAsync(int channel = 0, CancellationToken ct = default)
    {
        var at = Block.UpDescriptorAddress(channel);
        var d = RttDescriptor.Parse(await _memory.ReadMemoryAsync(at, RttDescriptor.Length, ct).ConfigureAwait(false));
        if (!d.IsValid) throw new DeviceException($"RTT up channel {channel} is not valid (buffer 0x{d.Buffer:X8}, size {d.Size}).");
        var spans = RttRing.ReadSpans(d.WriteOffset, d.ReadOffset, d.Size);
        if (spans.Count == 0) return [];
        var result = new List<byte>();
        foreach (var (offset, length) in spans) result.AddRange(await _memory.ReadMemoryAsync(d.Buffer + offset, (int)length, ct).ConfigureAwait(false));
        await _memory.WriteMemoryAsync(at + 16, BitConverter.GetBytes(d.WriteOffset), ct).ConfigureAwait(false);   // RdOff = WrOff
        return [.. result];
    }

    /// <summary>Reads up channel <paramref name="channel"/> as text.</summary>
    public async Task<string> ReadTextAsync(int channel = 0, CancellationToken ct = default) => Encoding.UTF8.GetString(await ReadAsync(channel, ct).ConfigureAwait(false));

    /// <summary>Writes to down channel <paramref name="channel"/>; returns the bytes that fit.</summary>
    /// <exception cref="ReadOnlyModeException">Down writes were not allowed at attach time.</exception>
    public async Task<int> WriteAsync(ReadOnlyMemory<byte> data, int channel = 0, CancellationToken ct = default)
    {
        if (!_allowDown) throw new ReadOnlyModeException("Writing to an RTT down channel sends input to the firmware; attach with allowDownWrites.");
        var at = Block.DownDescriptorAddress(channel);
        var d = RttDescriptor.Parse(await _memory.ReadMemoryAsync(at, RttDescriptor.Length, ct).ConfigureAwait(false));
        if (!d.IsValid) throw new DeviceException($"RTT down channel {channel} is not valid.");
        var n = (int)Math.Min(d.Free, (uint)data.Length);
        var write = d.WriteOffset;
        var done = 0;
        while (done < n)
        {
            var piece = (int)Math.Min((uint)(n - done), d.Size - write);
            await _memory.WriteMemoryAsync(d.Buffer + write, data.Slice(done, piece), ct).ConfigureAwait(false);
            done += piece;
            write = (write + (uint)piece) % d.Size;
        }

        await _memory.WriteMemoryAsync(at + 12, BitConverter.GetBytes(write), ct).ConfigureAwait(false);   // WrOff
        return n;
    }
}
