using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace IoTCom.Net.Protocols.EtherCat;

/// <summary>A network interface that can carry EtherCAT.</summary>
/// <param name="Name">Name to pass to <see cref="RawEthernetLink.Open"/> (Linux: eth0; Npcap: \Device\NPF_{GUID}).</param>
/// <param name="Description">Human-readable description.</param>
public sealed record EthernetInterface(string Name, string Description);

/// <summary>
/// Raw Ethernet frames on a real interface: AF_PACKET sockets on Linux (needs <c>CAP_NET_RAW</c>:
/// <c>sudo setcap cap_net_raw+ep</c> on the executable, or root), Npcap on Windows (install with "WinPcap API-compatible
/// mode"; administrator rights or Npcap's non-admin option) and libpcap on macOS (root, or access to /dev/bpf*). Use a
/// dedicated network port for EtherCAT: the segment must not be shared with IP traffic.
/// </summary>
public static partial class RawEthernetLink
{
    /// <summary>Opens <paramref name="name"/> for EtherCAT frames.</summary>
    /// <exception cref="TransportException">The interface cannot be opened (missing privileges, no Npcap, unknown name).</exception>
    public static IEthercatLink Open(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return OperatingSystem.IsLinux() ? new PacketSocketLink(name) : new PcapLink(name);
    }

    /// <summary>Lists interfaces (Linux: /sys/class/net; elsewhere: pcap_findalldevs).</summary>
    public static IReadOnlyList<EthernetInterface> List()
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                return [.. Directory.GetDirectories("/sys/class/net").Select(Path.GetFileName).Where(n => n is not null && n != "lo")
                    .Select(n => new EthernetInterface(n!, File.Exists($"/sys/class/net/{n}/operstate") ? File.ReadAllText($"/sys/class/net/{n}/operstate").Trim() : ""))];
            }
            catch (IOException)
            {
                return [];
            }
        }

        return PcapLink.List();
    }

    // ---- Linux AF_PACKET

    private sealed partial class PacketSocketLink : IEthercatLink
    {
        private const int AfPacket = 17, SockRaw = 3, PollIn = 1;
        private readonly int _fd;
        private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _reader;

        public PacketSocketLink(string name)
        {
            Name = name;
            var protocol = BinaryPrimitives.ReverseEndianness(EcatFrame.EtherType);
            _fd = Socket(AfPacket, SockRaw, protocol);
            if (_fd < 0) throw Errno($"socket(AF_PACKET) on {name} — EtherCAT needs CAP_NET_RAW (sudo setcap cap_net_raw+ep <executable>) or root");
            var index = IfNameToIndex(name);
            if (index == 0)
            {
                _ = Close(_fd);
                throw new TransportException($"Unknown network interface '{name}'.");
            }

            Span<byte> sll = stackalloc byte[20];
            BinaryPrimitives.WriteUInt16LittleEndian(sll, AfPacket);
            BinaryPrimitives.WriteUInt16BigEndian(sll[2..], EcatFrame.EtherType);
            BinaryPrimitives.WriteInt32LittleEndian(sll[4..], index);
            unsafe
            {
                fixed (byte* p = sll)
                {
                    if (Bind(_fd, p, sll.Length) < 0)
                    {
                        var ex = Errno($"bind({name})");
                        _ = Close(_fd);
                        throw ex;
                    }
                }
            }

            _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"ethercat-{name}" };
            _reader.Start();
        }

        public string Name { get; }

        private void ReadLoop()
        {
            var buffer = new byte[2048];
            var pfd = new PollFd { Fd = _fd, Events = PollIn };
            while (!_cts.IsCancellationRequested)
            {
                if (Poll(ref pfd, 1, 100) <= 0) continue;
                nint n;
                unsafe
                {
                    fixed (byte* p = buffer) n = Read(_fd, p, buffer.Length);
                }

                if (n <= 14) continue;
                if (BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(12)) == EcatFrame.EtherType) _frames.Writer.TryWrite(buffer.AsSpan(0, (int)n).ToArray());
            }
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
        {
            unsafe
            {
                fixed (byte* p = frame.Span)
                {
                    if (Write(_fd, p, frame.Length) != frame.Length) throw Errno($"write({Name})");
                }
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> ReceiveAsync(CancellationToken ct = default) => _frames.Reader.ReadAsync(ct);

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _reader.Join(500);
            _ = Close(_fd);
            _frames.Writer.TryComplete();
            _cts.Dispose();
        }

        private static TransportException Errno(string what) => new($"{what} failed (errno {Marshal.GetLastPInvokeError()}).");

        [StructLayout(LayoutKind.Sequential)]
        private struct PollFd
        {
            public int Fd;
            public short Events;
            public short Revents;
        }

        [LibraryImport("libc", EntryPoint = "socket", SetLastError = true)]
        private static partial int Socket(int domain, int type, int protocol);

        [LibraryImport("libc", EntryPoint = "bind", SetLastError = true)]
        private static unsafe partial int Bind(int fd, byte* addr, int len);

        [LibraryImport("libc", EntryPoint = "if_nametoindex", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        private static partial int IfNameToIndex(string name);

        [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
        private static partial int Poll(ref PollFd fds, nuint count, int timeoutMs);

        [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
        private static unsafe partial nint Read(int fd, byte* buf, nint count);

        [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
        private static unsafe partial nint Write(int fd, byte* buf, nint count);

        [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
        private static partial int Close(int fd);
    }

    // ---- Npcap / libpcap

    private sealed partial class PcapLink : IEthercatLink
    {
        private const string Lib = "iotcom_pcap";
        private static int _resolverSet;
        private readonly nint _handle;
        private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _reader;
        private readonly Lock _send = new();

        public PcapLink(string name)
        {
            EnsureResolver();
            Name = name;
            var error = new byte[256];
            try
            {
                unsafe
                {
                    fixed (byte* e = error) _handle = OpenLive(name, 2048, 1, 1, e);
                }
            }
            catch (DllNotFoundException ex)
            {
                throw new TransportException(OperatingSystem.IsWindows()
                    ? "Npcap is not installed (https://npcap.com, tick \"WinPcap API-compatible mode\")."
                    : "libpcap was not found.", ex);
            }

            if (_handle == 0) throw new TransportException($"pcap_open_live({name}) failed: {Text(error)} — run with administrator/root rights and check the name with `iotcom ethercat interfaces`.");
            _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"ethercat-{name}" };
            _reader.Start();
        }

        public string Name { get; }

        private static void EnsureResolver()
        {
            if (Interlocked.Exchange(ref _resolverSet, 1) != 0) return;
            NativeLibrary.SetDllImportResolver(typeof(PcapLink).Assembly, Resolve);
        }

        private static nint Resolve(string library, Assembly assembly, DllImportSearchPath? path)
        {
            if (library != Lib) return 0;
            string[] candidates = OperatingSystem.IsWindows()
                ? [Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll"), "wpcap.dll"]
                : OperatingSystem.IsMacOS() ? ["libpcap.A.dylib", "libpcap.dylib", "/usr/lib/libpcap.A.dylib"] : ["libpcap.so.1", "libpcap.so.0.8", "libpcap.so"];
            foreach (var c in candidates)
                if (NativeLibrary.TryLoad(c, out var h)) return h;
            return 0;
        }

        public static IReadOnlyList<EthernetInterface> List()
        {
            EnsureResolver();
            var result = new List<EthernetInterface>();
            var error = new byte[256];
            nint all;
            try
            {
                unsafe
                {
                    fixed (byte* e = error)
                        if (FindAllDevs(out all, e) != 0) return result;
                }
            }
            catch (DllNotFoundException)
            {
                return result;
            }

            for (var p = all; p != 0; p = Marshal.ReadIntPtr(p))
            {
                var name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(p, nint.Size)) ?? "";
                var description = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(p, nint.Size * 2)) ?? "";
                result.Add(new EthernetInterface(name, description));
            }

            FreeAllDevs(all);
            return result;
        }

        private void ReadLoop()
        {
            // struct pcap_pkthdr: struct timeval (8 bytes on Windows, 16 on 64-bit Unix), caplen, len.
            var capLenOffset = OperatingSystem.IsWindows() ? 8 : 16;
            while (!_cts.IsCancellationRequested)
            {
                int r;
                nint header, data;
                lock (_send) r = NextEx(_handle, out header, out data);
                if (r == 0) continue;
                if (r < 0) break;
                var length = Marshal.ReadInt32(header, capLenOffset);
                if (length <= 14) continue;
                var frame = new byte[length];
                Marshal.Copy(data, frame, 0, length);
                if (BinaryPrimitives.ReadUInt16BigEndian(frame.AsSpan(12)) == EcatFrame.EtherType) _frames.Writer.TryWrite(frame);
            }
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
        {
            int r;
            unsafe
            {
                fixed (byte* p = frame.Span)
                    lock (_send) r = SendPacket(_handle, p, frame.Length);
            }

            if (r != 0) throw new TransportException($"pcap_sendpacket({Name}) failed.");
            return ValueTask.CompletedTask;
        }

        public ValueTask<byte[]> ReceiveAsync(CancellationToken ct = default) => _frames.Reader.ReadAsync(ct);

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _reader.Join(500);
            lock (_send) PcapClose(_handle);
            _frames.Writer.TryComplete();
            _cts.Dispose();
        }

        private static string Text(byte[] error) => System.Text.Encoding.UTF8.GetString(error, 0, Math.Max(0, Array.IndexOf(error, (byte)0)));

        [LibraryImport(Lib, EntryPoint = "pcap_open_live", StringMarshalling = StringMarshalling.Utf8)]
        private static unsafe partial nint OpenLive(string device, int snaplen, int promisc, int timeoutMs, byte* errbuf);

        [LibraryImport(Lib, EntryPoint = "pcap_findalldevs")]
        private static unsafe partial int FindAllDevs(out nint devices, byte* errbuf);

        [LibraryImport(Lib, EntryPoint = "pcap_freealldevs")]
        private static partial void FreeAllDevs(nint devices);

        [LibraryImport(Lib, EntryPoint = "pcap_next_ex")]
        private static partial int NextEx(nint handle, out nint header, out nint data);

        [LibraryImport(Lib, EntryPoint = "pcap_sendpacket")]
        private static unsafe partial int SendPacket(nint handle, byte* data, int length);

        [LibraryImport(Lib, EntryPoint = "pcap_close")]
        private static partial void PcapClose(nint handle);
    }
}
