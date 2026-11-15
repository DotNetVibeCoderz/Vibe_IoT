using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;

namespace IoTCom.Net.Protocols.EtherCat;

/// <summary>Moves raw Ethernet frames between the master and the segment (raw socket, pcap, or a simulated segment).</summary>
public interface IEthercatLink : IAsyncDisposable
{
    /// <summary>Interface name, e.g. "eth0" or "virtual".</summary>
    string Name { get; }

    /// <summary>Sends one Ethernet frame.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default);

    /// <summary>Receives the next EtherCAT frame (EtherType 0x88A4); other traffic is filtered out.</summary>
    ValueTask<byte[]> ReceiveAsync(CancellationToken ct = default);
}

/// <summary>
/// A simulated EtherCAT slave controller (ESC): register memory with AL state machine, SII EEPROM, SyncManagers,
/// FMMUs for logical addressing, a CoE mailbox and a small object dictionary. Subclasses fill inputs and use outputs.
/// </summary>
public class VirtualEcatSlave
{
    private readonly byte[] _mem = new byte[0x2000];
    private readonly Dictionary<(ushort, byte), (byte[] Value, bool Writable)> _objects = [];
    private byte[] _sii = [];
    private bool _mailboxFull;

    /// <summary>Mailbox locations used by the simulated slaves.</summary>
    public const ushort MailboxOut = 0x1000, MailboxIn = 0x1080, MailboxSize = 128, OutputArea = 0x1100, InputArea = 0x1180;

    /// <summary>Creates a slave.</summary>
    public VirtualEcatSlave(string name, uint vendor, uint product, int outputBytes, int inputBytes, bool mailbox = true, uint revision = 0x00010000, uint serial = 0)
    {
        Name = name;
        (Vendor, Product, Revision, Serial) = (vendor, product, revision, serial);
        (OutputBytes, InputBytes, HasMailbox) = (outputBytes, inputBytes, mailbox);
        _mem[0] = 0x11;   // ESC type
        _mem[4] = 8;      // FMMUs
        _mem[5] = 8;      // SyncManagers
        BuildSii();
        AddObject(0x1000, 0, BitConverter.GetBytes(0x00001389u));
        AddObject(0x1008, 0, Encoding.ASCII.GetBytes(name));
        AddObject(0x1009, 0, "01"u8.ToArray());
        AddObject(0x100A, 0, "01.00"u8.ToArray());
        AddObject(0x1018, 1, BitConverter.GetBytes(vendor));
        AddObject(0x1018, 2, BitConverter.GetBytes(product));
        AddObject(0x1018, 3, BitConverter.GetBytes(revision));
        AddObject(0x1018, 4, BitConverter.GetBytes(serial));
    }

    /// <summary>Device name.</summary>
    public string Name { get; }

    /// <summary>Vendor ID.</summary>
    public uint Vendor { get; }

    /// <summary>Product code.</summary>
    public uint Product { get; }

    /// <summary>Revision.</summary>
    public uint Revision { get; }

    /// <summary>Serial number.</summary>
    public uint Serial { get; }

    /// <summary>Output process data size (master → slave).</summary>
    public int OutputBytes { get; }

    /// <summary>Input process data size (slave → master).</summary>
    public int InputBytes { get; }

    /// <summary>True when the slave has a CoE mailbox.</summary>
    public bool HasMailbox { get; }

    /// <summary>Current AL state.</summary>
    public EcatState State { get; private set; } = EcatState.Init;

    /// <summary>True when the AL error flag is set.</summary>
    public bool Error { get; private set; }

    /// <summary>AL status code of the last error.</summary>
    public ushort StatusCode { get; private set; }

    /// <summary>Configured station address.</summary>
    public ushort Station => BinaryPrimitives.ReadUInt16LittleEndian(_mem.AsSpan(EcatRegister.StationAddress));

    /// <summary>Outputs as last written by the master (zeros below OP: the safe state).</summary>
    public byte[] Outputs => State == EcatState.Op ? _mem.AsSpan(OutputArea, OutputBytes).ToArray() : new byte[OutputBytes];

    /// <summary>Input memory the application updates.</summary>
    protected Span<byte> InputMemory => _mem.AsSpan(InputArea, InputBytes);

    /// <summary>Adds an object to the dictionary.</summary>
    public void AddObject(ushort index, byte subIndex, byte[] value, bool writable = false) => _objects[(index, subIndex)] = (value, writable);

    /// <summary>Reads an object (for tests and views).</summary>
    public byte[]? GetObject(ushort index, byte subIndex) => _objects.TryGetValue((index, subIndex), out var o) ? o.Value : null;

    /// <summary>Called when the master writes a writable object; return an abort code to refuse, or 0.</summary>
    protected virtual uint OnObjectWrite(ushort index, byte subIndex, byte[] value) => 0;

    /// <summary>Advances the application (inputs).</summary>
    public virtual void Step(double seconds)
    {
    }

    /// <summary>Raises an AL error, as a real slave does on a watchdog or a local fault.</summary>
    public void RaiseError(ushort code, EcatState fallback = EcatState.SafeOp)
    {
        Error = true;
        StatusCode = code;
        if (State > fallback) State = fallback;
    }

    private void BuildSii()
    {
        var categories = new List<(ushort, byte[])> { (EcatSii.Strings, EcatSii.StringsCategory(Name)), (EcatSii.General, EcatSii.GeneralCategory(1, HasMailbox)) };
        var sms = new List<(ushort, ushort, byte, byte)>();
        if (HasMailbox)
        {
            sms.Add((MailboxOut, MailboxSize, 0x26, 1));
            sms.Add((MailboxIn, MailboxSize, 0x22, 2));
        }

        if (OutputBytes > 0) sms.Add((OutputArea, (ushort)OutputBytes, 0x64, 3));
        if (InputBytes > 0) sms.Add((InputArea, (ushort)InputBytes, 0x20, 4));
        categories.Add((EcatSii.SyncManager, EcatSii.SyncManagerCategory([.. sms])));
        if (OutputBytes > 0) categories.Add((EcatSii.RxPdo, EcatSii.PdoCategory(0x1600, 2, (0x7000, 1, (byte)(OutputBytes * 8)))));
        if (InputBytes > 0) categories.Add((EcatSii.TxPdo, EcatSii.PdoCategory(0x1A00, 3, (0x6000, 1, (byte)(InputBytes * 8)))));
        _sii = EcatSii.Build(Vendor, Product, Revision, Serial,
            HasMailbox ? (MailboxOut, MailboxSize, MailboxIn, MailboxSize, (ushort)0x0004) : ((ushort)0, (ushort)0, (ushort)0, (ushort)0, (ushort)0), categories);
    }

    private (ushort Start, ushort Length, bool Active) Sm(int n)
    {
        var at = EcatRegister.Sm0 + (n * 8);
        return (BinaryPrimitives.ReadUInt16LittleEndian(_mem.AsSpan(at)), BinaryPrimitives.ReadUInt16LittleEndian(_mem.AsSpan(at + 2)), (_mem[at + 6] & 1) != 0);
    }

    private void Refresh()
    {
        BinaryPrimitives.WriteUInt16LittleEndian(_mem.AsSpan(EcatRegister.AlStatus), (ushort)((byte)State | (Error ? 0x10 : 0)));
        BinaryPrimitives.WriteUInt16LittleEndian(_mem.AsSpan(EcatRegister.AlStatusCode), StatusCode);
        BinaryPrimitives.WriteUInt16LittleEndian(_mem.AsSpan(EcatRegister.DlStatus), 0x0230);   // PDI operational, port 0 link
        _mem[EcatRegister.Sm0 + 8 + 5] = (byte)(_mailboxFull ? 0x08 : 0x00);
    }

    /// <summary>Processes one datagram as the frame passes this slave. Returns true when the slave addressed itself.</summary>
    internal void Process(EcatDatagram d, int position)
    {
        _ = position;
        switch (d.Command)
        {
            case EcatCommand.Aprd or EcatCommand.Apwr or EcatCommand.Aprw or EcatCommand.Armw:
            {
                var addressed = d.Adp == 0;
                if (addressed) Physical(d, d.Command is EcatCommand.Armw ? EcatCommand.Aprd : d.Command, orRead: false);
                else if (d.Command == EcatCommand.Armw) Physical(d, EcatCommand.Apwr, orRead: false);
                d.Adp++;
                break;
            }

            case EcatCommand.Fprd or EcatCommand.Fpwr or EcatCommand.Fprw or EcatCommand.Frmw:
                if (d.Adp == Station) Physical(d, d.Command is EcatCommand.Frmw ? EcatCommand.Fprd : d.Command, orRead: false);
                else if (d.Command == EcatCommand.Frmw) Physical(d, EcatCommand.Fpwr, orRead: false);
                break;
            case EcatCommand.Brd or EcatCommand.Bwr or EcatCommand.Brw:
                Physical(d, d.Command, orRead: true);
                d.Adp++;
                break;
            case EcatCommand.Lrd or EcatCommand.Lwr or EcatCommand.Lrw:
                Logical(d);
                break;
        }
    }

    private void Physical(EcatDatagram d, EcatCommand kind, bool orRead)
    {
        var start = d.Ado;
        var length = d.Data.Length;
        if (start + length > _mem.Length) return;
        var read = kind is EcatCommand.Aprd or EcatCommand.Fprd or EcatCommand.Brd or EcatCommand.Aprw or EcatCommand.Fprw or EcatCommand.Brw;
        var write = kind is EcatCommand.Apwr or EcatCommand.Fpwr or EcatCommand.Bwr or EcatCommand.Aprw or EcatCommand.Fprw or EcatCommand.Brw;
        byte[]? incoming = write ? (byte[])d.Data.Clone() : null;
        if (read)
        {
            Refresh();
            for (var i = 0; i < length; i++) d.Data[i] = orRead ? (byte)(d.Data[i] | _mem[start + i]) : _mem[start + i];
            d.WorkingCounter++;
            var (smStart, smLength, _) = Sm(1);
            if (HasMailbox && _mailboxFull && start <= smStart + smLength - 1 && start + length >= smStart + smLength) _mailboxFull = false;
        }

        if (write)
        {
            incoming!.CopyTo(_mem, start);
            d.WorkingCounter += (ushort)(read ? 2 : 1);
            AfterWrite(start, length);
        }
    }

    private void AfterWrite(int start, int length)
    {
        bool Covers(int register) => start <= register && start + length > register;
        if (Covers(EcatRegister.AlControl)) Transition(BinaryPrimitives.ReadUInt16LittleEndian(_mem.AsSpan(EcatRegister.AlControl)));
        if (Covers(EcatRegister.SiiControl) && (_mem[EcatRegister.SiiControl + 1] & 0x01) != 0)
        {
            var word = (int)BinaryPrimitives.ReadUInt32LittleEndian(_mem.AsSpan(EcatRegister.SiiAddress));
            for (var i = 0; i < 8; i++)
            {
                var at = (word * 2) + i;
                _mem[EcatRegister.SiiData + i] = at < _sii.Length ? _sii[at] : (byte)0xFF;
            }

            _mem[EcatRegister.SiiControl + 1] &= 0x7E;   // command done, not busy
        }

        var (mbxStart, mbxLength, mbxActive) = Sm(0);
        if (HasMailbox && mbxActive && State >= EcatState.PreOp && Covers(mbxStart + mbxLength - 1)) HandleMailbox(_mem.AsSpan(mbxStart, mbxLength));
    }

    private void HandleMailbox(ReadOnlySpan<byte> raw)
    {
        if (!EcatMailbox.TryDecode(raw, out var mbx, out _) || mbx is null) return;
        byte[] reply;
        if (mbx.Type != EcatMailboxType.CoE || !CoeSdo.TryDecode(mbx.Payload, out var sdo, out _) || sdo is null)
            reply = new EcatMailbox(EcatMailboxType.Error, mbx.Counter, [0x01, 0x00, 0x02, 0x00]).Encode(MailboxSize);   // unsupported protocol
        else if (sdo.IsUploadRequest)
        {
            reply = !_objects.TryGetValue((sdo.Index, sdo.SubIndex), out var o)
                ? Coe(mbx.Counter, CoeSdo.Abort(sdo.Index, sdo.SubIndex, _objects.Keys.Any(k => k.Item1 == sdo.Index) ? 0x06090011u : 0x06020000u))
                : Coe(mbx.Counter, CoeSdo.UploadResponse(sdo.Index, sdo.SubIndex, o.Value));
        }
        else if (sdo.IsDownloadRequest)
        {
            if (!_objects.TryGetValue((sdo.Index, sdo.SubIndex), out var o))
                reply = Coe(mbx.Counter, CoeSdo.Abort(sdo.Index, sdo.SubIndex, 0x06020000));
            else if (!o.Writable)
                reply = Coe(mbx.Counter, CoeSdo.Abort(sdo.Index, sdo.SubIndex, 0x06010002));
            else if (sdo.Data.Length != o.Value.Length)
                reply = Coe(mbx.Counter, CoeSdo.Abort(sdo.Index, sdo.SubIndex, 0x06070010));
            else
            {
                var code = OnObjectWrite(sdo.Index, sdo.SubIndex, sdo.Data);
                if (code == 0) _objects[(sdo.Index, sdo.SubIndex)] = (sdo.Data, true);
                reply = Coe(mbx.Counter, code == 0 ? CoeSdo.DownloadResponse(sdo.Index, sdo.SubIndex) : CoeSdo.Abort(sdo.Index, sdo.SubIndex, code));
            }
        }
        else reply = Coe(mbx.Counter, CoeSdo.Abort(sdo.Index, sdo.SubIndex, 0x05040001));

        var (inStart, inLength, _) = Sm(1);
        reply.AsSpan(0, Math.Min(reply.Length, inLength)).CopyTo(_mem.AsSpan(inStart));
        _mailboxFull = true;
    }

    private static byte[] Coe(byte counter, byte[] payload) => new EcatMailbox(EcatMailboxType.CoE, counter, payload).Encode(MailboxSize);

    private void Transition(ushort control)
    {
        var requested = (EcatState)(control & 0x0F);
        var ack = (control & 0x10) != 0;
        if (Error && !ack) return;
        if (ack)
        {
            Error = false;
            StatusCode = 0;
        }

        if (requested == State) return;
        ushort? fault = requested switch
        {
            EcatState.Init => null,
            EcatState.Boot => 0x0013,
            EcatState.PreOp when State == EcatState.Init => MailboxValid() ? null : (ushort)0x0016,
            EcatState.PreOp => null,
            EcatState.SafeOp when State == EcatState.PreOp => ProcessDataValid(),
            EcatState.SafeOp when State == EcatState.Op => null,
            EcatState.Op when State == EcatState.SafeOp => null,
            EcatState.SafeOp or EcatState.Op => 0x0011,
            _ => 0x0012,
        };
        if (fault is { } code)
        {
            Error = true;
            StatusCode = code;
            return;
        }

        State = requested;
        if (State < EcatState.PreOp) _mailboxFull = false;
    }

    private bool MailboxValid()
    {
        if (!HasMailbox) return true;
        var (s0, l0, a0) = Sm(0);
        var (s1, l1, a1) = Sm(1);
        return a0 && a1 && s0 == MailboxOut && l0 == MailboxSize && s1 == MailboxIn && l1 == MailboxSize;
    }

    private ushort? ProcessDataValid()
    {
        var smOut = HasMailbox ? 2 : 0;
        var smIn = smOut + (OutputBytes > 0 ? 1 : 0);
        if (OutputBytes > 0)
        {
            var (s, l, a) = Sm(smOut);
            if (!a || s != OutputArea || l != OutputBytes) return 0x001D;
            if (!FmmuCovers(OutputArea, OutputBytes, 2)) return 0x0024;
        }

        if (InputBytes > 0)
        {
            var (s, l, a) = Sm(smIn);
            if (!a || s != InputArea || l != InputBytes) return 0x001E;
            if (!FmmuCovers(InputArea, InputBytes, 1)) return 0x0024;
        }

        return null;
    }

    private bool FmmuCovers(int physical, int length, byte type)
    {
        for (var i = 0; i < 8; i++)
        {
            var f = Fmmu(i);
            if (f.Active && (f.Type & type) != 0 && f.Physical <= physical && f.Physical + f.Length >= physical + length) return true;
        }

        return false;
    }

    private (uint Logical, ushort Length, ushort Physical, byte Type, bool Active) Fmmu(int n)
    {
        var at = EcatRegister.Fmmu0 + (n * 16);
        return (BinaryPrimitives.ReadUInt32LittleEndian(_mem.AsSpan(at)), BinaryPrimitives.ReadUInt16LittleEndian(_mem.AsSpan(at + 4)),
            BinaryPrimitives.ReadUInt16LittleEndian(_mem.AsSpan(at + 8)), _mem[at + 11], (_mem[at + 12] & 1) != 0);
    }

    private void Logical(EcatDatagram d)
    {
        if (State < EcatState.SafeOp) return;
        bool readHit = false, writeHit = false;
        var start = (long)d.Address;
        var end = start + d.Data.Length;
        for (var i = 0; i < 8; i++)
        {
            var f = Fmmu(i);
            if (!f.Active || f.Length == 0) continue;
            var from = Math.Max(start, f.Logical);
            var to = Math.Min(end, (long)f.Logical + f.Length);
            if (from >= to) continue;
            for (var a = from; a < to; a++)
            {
                var phys = f.Physical + (int)(a - f.Logical);
                var frameAt = (int)(a - start);
                if ((f.Type & 1) != 0 && d.Command is EcatCommand.Lrd or EcatCommand.Lrw)
                {
                    d.Data[frameAt] = _mem[phys];
                    readHit = true;
                }

                if ((f.Type & 2) != 0 && d.Command is EcatCommand.Lwr or EcatCommand.Lrw)
                {
                    _mem[phys] = d.Data[frameAt];
                    writeHit = true;
                }
            }
        }

        if (readHit) d.WorkingCounter++;
        if (writeHit) d.WorkingCounter += (ushort)(d.Command == EcatCommand.Lrw ? 2 : 1);
    }
}

/// <summary>
/// A simulated EtherCAT segment: frames pass every slave in order and come back with the source address marked as
/// returned. <see cref="BreakAfter"/> cuts the cable after a slave (the last reachable slave closes the loop, as on a
/// real line), <see cref="LossRate"/> drops frames.
/// </summary>
public sealed class VirtualEthercatSegment : IEthercatLink
{
    private readonly List<VirtualEcatSlave> _slaves = [];
    private readonly Channel<byte[]> _returned = Channel.CreateUnbounded<byte[]>();
    private readonly Lock _gate = new();
    private readonly Random _random;

    /// <summary>Creates an empty segment.</summary>
    public VirtualEthercatSegment(int seed = 1) => _random = new Random(seed);

    /// <inheritdoc />
    public string Name => "virtual";

    /// <summary>Slaves in cable order.</summary>
    public IReadOnlyList<VirtualEcatSlave> Slaves
    {
        get
        {
            lock (_gate) return [.. _slaves];
        }
    }

    /// <summary>Cable cut after this many slaves (null = intact).</summary>
    public int? BreakAfter { get; set; }

    /// <summary>Fraction of frames lost (0–1).</summary>
    public double LossRate { get; set; }

    /// <summary>Frames processed.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Appends a slave to the end of the line.</summary>
    public T Add<T>(T slave) where T : VirtualEcatSlave
    {
        lock (_gate) _slaves.Add(slave);
        return slave;
    }

    /// <summary>Runs every slave's application under the segment lock (so frames see consistent inputs).</summary>
    public void Step(double seconds)
    {
        lock (_gate)
            foreach (var s in _slaves) s.Step(seconds);
    }

    /// <inheritdoc />
    public ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct = default)
    {
        if (!EcatFrame.TryDecode(frame.Span, out var datagrams, out _)) return ValueTask.CompletedTask;
        lock (_gate)
        {
            FrameCount++;
            if (LossRate > 0 && _random.NextDouble() < LossRate) return ValueTask.CompletedTask;
            var reach = BreakAfter is { } b ? Math.Min(b, _slaves.Count) : _slaves.Count;
            foreach (var d in datagrams)
                for (var i = 0; i < reach; i++)
                    _slaves[i].Process(d, i);
        }

        var source = frame.Span.Slice(6, 6).ToArray();
        source[0] |= 0x02;
        _returned.Writer.TryWrite(EcatFrame.Encode(datagrams, source));
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<byte[]> ReceiveAsync(CancellationToken ct = default) => _returned.Reader.ReadAsync(ct);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _returned.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
