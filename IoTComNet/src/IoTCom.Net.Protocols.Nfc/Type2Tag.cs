using System.Globalization;

namespace IoTCom.Net.Protocols.Nfc;

/// <summary>NFC Forum Type 2 tag products with their memory sizes.</summary>
public enum Type2TagKind
{
    /// <summary>NTAG213: 45 pages, 144-byte data area.</summary>
    Ntag213,
    /// <summary>NTAG215: 135 pages, 496-byte data area.</summary>
    Ntag215,
    /// <summary>NTAG216: 231 pages, 872-byte data area.</summary>
    Ntag216,
}

/// <summary>A region of Type 2 tag memory, for the page map.</summary>
public enum Type2Region
{
    /// <summary>Serial number and check bytes (pages 0–2).</summary>
    Uid,
    /// <summary>Static lock bytes (page 2, bytes 2–3).</summary>
    Lock,
    /// <summary>Capability container (page 3).</summary>
    CapabilityContainer,
    /// <summary>A TLV header (type and length).</summary>
    TlvHeader,
    /// <summary>NDEF message bytes.</summary>
    Ndef,
    /// <summary>Other TLV content (lock control, memory control, proprietary).</summary>
    OtherTlv,
    /// <summary>Terminator TLV (0xFE).</summary>
    Terminator,
    /// <summary>Unused data area.</summary>
    Free,
    /// <summary>Dynamic lock and configuration pages after the data area.</summary>
    Configuration,
}

/// <summary>Type 2 tag memory layout (NFC Forum Type 2 Tag specification): pages of four bytes, data area from page 4.</summary>
public static class Type2Tag
{
    /// <summary>Bytes per page.</summary>
    public const int PageSize = 4;

    /// <summary>Total pages of a product.</summary>
    public static int Pages(Type2TagKind kind) => kind switch { Type2TagKind.Ntag213 => 45, Type2TagKind.Ntag215 => 135, _ => 231 };

    /// <summary>Data area size in bytes (capability container byte 2 × 8).</summary>
    public static int DataAreaSize(Type2TagKind kind) => kind switch { Type2TagKind.Ntag213 => 144, Type2TagKind.Ntag215 => 496, _ => 872 };

    /// <summary>Capability container for a product: magic 0xE1, version 1.0, size/8, read/write access.</summary>
    public static byte[] CapabilityContainer(Type2TagKind kind, bool writable = true) => [0xE1, 0x10, (byte)(DataAreaSize(kind) / 8), (byte)(writable ? 0x00 : 0x0F)];

    /// <summary>Builds pages 0–2 for a 7-byte UID (with the two BCC check bytes, zero lock bytes).</summary>
    public static byte[] UidPages(ReadOnlySpan<byte> uid)
    {
        if (uid.Length != 7) throw new ArgumentException("Type 2 tags have a 7-byte UID.", nameof(uid));
        return
        [
            uid[0], uid[1], uid[2], (byte)(0x88 ^ uid[0] ^ uid[1] ^ uid[2]),
            uid[3], uid[4], uid[5], uid[6],
            (byte)(uid[3] ^ uid[4] ^ uid[5] ^ uid[6]), 0x48, 0x00, 0x00,
        ];
    }

    /// <summary>The NDEF TLV, terminator and zero padding for a data area of <paramref name="dataAreaSize"/> bytes.</summary>
    /// <exception cref="ArgumentException">The message does not fit.</exception>
    public static byte[] FormatDataArea(NdefMessage message, int dataAreaSize)
    {
        ArgumentNullException.ThrowIfNull(message);
        var ndef = message.Encode();
        var header = ndef.Length < 0xFF ? new byte[] { 0x03, (byte)ndef.Length } : [0x03, 0xFF, (byte)(ndef.Length >> 8), (byte)ndef.Length];
        var needed = header.Length + ndef.Length + 1;
        if (needed > dataAreaSize) throw new ArgumentException($"The NDEF message needs {needed} bytes; the tag holds {dataAreaSize}.", nameof(message));
        var area = new byte[dataAreaSize];
        header.CopyTo(area, 0);
        ndef.CopyTo(area, header.Length);
        area[header.Length + ndef.Length] = 0xFE;
        return area;
    }

    /// <summary>Finds the NDEF message in a data area (TLVs from page 4); null when the tag holds no NDEF TLV.</summary>
    /// <exception cref="ProtocolException">Malformed TLVs or NDEF.</exception>
    public static NdefMessage? ReadNdef(ReadOnlySpan<byte> dataArea)
    {
        foreach (var (type, start, length, valueStart) in Tlvs(dataArea))
        {
            if (type == 0x03) return length == 0 ? new NdefMessage([]) : NdefMessage.Parse(dataArea.Slice(valueStart, length));
            if (type == 0xFE) break;
            _ = start;
        }

        return null;
    }

    private static List<(byte Type, int Start, int Length, int ValueStart)> Tlvs(ReadOnlySpan<byte> d)
    {
        var list = new List<(byte, int, int, int)>();
        var p = 0;
        while (p < d.Length)
        {
            var t = d[p];
            if (t == 0x00)
            {
                p++;
                continue;
            }

            if (t == 0xFE)
            {
                list.Add((t, p, 0, p + 1));
                break;
            }

            if (p + 1 >= d.Length) throw new ProtocolException("Truncated TLV.");
            int length = d[p + 1], header = 2;
            if (length == 0xFF)
            {
                if (p + 3 >= d.Length) throw new ProtocolException("Truncated 3-byte TLV length.");
                length = (d[p + 2] << 8) | d[p + 3];
                header = 4;
            }

            if (p + header + length > d.Length) throw new ProtocolException($"TLV 0x{t:X2} of {length} bytes runs past the data area.");
            list.Add((t, p, length, p + header));
            p += header + length;
        }

        return list;
    }

    /// <summary>Classifies every byte of a full memory dump (from page 0) for the page map.</summary>
    public static Type2Region[] Map(ReadOnlySpan<byte> memory, int dataAreaSize)
    {
        var map = new Type2Region[memory.Length];
        for (var i = 0; i < map.Length; i++)
            map[i] = i < 10 ? Type2Region.Uid : i < 12 ? Type2Region.Lock : i < 16 ? Type2Region.CapabilityContainer : i < 16 + dataAreaSize ? Type2Region.Free : Type2Region.Configuration;
        if (memory.Length < 16) return map;
        try
        {
            var area = memory.Slice(16, Math.Min(dataAreaSize, memory.Length - 16));
            foreach (var (type, start, length, valueStart) in Tlvs(area))
            {
                var headerLength = valueStart - start;
                for (var i = 0; i < headerLength; i++) map[16 + start + i] = type == 0xFE ? Type2Region.Terminator : Type2Region.TlvHeader;
                for (var i = 0; i < length; i++) map[16 + valueStart + i] = type == 0x03 ? Type2Region.Ndef : Type2Region.OtherTlv;
            }
        }
        catch (ProtocolException)
        {
        }

        return map;
    }
}

/// <summary>A smart card (or contactless tag) connection: sends an APDU, returns the response including SW1 SW2.</summary>
public interface ISmartCardChannel : IAsyncDisposable
{
    /// <summary>Reader name.</summary>
    string Reader { get; }

    /// <summary>Answer to reset (ATR) reported by the reader.</summary>
    ReadOnlyMemory<byte> Atr { get; }

    /// <summary>Exchanges one APDU.</summary>
    ValueTask<byte[]> TransmitAsync(ReadOnlyMemory<byte> apdu, CancellationToken ct = default);
}

/// <summary>A reader that can wait for a card or tag.</summary>
public interface INfcReader : IAsyncDisposable
{
    /// <summary>Reader name.</summary>
    string Name { get; }

    /// <summary>Waits until a tag is present and connects to it.</summary>
    ValueTask<ISmartCardChannel> WaitForTagAsync(CancellationToken ct = default);
}

/// <summary>Options for <see cref="Type2TagClient"/>.</summary>
public sealed class NfcTagOptions
{
    /// <summary>Refuse writes (default true). Call <see cref="AllowWrites"/> to write NDEF messages.</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>Allows writes.</summary>
    public NfcTagOptions AllowWrites()
    {
        ReadOnly = false;
        return this;
    }
}

/// <summary>
/// Reads and writes NFC Forum Type 2 tags (NTAG21x, MIFARE Ultralight) through the PC/SC storage-card commands that
/// contactless readers such as the ACR122U and ACR1252U implement: GET DATA (UID), READ BINARY (four pages) and UPDATE
/// BINARY (one page). Writes are refused unless allowed and never touch the UID, lock or configuration pages.
/// </summary>
public sealed class Type2TagClient
{
    private readonly ISmartCardChannel _card;
    private readonly NfcTagOptions _options;

    /// <summary>Creates a client on a connected card.</summary>
    public Type2TagClient(ISmartCardChannel card, NfcTagOptions? options = null) => (_card, _options) = (card, options ?? new NfcTagOptions());

    private static byte[] Check(byte[] response, string what)
    {
        if (response.Length < 2) throw new ProtocolException($"{what}: response shorter than the status word.");
        int sw1 = response[^2], sw2 = response[^1];
        if (sw1 != 0x90 || sw2 != 0x00) throw new DeviceException(string.Create(CultureInfo.InvariantCulture, $"{what} failed: SW {sw1:X2}{sw2:X2}."), (sw1 << 8) | sw2);
        return response[..^2];
    }

    /// <summary>Reads the UID (PC/SC GET DATA FF CA 00 00 00).</summary>
    public async Task<byte[]> GetUidAsync(CancellationToken ct = default) =>
        Check(await _card.TransmitAsync(new byte[] { 0xFF, 0xCA, 0x00, 0x00, 0x00 }, ct).ConfigureAwait(false), "GET DATA (UID)");

    /// <summary>Reads four pages (16 bytes) from <paramref name="page"/> (READ BINARY FF B0 00 page 10).</summary>
    public async Task<byte[]> ReadPagesAsync(byte page, CancellationToken ct = default)
    {
        var data = Check(await _card.TransmitAsync(new byte[] { 0xFF, 0xB0, 0x00, page, 0x10 }, ct).ConfigureAwait(false), $"READ BINARY page {page}");
        if (data.Length != 16) throw new ProtocolException($"READ BINARY page {page} returned {data.Length} bytes instead of 16.");
        return data;
    }

    /// <summary>Reads the capability container and the whole data area; returns the CC and the data area bytes.</summary>
    public async Task<(byte[] Cc, byte[] Data)> ReadDataAreaAsync(CancellationToken ct = default)
    {
        var head = await ReadPagesAsync(0, ct).ConfigureAwait(false);
        var cc = head[12..16];
        if (cc[0] != 0xE1) throw new ProtocolException($"No NDEF capability container (CC0 = 0x{cc[0]:X2}); the tag is not NDEF formatted.");
        var size = cc[2] * 8;
        var data = new byte[size];
        for (var offset = 0; offset < size; offset += 16)
        {
            var block = await ReadPagesAsync((byte)(4 + (offset / 4)), ct).ConfigureAwait(false);
            block.AsSpan(0, Math.Min(16, size - offset)).CopyTo(data.AsSpan(offset));
        }

        return (cc, data);
    }

    /// <summary>Reads the NDEF message; null when the tag holds none.</summary>
    public async Task<NdefMessage?> ReadNdefAsync(CancellationToken ct = default)
    {
        var (_, data) = await ReadDataAreaAsync(ct).ConfigureAwait(false);
        return Type2Tag.ReadNdef(data);
    }

    /// <summary>Reads the whole memory (header pages, data area and up to <paramref name="extraPages"/> configuration pages).</summary>
    public async Task<byte[]> DumpAsync(int extraPages = 5, CancellationToken ct = default)
    {
        var (cc, _) = await ReadDataAreaAsync(ct).ConfigureAwait(false);
        var pages = 4 + (cc[2] * 2) + extraPages;
        var memory = new List<byte>();
        for (var page = 0; page < pages; page += 4)
        {
            try
            {
                memory.AddRange(await ReadPagesAsync((byte)page, ct).ConfigureAwait(false));
            }
            catch (DeviceException)
            {
                break;   // past the end of this product
            }
        }

        return [.. memory.Take(pages * 4)];
    }

    /// <summary>Writes one page (UPDATE BINARY FF D6 00 page 04 data).</summary>
    /// <exception cref="ReadOnlyModeException">Writes are not allowed.</exception>
    public async Task WritePageAsync(byte page, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_options.ReadOnly) throw new ReadOnlyModeException("Refusing to write the NFC tag: writes are off (NfcTagOptions.AllowWrites()).");
        if (data.Length != 4) throw new ArgumentException("A page is four bytes.", nameof(data));
        if (page < 4) throw new ArgumentOutOfRangeException(nameof(page), "Pages 0–3 hold the UID, lock bytes and capability container; they are never written.");
        Check(await _card.TransmitAsync((byte[])[0xFF, 0xD6, 0x00, page, 0x04, .. data.Span], ct).ConfigureAwait(false), $"UPDATE BINARY page {page}");
    }

    /// <summary>Writes an NDEF message into the data area (only the pages that change).</summary>
    /// <exception cref="ReadOnlyModeException">Writes are not allowed.</exception>
    /// <exception cref="DeviceException">The tag's capability container says it is read-only.</exception>
    public async Task WriteNdefAsync(NdefMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (_options.ReadOnly) throw new ReadOnlyModeException("Refusing to write the NFC tag: writes are off (NfcTagOptions.AllowWrites()).");
        var (cc, current) = await ReadDataAreaAsync(ct).ConfigureAwait(false);
        if ((cc[3] & 0x0F) != 0) throw new DeviceException("The tag is read-only (capability container write access is not 0).");
        var area = Type2Tag.FormatDataArea(message, current.Length);
        var lastUsed = Array.FindLastIndex(area, b => b != 0);
        var lastOld = Array.FindLastIndex(current, b => b != 0);
        var end = Math.Max(lastUsed, lastOld) + 1;
        var firstChanges = !area.AsSpan(0, 4).SequenceEqual(current.AsSpan(0, 4));
        // A tag pulled away mid-write must not hold a half-written message: empty the NDEF TLV first, write the body,
        // then write the page with the TLV header last.
        if (firstChanges && end > 4) await WritePageAsync(4, new byte[] { 0x03, 0x00, 0xFE, 0x00 }, ct).ConfigureAwait(false);
        for (var offset = 4; offset < end; offset += 4)
        {
            if (area.AsSpan(offset, 4).SequenceEqual(current.AsSpan(offset, 4))) continue;
            await WritePageAsync((byte)(4 + (offset / 4)), area.AsMemory(offset, 4), ct).ConfigureAwait(false);
        }

        if (firstChanges) await WritePageAsync(4, area.AsMemory(0, 4), ct).ConfigureAwait(false);
    }
}
