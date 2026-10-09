namespace IoTCom.Net.Protocols.Nfc;

/// <summary>
/// A simulated NTAG21x tag: full memory (UID pages with check bytes, capability container, data area, configuration
/// pages) that answers the PC/SC storage-card APDUs like a contactless reader would.
/// </summary>
public sealed class VirtualType2Tag
{
    private readonly Lock _gate = new();

    /// <summary>Creates a formatted tag, optionally with an NDEF message.</summary>
    public VirtualType2Tag(Type2TagKind kind, ReadOnlySpan<byte> uid, NdefMessage? message = null, bool writable = true, string? label = null)
    {
        Kind = kind;
        Uid = uid.ToArray();
        Label = label ?? $"{kind} {Convert.ToHexString(uid)}";
        Memory = new byte[Type2Tag.Pages(kind) * Type2Tag.PageSize];
        Type2Tag.UidPages(uid).CopyTo(Memory, 0);
        Type2Tag.CapabilityContainer(kind, writable).CopyTo(Memory, 12);
        if (message is not null) Type2Tag.FormatDataArea(message, Type2Tag.DataAreaSize(kind)).CopyTo(Memory, 16);
        else (Memory[16], Memory[17], Memory[18]) = (0x03, 0x00, 0xFE);   // a blank NDEF tag: empty NDEF TLV, terminator
    }

    /// <summary>Product.</summary>
    public Type2TagKind Kind { get; }

    /// <summary>7-byte UID.</summary>
    public byte[] Uid { get; }

    /// <summary>Display name for demos.</summary>
    public string Label { get; }

    /// <summary>The raw memory (page 0 onwards).</summary>
    public byte[] Memory { get; }

    /// <summary>Pages written so far.</summary>
    public int PagesWritten { get; private set; }

    /// <summary>A copy of the memory.</summary>
    public byte[] Snapshot()
    {
        lock (_gate) return [.. Memory];
    }

    /// <summary>Answers one APDU (GET DATA, READ BINARY, UPDATE BINARY).</summary>
    public byte[] Process(ReadOnlySpan<byte> apdu)
    {
        lock (_gate)
        {
            if (apdu.Length < 5 || apdu[0] != 0xFF) return [0x6E, 0x00];   // class not supported
            var pages = Memory.Length / 4;
            switch (apdu[1])
            {
                case 0xCA when apdu[2] == 0x00:
                    return [.. Uid, 0x90, 0x00];
                case 0xB0:
                {
                    var page = apdu[3];
                    if (page >= pages) return [0x6A, 0x82];
                    var r = new byte[18];
                    for (var i = 0; i < 16; i++) r[i] = Memory[((page * 4) + i) % Memory.Length];   // NTAG rolls over at the end
                    (r[16], r[17]) = ((byte)0x90, (byte)0x00);
                    return r;
                }

                case 0xD6:
                {
                    var page = apdu[3];
                    if (apdu.Length != 9 || apdu[4] != 4) return [0x67, 0x00];
                    if (page < 3 || page >= pages) return [0x6A, 0x82];
                    if (page == 3)
                    {
                        // The capability container is one-time programmable: bits can only be set.
                        for (var i = 0; i < 4; i++) Memory[12 + i] |= apdu[5 + i];
                    }
                    else
                    {
                        apdu.Slice(5, 4).CopyTo(Memory.AsSpan(page * 4));
                    }

                    PagesWritten++;
                    return [0x90, 0x00];
                }

                default:
                    return [0x6D, 0x00];   // instruction not supported
            }
        }
    }
}

/// <summary>A simulated contactless reader: present a <see cref="VirtualType2Tag"/>, take it away, and read it through the same APDUs as a real reader.</summary>
public sealed class VirtualNfcReader : INfcReader
{
    private readonly Lock _gate = new();
    private VirtualType2Tag? _tag;
    private TaskCompletionSource _present = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _generation;

    /// <summary>Creates a reader.</summary>
    public VirtualNfcReader(string name = "IoTCom Virtual NFC Reader 0") => Name = name;

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>The tag on the reader, if any.</summary>
    public VirtualType2Tag? Tag
    {
        get
        {
            lock (_gate) return _tag;
        }
    }

    /// <summary>Puts a tag on the reader.</summary>
    public void Present(VirtualType2Tag tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        lock (_gate)
        {
            _tag = tag;
            _generation++;
            _present.TrySetResult();
        }
    }

    /// <summary>Takes the tag away.</summary>
    public void Remove()
    {
        lock (_gate)
        {
            _tag = null;
            _generation++;
            if (_present.Task.IsCompleted) _present = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ISmartCardChannel> WaitForTagAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_tag is not null) return new Channel(this, _tag, _generation);
                wait = _present.Task;
            }

            await wait.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Channel(VirtualNfcReader reader, VirtualType2Tag tag, int generation) : ISmartCardChannel
    {
        public string Reader => reader.Name;

        // ATR of a PC/SC contactless storage card (PC/SC part 3), NFC Forum Type 2 / NTAG.
        public ReadOnlyMemory<byte> Atr { get; } = new byte[] { 0x3B, 0x8F, 0x80, 0x01, 0x80, 0x4F, 0x0C, 0xA0, 0x00, 0x00, 0x03, 0x06, 0x03, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x68 };

        public ValueTask<byte[]> TransmitAsync(ReadOnlyMemory<byte> apdu, CancellationToken ct = default)
        {
            lock (reader._gate)
            {
                if (reader._generation != generation || reader._tag != tag) throw new TransportException("The tag was removed from the reader.");
            }

            return ValueTask.FromResult(tag.Process(apdu.Span));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
