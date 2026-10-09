using System.Runtime.InteropServices;
using System.Text;

namespace IoTCom.Net.Protocols.Nfc;

/// <summary>A PC/SC error with its SCARD_ code.</summary>
public sealed class PcscException : TransportException
{
    /// <summary>Creates the exception.</summary>
    public PcscException() { }

    /// <summary>Creates the exception with a message.</summary>
    public PcscException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public PcscException(string message, Exception? inner) : base(message, inner) { }

    /// <summary>Creates the exception for a PC/SC result.</summary>
    public PcscException(string operation, uint code) : base($"{operation}: {Pcsc.Describe(code)} (0x{code:X8}).") => Code = code;

    /// <summary>The SCARD_ result code.</summary>
    public uint Code { get; }
}

/// <summary>PC/SC (winscard on Windows, pcsc-lite on Linux, the PCSC framework on macOS): list readers and connect to cards.</summary>
public static partial class Pcsc
{
    /// <summary>No card in the reader.</summary>
    public const uint NoSmartCard = 0x8010000C;
    /// <summary>The card was removed.</summary>
    public const uint RemovedCard = 0x80100069;
    /// <summary>The card does not answer.</summary>
    public const uint UnresponsiveCard = 0x80100066;
    /// <summary>No readers are connected.</summary>
    public const uint NoReadersAvailable = 0x8010002E;
    /// <summary>The smart card service is not running.</summary>
    public const uint NoService = 0x8010001D;
    /// <summary>The smart card service stopped.</summary>
    public const uint ServiceStopped = 0x8010001E;

    private const uint ScopeUser = 0, ShareShared = 2, ProtocolT0 = 1, ProtocolT1 = 2, LeaveCard = 0;

    /// <summary>A readable name for a result code.</summary>
    public static string Describe(uint code) => code switch
    {
        NoSmartCard => "no card or tag on the reader",
        RemovedCard => "the card or tag was removed",
        UnresponsiveCard => "the card or tag does not answer",
        NoReadersAvailable => "no readers are connected",
        NoService or ServiceStopped => "the smart card service is not running",
        0x80100009 => "unknown reader",
        0x8010000A => "timeout",
        0x8010000B => "sharing violation (another application holds the card)",
        0x80100017 => "the reader is unavailable",
        _ => "PC/SC error",
    };

    private static bool IsLinux => OperatingSystem.IsLinux();

    private static bool IsMac => OperatingSystem.IsMacOS();

    // Normalises LONG (int on Windows/macOS, long on Linux) to the 32-bit code.
    private static uint Code(long result) => (uint)(result & 0xFFFF_FFFF);

    /// <summary>Lists reader names; empty when there are none or the service is not running.</summary>
    /// <exception cref="PcscException">Other PC/SC errors.</exception>
    public static IReadOnlyList<string> ListReaders()
    {
        using var context = PcscContext.TryEstablish(out var code);
        if (context is null)
        {
            if (code is NoService or ServiceStopped or NoReadersAvailable) return [];
            throw new PcscException("SCardEstablishContext", code);
        }

        return context.ListReaders();
    }

    internal sealed class PcscContext : IDisposable
    {
        private PcscContext(nint handle) => Handle = handle;

        public nint Handle { get; private set; }

        public static PcscContext? TryEstablish(out uint code)
        {
            try
            {
                return Establish(out code);
            }
            catch (DllNotFoundException ex)
            {
                throw new PcscException(IsLinux ? "PC/SC is not installed (install pcscd and libpcsclite1)." : "The PC/SC library could not be loaded.", ex);
            }
        }

        private static PcscContext? Establish(out uint code)
        {
            nint ctx;
            if (IsLinux)
            {
                code = Code(Linux.SCardEstablishContext(ScopeUser, 0, 0, out ctx));
            }
            else if (IsMac)
            {
                code = Code(Mac.SCardEstablishContext(ScopeUser, 0, 0, out var c32));
                ctx = c32;
            }
            else
            {
                code = Code(Windows.SCardEstablishContext(ScopeUser, 0, 0, out ctx));
            }

            return code == 0 ? new PcscContext(ctx) : null;
        }

        public IReadOnlyList<string> ListReaders()
        {
            uint code;
            string multi;
            if (IsLinux || IsMac)
            {
                nuint lengthL = 0;
                uint length32 = 0;
                code = IsLinux ? Code(Linux.SCardListReaders(Handle, 0, null, ref lengthL)) : Code(Mac.SCardListReaders((int)Handle, 0, null, ref length32));
                if (code == NoReadersAvailable) return [];
                if (code != 0) throw new PcscException("SCardListReaders", code);
                var buffer = new byte[IsLinux ? (int)lengthL : (int)length32];
                code = IsLinux ? Code(Linux.SCardListReaders(Handle, 0, buffer, ref lengthL)) : Code(Mac.SCardListReaders((int)Handle, 0, buffer, ref length32));
                if (code != 0) throw new PcscException("SCardListReaders", code);
                multi = Encoding.UTF8.GetString(buffer);
            }
            else
            {
                uint length = 0;
                code = Code(Windows.SCardListReadersW(Handle, 0, null, ref length));
                if (code == NoReadersAvailable) return [];
                if (code != 0) throw new PcscException("SCardListReaders", code);
                var buffer = new char[length];
                code = Code(Windows.SCardListReadersW(Handle, 0, buffer, ref length));
                if (code != 0) throw new PcscException("SCardListReaders", code);
                multi = new string(buffer);
            }

            return multi.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }

        public uint TryConnect(string reader, out nint card, out uint protocol)
        {
            uint code;
            if (IsLinux)
            {
                code = Code(Linux.SCardConnect(Handle, reader, ShareShared, ProtocolT0 | ProtocolT1, out card, out var p));
                protocol = (uint)p;
            }
            else if (IsMac)
            {
                code = Code(Mac.SCardConnect((int)Handle, reader, ShareShared, ProtocolT0 | ProtocolT1, out var c32, out protocol));
                card = c32;
            }
            else
            {
                code = Code(Windows.SCardConnectW(Handle, reader, ShareShared, ProtocolT0 | ProtocolT1, out card, out protocol));
            }

            return code;
        }

        public void Dispose()
        {
            if (Handle == 0) return;
            // Nothing useful can be done if releasing fails; the handle is dropped either way.
            _ = IsLinux ? Linux.SCardReleaseContext(Handle) : IsMac ? Mac.SCardReleaseContext((int)Handle) : Windows.SCardReleaseContext(Handle);
            Handle = 0;
        }
    }

    internal static byte[] Transmit(nint card, uint protocol, ReadOnlySpan<byte> apdu)
    {
        var receive = new byte[258];
        uint code;
        int length;
        if (IsLinux)
        {
            var pci = new LinuxIoRequest { Protocol = protocol, PciLength = (nuint)Marshal.SizeOf<LinuxIoRequest>() };
            nuint received = (nuint)receive.Length;
            code = Code(Linux.SCardTransmit(card, ref pci, apdu, (nuint)apdu.Length, 0, receive, ref received));
            length = (int)received;
        }
        else if (IsMac)
        {
            var pci = new IoRequest { Protocol = protocol, PciLength = (uint)Marshal.SizeOf<IoRequest>() };
            uint received = (uint)receive.Length;
            code = Code(Mac.SCardTransmit((int)card, ref pci, apdu, (uint)apdu.Length, 0, receive, ref received));
            length = (int)received;
        }
        else
        {
            var pci = new IoRequest { Protocol = protocol, PciLength = (uint)Marshal.SizeOf<IoRequest>() };
            uint received = (uint)receive.Length;
            code = Code(Windows.SCardTransmit(card, ref pci, apdu, (uint)apdu.Length, 0, receive, ref received));
            length = (int)received;
        }

        if (code != 0) throw new PcscException("SCardTransmit", code);
        return receive[..length];
    }

    internal static byte[] Atr(nint card)
    {
        // The reader name and ATR come from SCardStatus; only the ATR is needed.
        var atr = new byte[36];
        uint code;
        int length;
        if (IsLinux)
        {
            nuint nameLength = 0, state = 0, protocol = 0, atrLength = (nuint)atr.Length;
            code = Code(Linux.SCardStatus(card, null, ref nameLength, out state, out protocol, atr, ref atrLength));
            length = (int)atrLength;
        }
        else if (IsMac)
        {
            uint nameLength = 0, atrLength = (uint)atr.Length;
            code = Code(Mac.SCardStatus((int)card, null, ref nameLength, out _, out _, atr, ref atrLength));
            length = (int)atrLength;
        }
        else
        {
            uint nameLength = 0, atrLength = (uint)atr.Length;
            code = Code(Windows.SCardStatusW(card, null, ref nameLength, out _, out _, atr, ref atrLength));
            length = (int)atrLength;
        }

        return code == 0 ? atr[..length] : [];
    }

    internal static void Disconnect(nint card)
    {
        // A tag already taken away makes this fail; the handle is gone either way.
        _ = IsLinux ? Linux.SCardDisconnect(card, LeaveCard) : IsMac ? Mac.SCardDisconnect((int)card, LeaveCard) : Windows.SCardDisconnect(card, LeaveCard);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoRequest
    {
        public uint Protocol;
        public uint PciLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct LinuxIoRequest
    {
        public nuint Protocol;
        public nuint PciLength;
    }

    private static partial class Windows
    {
        private const string Lib = "winscard.dll";

        [LibraryImport(Lib)]
        internal static partial int SCardEstablishContext(uint scope, nint reserved1, nint reserved2, out nint context);

        [LibraryImport(Lib)]
        internal static partial int SCardReleaseContext(nint context);

        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int SCardListReadersW(nint context, nint groups, [Out] char[]? readers, ref uint length);

        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int SCardConnectW(nint context, string reader, uint share, uint protocols, out nint card, out uint activeProtocol);

        [LibraryImport(Lib)]
        internal static partial int SCardTransmit(nint card, ref IoRequest sendPci, ReadOnlySpan<byte> send, uint sendLength, nint receivePci, [Out] byte[] receive, ref uint receiveLength);

        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int SCardStatusW(nint card, [Out] char[]? reader, ref uint readerLength, out uint state, out uint protocol, [Out] byte[] atr, ref uint atrLength);

        [LibraryImport(Lib)]
        internal static partial int SCardDisconnect(nint card, uint disposition);
    }

    private static partial class Linux
    {
        private const string Lib = "libpcsclite.so.1";

        [LibraryImport(Lib)]
        internal static partial nint SCardEstablishContext(nuint scope, nint reserved1, nint reserved2, out nint context);

        [LibraryImport(Lib)]
        internal static partial nint SCardReleaseContext(nint context);

        [LibraryImport(Lib)]
        internal static partial nint SCardListReaders(nint context, nint groups, [Out] byte[]? readers, ref nuint length);

        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint SCardConnect(nint context, string reader, nuint share, nuint protocols, out nint card, out nuint activeProtocol);

        [LibraryImport(Lib)]
        internal static partial nint SCardTransmit(nint card, ref LinuxIoRequest sendPci, ReadOnlySpan<byte> send, nuint sendLength, nint receivePci, [Out] byte[] receive, ref nuint receiveLength);

        [LibraryImport(Lib)]
        internal static partial nint SCardStatus(nint card, [Out] byte[]? reader, ref nuint readerLength, out nuint state, out nuint protocol, [Out] byte[] atr, ref nuint atrLength);

        [LibraryImport(Lib)]
        internal static partial nint SCardDisconnect(nint card, nuint disposition);
    }

    private static partial class Mac
    {
        private const string Lib = "/System/Library/Frameworks/PCSC.framework/PCSC";

        [LibraryImport(Lib)]
        internal static partial int SCardEstablishContext(uint scope, nint reserved1, nint reserved2, out int context);

        [LibraryImport(Lib)]
        internal static partial int SCardReleaseContext(int context);

        [LibraryImport(Lib)]
        internal static partial int SCardListReaders(int context, nint groups, [Out] byte[]? readers, ref uint length);

        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int SCardConnect(int context, string reader, uint share, uint protocols, out int card, out uint activeProtocol);

        [LibraryImport(Lib)]
        internal static partial int SCardTransmit(int card, ref IoRequest sendPci, ReadOnlySpan<byte> send, uint sendLength, nint receivePci, [Out] byte[] receive, ref uint receiveLength);

        [LibraryImport(Lib)]
        internal static partial int SCardStatus(int card, [Out] byte[]? reader, ref uint readerLength, out uint state, out uint protocol, [Out] byte[] atr, ref uint atrLength);

        [LibraryImport(Lib)]
        internal static partial int SCardDisconnect(int card, uint disposition);
    }
}

/// <summary>
/// A contactless reader through PC/SC (ACR122U, ACR1252U, Identiv uTrust, HID Omnikey 5x22…). It waits for a tag by
/// polling SCardConnect and exchanges APDUs with it; <see cref="Type2TagClient"/> reads and writes NDEF on top.
/// </summary>
public sealed class PcscNfcReader : INfcReader
{
    /// <summary>Uses the named reader.</summary>
    public PcscNfcReader(string name) => Name = name;

    private static readonly string[] ContactlessHints = ["PICC", "Contactless", "NFC", "ACR122", "ACR1252", "ACR1255", "5022", "5422", "uTrust 3700", "CL "];

    /// <summary>
    /// The first reader whose name contains <paramref name="match"/>; without a match, the first reader that looks
    /// contactless (PICC, ACR122, Omnikey 5x22…), so contact smart card readers and virtual smart cards are skipped.
    /// </summary>
    /// <exception cref="TransportException">No matching reader.</exception>
    public static PcscNfcReader Open(string? match = null)
    {
        var readers = Pcsc.ListReaders();
        var name = match is not null
            ? readers.FirstOrDefault(r => r.Contains(match, StringComparison.OrdinalIgnoreCase))
            : readers.FirstOrDefault(r => ContactlessHints.Any(h => r.Contains(h, StringComparison.OrdinalIgnoreCase)));
        return new PcscNfcReader(name ?? throw new TransportException(readers.Count == 0
            ? "No PC/SC readers are connected."
            : match is null
                ? $"No contactless reader found among: {string.Join(", ", readers)}. Name one with the reader option."
                : $"No PC/SC reader matches \"{match}\" ({string.Join(", ", readers)})."));
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>How often to poll for a tag (default 250 ms).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc />
    public async ValueTask<ISmartCardChannel> WaitForTagAsync(CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var context = Pcsc.PcscContext.TryEstablish(out var code) ?? throw new PcscException("SCardEstablishContext", code);
            code = context.TryConnect(Name, out var card, out var protocol);
            if (code == 0) return new Card(Name, context, card, protocol);
            context.Dispose();
            if (code is not (Pcsc.NoSmartCard or Pcsc.RemovedCard or Pcsc.UnresponsiveCard)) throw new PcscException($"SCardConnect \"{Name}\"", code);
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Card : ISmartCardChannel
    {
        private readonly Pcsc.PcscContext _context;
        private readonly uint _protocol;
        private nint _card;

        public Card(string reader, Pcsc.PcscContext context, nint card, uint protocol)
        {
            (Reader, _context, _card, _protocol) = (reader, context, card, protocol);
            Atr = Pcsc.Atr(card);
        }

        public string Reader { get; }

        public ReadOnlyMemory<byte> Atr { get; }

        public ValueTask<byte[]> TransmitAsync(ReadOnlyMemory<byte> apdu, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_card == 0, this);
            return ValueTask.FromResult(Pcsc.Transmit(_card, _protocol, apdu.Span));
        }

        public ValueTask DisposeAsync()
        {
            if (_card != 0) Pcsc.Disconnect(_card);
            _card = 0;
            _context.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
