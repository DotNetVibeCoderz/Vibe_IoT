using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace IoTCom.Net.Debug;

/// <summary>A contiguous block of firmware at an address.</summary>
/// <param name="Address">Start address.</param>
/// <param name="Data">Bytes.</param>
public sealed record FirmwareSegment(uint Address, byte[] Data)
{
    /// <summary>First address after the segment.</summary>
    public ulong End => (ulong)Address + (uint)Data.Length;
}

/// <summary>
/// A firmware image: sorted, non-overlapping segments plus an optional entry point. Loads Intel HEX, raw binaries,
/// DFU files (with the DFU suffix, plain or DfuSe) and ELF32 executables (PT_LOAD segments at their physical address).
/// </summary>
public sealed class FirmwareImage
{
    private readonly List<FirmwareSegment> _segments;

    /// <summary>Creates an image; adjacent segments are merged, overlaps are rejected.</summary>
    public FirmwareImage(IEnumerable<FirmwareSegment> segments, uint? entryPoint = null)
    {
        var sorted = segments.Where(s => s.Data.Length > 0).OrderBy(s => s.Address).ToList();
        _segments = [];
        foreach (var s in sorted)
        {
            if (_segments.Count > 0)
            {
                var last = _segments[^1];
                if (s.Address < last.End) throw new ProtocolException($"Firmware segments overlap at 0x{s.Address:X8}.");
                if (s.Address == last.End)
                {
                    _segments[^1] = new FirmwareSegment(last.Address, [.. last.Data, .. s.Data]);
                    continue;
                }
            }

            _segments.Add(s);
        }

        EntryPoint = entryPoint;
    }

    /// <summary>Segments in address order.</summary>
    public IReadOnlyList<FirmwareSegment> Segments => _segments;

    /// <summary>Entry point (Intel HEX start address, ELF e_entry), if known.</summary>
    public uint? EntryPoint { get; }

    /// <summary>Bytes of data (gaps not counted).</summary>
    public long Size => _segments.Sum(s => (long)s.Data.Length);

    /// <summary>Lowest address.</summary>
    public uint Start => _segments.Count > 0 ? _segments[0].Address : 0;

    /// <summary>One past the highest address.</summary>
    public ulong End => _segments.Count > 0 ? _segments[^1].End : 0;

    /// <summary>A raw binary at <paramref name="address"/>.</summary>
    public static FirmwareImage FromBinary(byte[] data, uint address) => new([new FirmwareSegment(address, data)]);

    /// <summary>The image as one block from <see cref="Start"/>, gaps filled with <paramref name="fill"/> (0xFF = erased flash).</summary>
    public byte[] Flatten(byte fill = 0xFF)
    {
        if (_segments.Count == 0) return [];
        var total = End - Start;
        if (total > 64 * 1024 * 1024) throw new InvalidOperationException($"The image spans {total} bytes; flatten refuses more than 64 MiB.");
        var b = new byte[total];
        b.AsSpan().Fill(fill);
        foreach (var s in _segments) s.Data.CopyTo(b, (int)(s.Address - Start));
        return b;
    }

    /// <summary>Finds an ASCII marker such as <c>FWVER:</c> and returns the text after it up to a NUL or non-printable byte.</summary>
    public string? FindTag(string marker)
    {
        var m = Encoding.ASCII.GetBytes(marker);
        foreach (var s in _segments)
        {
            var at = s.Data.AsSpan().IndexOf(m);
            if (at < 0) continue;
            var end = at + m.Length;
            while (end < s.Data.Length && s.Data[end] is >= 0x20 and < 0x7F) end++;
            return Encoding.ASCII.GetString(s.Data, at + m.Length, end - at - m.Length);
        }

        return null;
    }

    /// <summary>Loads a file by extension (.hex/.ihex, .dfu, .elf/.axf/.out, else raw binary at <paramref name="binaryAddress"/>), or by content.</summary>
    public static FirmwareImage Load(string path, uint binaryAddress = 0x08000000)
    {
        var bytes = File.ReadAllBytes(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".hex" or ".ihex" => IntelHex.Parse(Encoding.ASCII.GetString(bytes)),
            ".dfu" => DfuFile.Parse(bytes).Image,
            ".elf" or ".axf" or ".out" => Elf.Parse(bytes),
            _ when bytes.Length >= 4 && bytes[0] == 0x7F && bytes[1] == (byte)'E' && bytes[2] == (byte)'L' && bytes[3] == (byte)'F' => Elf.Parse(bytes),
            _ when bytes.Length > 0 && bytes[0] == (byte)':' => IntelHex.Parse(Encoding.ASCII.GetString(bytes)),
            _ => FromBinary(bytes, binaryAddress),
        };
    }

    /// <summary>Normalised description: <c>ADDR:LEN:CRC32</c> per segment and the entry point.</summary>
    public string Describe() =>
        $"segments={string.Join(',', _segments.Select(s => string.Create(CultureInfo.InvariantCulture, $"{s.Address:X8}:{s.Data.Length}:{Crc32.Compute(s.Data):X8}")))} entry={(EntryPoint is { } e ? e.ToString("X8", CultureInfo.InvariantCulture) : "-")}";

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{_segments.Count} segment(s), {Size} bytes, 0x{Start:X8}–0x{End:X8}");
}

/// <summary>CRC-32 (IEEE 802.3, reflected 0xEDB88320).</summary>
public static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }

        return t;
    }

    /// <summary>Running CRC without the final inversion (start with 0xFFFFFFFF), as the DFU suffix stores it.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>Standard CRC-32 (check value of "123456789" is 0xCBF43926).</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => ~Update(0xFFFFFFFF, data);
}

/// <summary>Intel HEX (I8HEX/I16HEX/I32HEX records 00–05).</summary>
public static class IntelHex
{
    /// <summary>Parses Intel HEX text.</summary>
    /// <exception cref="ProtocolException">Bad record, checksum, missing end-of-file record, overlapping data.</exception>
    public static FirmwareImage Parse(string text)
    {
        var segments = new List<FirmwareSegment>();
        uint upper = 0;
        uint? entry = null;
        var current = new List<byte>();
        uint currentStart = 0;
        var eof = false;
        var line = 0;
        foreach (var raw in text.Split('\n'))
        {
            line++;
            var r = raw.Trim();
            if (r.Length == 0) continue;
            if (eof) throw new ProtocolException($"Line {line}: data after the end-of-file record.");
            if (r[0] != ':' || r.Length < 11 || r.Length % 2 == 0) throw new ProtocolException($"Line {line}: not an Intel HEX record.");
            byte[] b;
            try
            {
                b = Convert.FromHexString(r.AsSpan(1));
            }
            catch (FormatException)
            {
                throw new ProtocolException($"Line {line}: not hexadecimal.");
            }

            var count = b[0];
            if (b.Length != count + 5) throw new ProtocolException($"Line {line}: length {count} does not match the record.");
            byte sum = 0;
            foreach (var x in b) sum += x;
            if (sum != 0) throw new ProtocolException($"Line {line}: checksum mismatch.");
            var offset = (uint)((b[1] << 8) | b[2]);
            var data = b.AsSpan(4, count);
            switch (b[3])
            {
                case 0x00:
                    var address = upper + offset;
                    if (current.Count > 0 && address != currentStart + (uint)current.Count)
                    {
                        segments.Add(new FirmwareSegment(currentStart, [.. current]));
                        current.Clear();
                    }

                    if (current.Count == 0) currentStart = address;
                    current.AddRange(data.ToArray());
                    break;
                case 0x01:
                    eof = true;
                    break;
                case 0x02 when count == 2:
                    upper = (uint)((data[0] << 8) | data[1]) << 4;
                    break;
                case 0x04 when count == 2:
                    upper = (uint)((data[0] << 8) | data[1]) << 16;
                    break;
                case 0x03 when count == 4:
                    entry = (uint)((((data[0] << 8) | data[1]) << 4) + ((data[2] << 8) | data[3]));
                    break;
                case 0x05 when count == 4:
                    entry = BinaryPrimitives.ReadUInt32BigEndian(data);
                    break;
                default:
                    throw new ProtocolException($"Line {line}: record type 0x{b[3]:X2} with {count} bytes is not valid.");
            }
        }

        if (!eof) throw new ProtocolException("Missing end-of-file record (:00000001FF).");
        if (current.Count > 0) segments.Add(new FirmwareSegment(currentStart, [.. current]));
        return new FirmwareImage(segments, entry);
    }

    /// <summary>Formats an image as Intel HEX (I32HEX, 16 data bytes per record).</summary>
    public static string Format(FirmwareImage image, int bytesPerLine = 16)
    {
        var sb = new StringBuilder();
        void Record(byte type, ushort offset, ReadOnlySpan<byte> data)
        {
            Span<byte> b = stackalloc byte[data.Length + 5];
            b[0] = (byte)data.Length;
            b[1] = (byte)(offset >> 8);
            b[2] = (byte)offset;
            b[3] = type;
            data.CopyTo(b[4..]);
            byte sum = 0;
            foreach (var x in b[..^1]) sum += x;
            b[^1] = (byte)(0x100 - sum);
            sb.Append(':').Append(Convert.ToHexString(b)).Append('\n');
        }

        uint? upper = null;
        foreach (var s in image.Segments)
        {
            for (var i = 0; i < s.Data.Length;)
            {
                var address = s.Address + (uint)i;
                if (address >> 16 != upper)
                {
                    upper = address >> 16;
                    Record(0x04, 0, [(byte)(upper >> 8), (byte)upper]);
                }

                var n = Math.Min(bytesPerLine, Math.Min(s.Data.Length - i, 0x10000 - (int)(address & 0xFFFF)));
                Record(0x00, (ushort)address, s.Data.AsSpan(i, n));
                i += n;
            }
        }

        if (image.EntryPoint is { } e)
        {
            Span<byte> d = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(d, e);
            Record(0x05, 0, d);
        }

        Record(0x01, 0, []);
        return sb.ToString();
    }
}

/// <summary>A DFU file: payload plus the 16-byte DFU suffix; DfuSe payloads carry targets and elements.</summary>
/// <param name="VendorId">idVendor (0xFFFF = any).</param>
/// <param name="ProductId">idProduct (0xFFFF = any).</param>
/// <param name="DeviceVersion">bcdDevice (0xFFFF = any).</param>
/// <param name="DfuVersion">bcdDFU: 0x0100 plain, 0x011A DfuSe.</param>
/// <param name="Image">Firmware (a plain payload is placed at address 0).</param>
/// <param name="Targets">DfuSe targets: alternate setting and name (empty for plain files).</param>
public sealed record DfuFile(ushort VendorId, ushort ProductId, ushort DeviceVersion, ushort DfuVersion, FirmwareImage Image, IReadOnlyList<(byte Alternate, string Name)> Targets)
{
    /// <summary>Suffix length.</summary>
    public const int SuffixLength = 16;

    /// <summary>True for a DfuSe file.</summary>
    public bool IsDfuSe => Targets.Count > 0;

    /// <summary>Parses a DFU file and verifies the suffix CRC.</summary>
    /// <exception cref="ProtocolException">No suffix, CRC mismatch, malformed DfuSe prefix.</exception>
    public static DfuFile Parse(ReadOnlySpan<byte> file)
    {
        if (file.Length < SuffixLength) throw new ProtocolException("Too short for a DFU suffix.");
        var suffix = file[^SuffixLength..];
        if (suffix[8] != (byte)'U' || suffix[9] != (byte)'F' || suffix[10] != (byte)'D') throw new ProtocolException("No DFU suffix (signature UFD).");
        if (suffix[11] != SuffixLength) throw new ProtocolException($"Unsupported DFU suffix length {suffix[11]}.");
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(suffix[12..]);
        var crc = Crc32.Update(0xFFFFFFFF, file[..^4]);
        if (crc != stored) throw new ProtocolException($"DFU suffix CRC mismatch (stored 0x{stored:X8}, computed 0x{crc:X8}).");
        var payload = file[..^SuffixLength];
        var bcdDevice = BinaryPrimitives.ReadUInt16LittleEndian(suffix);
        var pid = BinaryPrimitives.ReadUInt16LittleEndian(suffix[2..]);
        var vid = BinaryPrimitives.ReadUInt16LittleEndian(suffix[4..]);
        var dfu = BinaryPrimitives.ReadUInt16LittleEndian(suffix[6..]);
        if (payload.Length < 11 || !payload[..5].SequenceEqual("DfuSe"u8))
            return new DfuFile(vid, pid, bcdDevice, dfu, FirmwareImage.FromBinary(payload.ToArray(), 0), []);
        if (payload[5] != 1) throw new ProtocolException($"DfuSe version {payload[5]} is not supported.");
        var imageSize = BinaryPrimitives.ReadUInt32LittleEndian(payload[6..]);
        if (imageSize != payload.Length) throw new ProtocolException($"DfuSe image size {imageSize} does not match the payload ({payload.Length}).");
        var targets = new List<(byte, string)>();
        var segments = new List<FirmwareSegment>();
        var at = 11;
        for (var t = 0; t < payload[10]; t++)
        {
            if (payload.Length - at < 274 || !payload.Slice(at, 6).SequenceEqual("Target"u8)) throw new ProtocolException($"DfuSe target {t} is malformed.");
            var alt = payload[at + 6];
            var named = BinaryPrimitives.ReadUInt32LittleEndian(payload[(at + 7)..]) != 0;
            var nameBytes = payload.Slice(at + 11, 255);
            var nul = nameBytes.IndexOf((byte)0);
            var name = named ? Encoding.ASCII.GetString(nul >= 0 ? nameBytes[..nul] : nameBytes) : "";
            var targetSize = BinaryPrimitives.ReadUInt32LittleEndian(payload[(at + 266)..]);
            var elements = BinaryPrimitives.ReadUInt32LittleEndian(payload[(at + 270)..]);
            at += 274;
            var targetStart = at;
            for (var e = 0; e < elements; e++)
            {
                if (payload.Length - at < 8) throw new ProtocolException($"DfuSe element {e} of target {t} is truncated.");
                var address = BinaryPrimitives.ReadUInt32LittleEndian(payload[at..]);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(payload[(at + 4)..]);
                if (size > payload.Length - at - 8) throw new ProtocolException($"DfuSe element {e} of target {t} announces {size} bytes past the end.");
                segments.Add(new FirmwareSegment(address, payload.Slice(at + 8, (int)size).ToArray()));
                at += 8 + (int)size;
            }

            if (at - targetStart != targetSize) throw new ProtocolException($"DfuSe target {t} size {targetSize} does not match its elements ({at - targetStart}).");
            targets.Add((alt, name));
        }

        if (at != payload.Length) throw new ProtocolException($"{payload.Length - at} bytes follow the last DfuSe target.");
        return new DfuFile(vid, pid, bcdDevice, dfu, new FirmwareImage(segments), targets);
    }

    /// <summary>Builds a DfuSe file with one target (alternate 0, "Internal Flash").</summary>
    public static byte[] BuildDfuSe(FirmwareImage image, ushort vendorId, ushort productId, ushort deviceVersion = 0xFFFF, string targetName = "Internal Flash")
    {
        var elements = new List<byte>();
        foreach (var s in image.Segments)
        {
            elements.AddRange(BitConverter.GetBytes(s.Address));
            elements.AddRange(BitConverter.GetBytes(s.Data.Length));
            elements.AddRange(s.Data);
        }

        var target = new byte[274];
        "Target"u8.CopyTo(target);
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(7), 1);
        Encoding.ASCII.GetBytes(targetName).AsSpan(0, Math.Min(254, targetName.Length)).CopyTo(target.AsSpan(11));
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(266), (uint)elements.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(target.AsSpan(270), (uint)image.Segments.Count);
        var body = new List<byte>();
        body.AddRange("DfuSe"u8.ToArray());
        body.Add(1);
        body.AddRange(new byte[4]);
        body.Add(1);
        body.AddRange(target);
        body.AddRange(elements);
        var payload = body.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(6), (uint)payload.Length);
        return AddSuffix(payload, vendorId, productId, deviceVersion, 0x011A);
    }

    /// <summary>Appends a DFU suffix (with CRC) to a payload.</summary>
    public static byte[] AddSuffix(ReadOnlySpan<byte> payload, ushort vendorId, ushort productId, ushort deviceVersion = 0xFFFF, ushort dfuVersion = 0x0100)
    {
        var file = new byte[payload.Length + SuffixLength];
        payload.CopyTo(file);
        var s = file.AsSpan(payload.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(s, deviceVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(s[2..], productId);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], vendorId);
        BinaryPrimitives.WriteUInt16LittleEndian(s[6..], dfuVersion);
        s[8] = (byte)'U';
        s[9] = (byte)'F';
        s[10] = (byte)'D';
        s[11] = SuffixLength;
        BinaryPrimitives.WriteUInt32LittleEndian(s[12..], Crc32.Update(0xFFFFFFFF, file.AsSpan(0, file.Length - 4)));
        return file;
    }

    /// <summary>Normalised description.</summary>
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"vid={VendorId:X4} pid={ProductId:X4} device={DeviceVersion:X4} dfu={DfuVersion:X4} targets={string.Join(',', Targets.Select(t => $"{t.Alternate}:{t.Name}"))} {Image.Describe()}");
}

/// <summary>Loads PT_LOAD segments of little-endian ELF32 executables (ARM Cortex-M, RISC-V 32).</summary>
public static class Elf
{
    /// <summary>Parses an ELF file.</summary>
    /// <exception cref="ProtocolException">Not ELF32 little-endian, or headers out of range.</exception>
    public static FirmwareImage Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 52 || b[0] != 0x7F || b[1] != (byte)'E' || b[2] != (byte)'L' || b[3] != (byte)'F') throw new ProtocolException("Not an ELF file.");
        if (b[4] != 1 || b[5] != 1) throw new ProtocolException("Only little-endian ELF32 files are supported.");
        var entry = BinaryPrimitives.ReadUInt32LittleEndian(b[24..]);
        var phoff = BinaryPrimitives.ReadUInt32LittleEndian(b[28..]);
        var phentsize = BinaryPrimitives.ReadUInt16LittleEndian(b[42..]);
        var phnum = BinaryPrimitives.ReadUInt16LittleEndian(b[44..]);
        if (phentsize < 32 || phoff + ((long)phentsize * phnum) > b.Length) throw new ProtocolException("ELF program headers run past the end.");
        var segments = new List<FirmwareSegment>();
        for (var i = 0; i < phnum; i++)
        {
            var ph = b.Slice((int)phoff + (i * phentsize), 32);
            if (BinaryPrimitives.ReadUInt32LittleEndian(ph) != 1) continue;   // PT_LOAD
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(ph[4..]);
            var paddr = BinaryPrimitives.ReadUInt32LittleEndian(ph[12..]);
            var filesz = BinaryPrimitives.ReadUInt32LittleEndian(ph[16..]);
            if (filesz == 0) continue;
            if (offset + (long)filesz > b.Length) throw new ProtocolException($"ELF segment {i} runs past the end.");
            segments.Add(new FirmwareSegment(paddr, b.Slice((int)offset, (int)filesz).ToArray()));
        }

        return new FirmwareImage(segments, entry);
    }
}
