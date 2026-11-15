using System.Buffers.Binary;
using System.IO.BACnet;
using System.Net;
using System.Threading.Channels;

namespace IoTCom.Net.Adapters.Bacnet;

/// <summary>
/// Runs the BACnet library's client over an IoTCom <see cref="IDatagramTransport"/> (real UDP or the in-memory
/// network): adds and strips the 4-byte BVLC header, unwraps Forwarded-NPDUs, and reports every frame to a tap.
/// </summary>
internal sealed class IoTComBacnetTransport : IBacnetTransport
{
    private readonly IDatagramTransport _socket;
    private readonly IPEndPoint _broadcast;
    private readonly Action<FrameDirection, byte[]>? _tap;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public IoTComBacnetTransport(IDatagramTransport socket, IPEndPoint broadcast, Action<FrameDirection, byte[]>? tap) =>
        (_socket, _broadcast, _tap) = (socket, broadcast, tap);

    public event MessageRecievedHandler? MessageRecieved;

    public byte MaxInfoFrames { get; set; } = 0xFF;

    public int HeaderLength => 4;

    public int MaxBufferLength => 1500;

    public BacnetAddressTypes Type => BacnetAddressTypes.IP;

    public BacnetMaxAdpu MaxAdpuLength => BacnetMaxAdpu.MAX_APDU1476;

    public static BacnetAddress Address(IPEndPoint ep)
    {
        var adr = new byte[6];
        ep.Address.MapToIPv4().GetAddressBytes().CopyTo(adr, 0);
        BinaryPrimitives.WriteUInt16BigEndian(adr.AsSpan(4), (ushort)ep.Port);
        return new BacnetAddress(BacnetAddressTypes.IP, 0, adr);
    }

    public static IPEndPoint Endpoint(BacnetAddress address) =>
        new(new IPAddress(address.adr.AsSpan(0, 4)), BinaryPrimitives.ReadUInt16BigEndian(address.adr.AsSpan(4)));

    public BacnetAddress GetBroadcastAddress()
    {
        var a = Address(_broadcast);
        a.net = 0xFFFF;
        return a;
    }

    public void Start() => _loop ??= Task.Run(() => ReceiveAsync(_cts.Token));

    public bool WaitForAllTransmits(int timeout) => true;

    public int Send(byte[] buffer, int offset, int dataLength, BacnetAddress address, bool waitForTransmission, int timeout)
    {
        var to = Endpoint(address);
        var broadcast = address.net == 0xFFFF || to.Equals(_broadcast) || to.Address.Equals(IPAddress.Broadcast);
        var start = offset - HeaderLength;
        buffer[start] = 0x81;
        buffer[start + 1] = (byte)(broadcast ? 0x0B : 0x0A);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(start + 2), (ushort)(dataLength + HeaderLength));
        var frame = buffer.AsSpan(start, dataLength + HeaderLength).ToArray();
        _tap?.Invoke(FrameDirection.Outbound, frame);
        _socket.SendAsync(frame, broadcast ? _broadcast : to, _cts.Token).AsTask().GetAwaiter().GetResult();
        return frame.Length;
    }

    private async Task ReceiveAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Datagram d;
            try
            {
                d = await _socket.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ObjectDisposedException or ChannelClosedException)
            {
                if (ct.IsCancellationRequested) break;
                continue;
            }

            var data = d.Data.ToArray();
            if (data.Length < 4 || data[0] != 0x81) continue;
            if (d.Remote is IPEndPoint remote && _socket.LocalEndPoint is IPEndPoint local && remote.Equals(local)) continue;   // our own broadcast
            _tap?.Invoke(FrameDirection.Inbound, data);
            var from = d.Remote as IPEndPoint;
            var offset = 4;
            switch (data[1])
            {
                case 0x0A or 0x0B:
                    break;
                case 0x04 when data.Length >= 10:
                    from = new IPEndPoint(new IPAddress(data.AsSpan(4, 4)), BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(8)));
                    offset = 10;
                    break;
                default:
                    continue;
            }

            if (from is null) continue;
            try
            {
                MessageRecieved?.Invoke(this, data, offset, data.Length - offset, Address(from));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A malformed frame must not stop the receive loop; the library logs its own decode errors.
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _socket.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _cts.Dispose();
    }
}
