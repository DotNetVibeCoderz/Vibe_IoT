namespace IoTCom.Net.Protocols.Bacnet;

/// <summary>A property reference: property and optional array index.</summary>
/// <param name="Property">Property.</param>
/// <param name="ArrayIndex">Array index (null = whole property).</param>
public readonly record struct BacnetPropertyReference(BacnetPropertyId Property, uint? ArrayIndex = null);

/// <summary>One property result of ReadPropertyMultiple: values, or an error.</summary>
public sealed record BacnetPropertyResult(BacnetPropertyReference Reference, IReadOnlyList<BacnetValue>? Values, BacnetErrorClass ErrorClass = default, BacnetErrorCode ErrorCode = default)
{
    /// <summary>True when the property was read.</summary>
    public bool Ok => Values is not null;
}

/// <summary>A property value in a COV notification.</summary>
public sealed record BacnetPropertyValue(BacnetPropertyReference Reference, IReadOnlyList<BacnetValue> Values, byte? Priority = null);

/// <summary>A decoded COV notification.</summary>
public sealed record BacnetCovNotification(uint SubscriberProcessId, BacnetObjectId Device, BacnetObjectId Object, uint TimeRemaining, IReadOnlyList<BacnetPropertyValue> Values);

/// <summary>Service data encoders and decoders (clauses 13, 15, 16), sans-I/O.</summary>
public static class BacnetServices
{
    // ---- Who-Is / I-Am / Who-Has / I-Have ----------------------------------------------------------------------------

    /// <summary>Who-Is service data (range optional).</summary>
    public static byte[] WhoIs(uint? low = null, uint? high = null)
    {
        var w = new BacnetWriter();
        if (low is not null && high is not null) w.ContextUnsigned(0, low.Value).ContextUnsigned(1, high.Value);
        return w.ToArray();
    }

    /// <summary>Decodes Who-Is (null range = all devices).</summary>
    public static (uint? Low, uint? High) DecodeWhoIs(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return (null, null);
        var r = new BacnetReader(data);
        return ((uint)r.ContextUnsigned(0), (uint)r.ContextUnsigned(1));
    }

    /// <summary>I-Am service data.</summary>
    public static byte[] IAm(uint device, int maxApdu, BacnetSegmentation segmentation, uint vendorId) => new BacnetWriter()
        .Application(BacnetValue.Object(new BacnetObjectId(BacnetObjectType.Device, device)))
        .Application(BacnetValue.Unsigned((uint)maxApdu))
        .Application(BacnetValue.Enumerated((uint)segmentation))
        .Application(BacnetValue.Unsigned(vendorId)).ToArray();

    /// <summary>Decodes I-Am.</summary>
    public static (uint Device, int MaxApdu, BacnetSegmentation Segmentation, uint VendorId) DecodeIAm(ReadOnlySpan<byte> data)
    {
        var r = new BacnetReader(data);
        var id = (BacnetObjectId)r.Application().Value!;
        var max = (uint)r.Application().Value!;
        var seg = (uint)r.Application().Value!;
        var vendor = (uint)r.Application().Value!;
        return (id.Instance, (int)max, (BacnetSegmentation)seg, vendor);
    }

    /// <summary>Decodes Who-Has: range and either an object id or a name.</summary>
    public static (uint? Low, uint? High, BacnetObjectId? Object, string? Name) DecodeWhoHas(ReadOnlySpan<byte> data)
    {
        var r = new BacnetReader(data);
        uint? low = null, high = null;
        if (r.IsContext(0))
        {
            low = (uint)r.ContextUnsigned(0);
            high = (uint)r.ContextUnsigned(1);
        }

        if (r.IsContext(2)) return (low, high, r.ContextObjectId(2), null);
        return (low, high, null, r.ContextText(3));
    }

    /// <summary>I-Have service data.</summary>
    public static byte[] IHave(uint device, BacnetObjectId obj, string name) => new BacnetWriter()
        .Application(BacnetValue.Object(new BacnetObjectId(BacnetObjectType.Device, device)))
        .Application(BacnetValue.Object(obj)).Application(BacnetValue.Text(name)).ToArray();

    // ---- ReadProperty -------------------------------------------------------------------------------------------------

    /// <summary>ReadProperty request data.</summary>
    public static byte[] ReadProperty(BacnetObjectId obj, BacnetPropertyReference p)
    {
        var w = new BacnetWriter().ContextObjectId(0, obj).ContextEnumerated(1, (uint)p.Property);
        if (p.ArrayIndex is { } i) w.ContextUnsigned(2, i);
        return w.ToArray();
    }

    /// <summary>Decodes a ReadProperty request.</summary>
    public static (BacnetObjectId Object, BacnetPropertyReference Property) DecodeReadProperty(ReadOnlySpan<byte> data)
    {
        var r = new BacnetReader(data);
        var obj = r.ContextObjectId(0);
        var prop = (BacnetPropertyId)r.ContextUnsigned(1);
        uint? index = r.IsContext(2) ? (uint)r.ContextUnsigned(2) : null;
        if (!r.End) throw new ProtocolException("ReadProperty request has unexpected trailing data.");
        return (obj, new BacnetPropertyReference(prop, index));
    }

    /// <summary>ReadProperty-ACK data.</summary>
    public static byte[] ReadPropertyAck(BacnetObjectId obj, BacnetPropertyReference p, IEnumerable<BacnetValue> values)
    {
        var w = new BacnetWriter().ContextObjectId(0, obj).ContextEnumerated(1, (uint)p.Property);
        if (p.ArrayIndex is { } i) w.ContextUnsigned(2, i);
        w.Open(3);
        foreach (var v in values) w.Application(v);
        return w.Close(3).ToArray();
    }

    /// <summary>Decodes a ReadProperty-ACK.</summary>
    public static (BacnetObjectId Object, BacnetPropertyReference Property, IReadOnlyList<BacnetValue> Values) DecodeReadPropertyAck(ReadOnlySpan<byte> data)
    {
        var r = new BacnetReader(data);
        var obj = r.ContextObjectId(0);
        var prop = (BacnetPropertyId)r.ContextUnsigned(1);
        uint? index = r.IsContext(2) ? (uint)r.ContextUnsigned(2) : null;
        r.Open(3);
        var values = new List<BacnetValue>();
        while (!r.IsClosing(3)) values.Add(r.Application());
        r.Close(3);
        return (obj, new BacnetPropertyReference(prop, index), values);
    }

    // ---- ReadPropertyMultiple -----------------------------------------------------------------------------------------

    /// <summary>ReadPropertyMultiple request data.</summary>
    public static byte[] ReadPropertyMultiple(IEnumerable<(BacnetObjectId Object, IReadOnlyList<BacnetPropertyReference> Properties)> specs)
    {
        var w = new BacnetWriter();
        foreach (var (obj, props) in specs)
        {
            w.ContextObjectId(0, obj).Open(1);
            foreach (var p in props)
            {
                w.ContextEnumerated(0, (uint)p.Property);
                if (p.ArrayIndex is { } i) w.ContextUnsigned(1, i);
            }

            w.Close(1);
        }

        return w.ToArray();
    }

    /// <summary>Decodes a ReadPropertyMultiple request.</summary>
    public static IReadOnlyList<(BacnetObjectId Object, IReadOnlyList<BacnetPropertyReference> Properties)> DecodeReadPropertyMultiple(ReadOnlySpan<byte> data)
    {
        var r = new BacnetReader(data);
        var specs = new List<(BacnetObjectId, IReadOnlyList<BacnetPropertyReference>)>();
        while (!r.End)
        {
            var obj = r.ContextObjectId(0);
            r.Open(1);
            var props = new List<BacnetPropertyReference>();
            while (!r.IsClosing(1))
            {
                var p = (BacnetPropertyId)r.ContextUnsigned(0);
                uint? i = r.IsContext(1) ? (uint)r.ContextUnsigned(1) : null;
                props.Add(new BacnetPropertyReference(p, i));
            }

            r.Close(1);
            specs.Add((obj, props));
        }

        if (specs.Count == 0) throw new ProtocolException("ReadPropertyMultiple request without a read access specification.");
        return specs;
    }

    /// <summary>ReadPropertyMultiple-ACK data.</summary>
    public static byte[] ReadPropertyMultipleAck(IEnumerable<(BacnetObjectId Object, IReadOnlyList<BacnetPropertyResult> Results)> results)
    {
        var w = new BacnetWriter();
        foreach (var (obj, list) in results)
        {
            w.ContextObjectId(0, obj).Open(1);
            foreach (var res in list)
            {
                w.ContextEnumerated(2, (uint)res.Reference.Property);
                if (res.Reference.ArrayIndex is { } i) w.ContextUnsigned(3, i);
                if (res.Values is { } values)
                {
                    w.Open(4);
                    foreach (var v in values) w.Application(v);
                    w.Close(4);
                }
                else
                {
                    w.Open(5).Application(BacnetValue.Enumerated((uint)res.ErrorClass)).Application(BacnetValue.Enumerated((uint)res.ErrorCode)).Close(5);
                }
            }

            w.Close(1);
        }

        return w.ToArray();
    }

    /// <summary>Decodes a ReadPropertyMultiple-ACK.</summary>
    public static IReadOnlyList<(BacnetObjectId Object, IReadOnlyList<BacnetPropertyResult> Results)> DecodeReadPropertyMultipleAck(ReadOnlySpan<byte> data)
    {
        var r = new BacnetReader(data);
        var all = new List<(BacnetObjectId, IReadOnlyList<BacnetPropertyResult>)>();
        while (!r.End)
        {
            var obj = r.ContextObjectId(0);
            r.Open(1);
            var list = new List<BacnetPropertyResult>();
            while (!r.IsClosing(1))
            {
                var p = (BacnetPropertyId)r.ContextUnsigned(2);
                uint? i = r.IsContext(3) ? (uint)r.ContextUnsigned(3) : null;
                var reference = new BacnetPropertyReference(p, i);
                if (r.IsOpening(4))
                {
                    r.Open(4);
                    var values = new List<BacnetValue>();
                    while (!r.IsClosing(4)) values.Add(r.Application());
                    r.Close(4);
                    list.Add(new BacnetPropertyResult(reference, values));
                }
                else
                {
                    r.Open(5);
                    var cls = (BacnetErrorClass)(uint)r.Application().Value!;
                    var code = (BacnetErrorCode)(uint)r.Application().Value!;
                    r.Close(5);
                    list.Add(new BacnetPropertyResult(reference, null, cls, code));
                }
            }

            r.Close(1);
            all.Add((obj, list));
        }

        return all;
    }

    // ---- WriteProperty ------------------------------------------------------------------------------------------------

    /// <summary>WriteProperty request data.</summary>
    public static byte[] WriteProperty(BacnetObjectId obj, BacnetPropertyReference p, IEnumerable<BacnetValue> values, byte? priority = null)
    {
        var w = new BacnetWriter().ContextObjectId(0, obj).ContextEnumerated(1, (uint)p.Property);
        if (p.ArrayIndex is { } i) w.ContextUnsigned(2, i);
        w.Open(3);
        foreach (var v in values) w.Application(v);
        w.Close(3);
        if (priority is { } pr) w.ContextUnsigned(4, pr);
        return w.ToArray();
    }

    /// <summary>Decodes a WriteProperty request.</summary>
    public static (BacnetObjectId Object, BacnetPropertyReference Property, IReadOnlyList<BacnetValue> Values, byte? Priority) DecodeWriteProperty(ReadOnlySpan<byte> data)
    {
        var r = new BacnetReader(data);
        var obj = r.ContextObjectId(0);
        var prop = (BacnetPropertyId)r.ContextUnsigned(1);
        uint? index = r.IsContext(2) ? (uint)r.ContextUnsigned(2) : null;
        r.Open(3);
        var values = new List<BacnetValue>();
        while (!r.IsClosing(3)) values.Add(r.Application());
        r.Close(3);
        byte? priority = null;
        if (r.IsContext(4))
        {
            var p = r.ContextUnsigned(4);
            if (p is < 1 or > 16) throw new ProtocolException($"Write priority {p} is outside 1–16.");
            priority = (byte)p;
        }

        return (obj, new BacnetPropertyReference(prop, index), values, priority);
    }

    // ---- SubscribeCOV and notifications -------------------------------------------------------------------------------

    /// <summary>SubscribeCOV request data (confirmed/lifetime null = cancellation).</summary>
    public static byte[] SubscribeCov(uint processId, BacnetObjectId obj, bool? confirmed, uint? lifetime)
    {
        var w = new BacnetWriter().ContextUnsigned(0, processId).ContextObjectId(1, obj);
        if (confirmed is { } c) w.ContextBoolean(2, c);
        if (lifetime is { } l) w.ContextUnsigned(3, l);
        return w.ToArray();
    }

    /// <summary>Decodes a SubscribeCOV request; <c>Confirmed</c> null means cancellation.</summary>
    public static (uint ProcessId, BacnetObjectId Object, bool? Confirmed, uint? Lifetime) DecodeSubscribeCov(ReadOnlySpan<byte> data)
    {
        var r = new BacnetReader(data);
        var pid = (uint)r.ContextUnsigned(0);
        var obj = r.ContextObjectId(1);
        bool? confirmed = r.IsContext(2) ? r.ContextBoolean(2) : null;
        uint? lifetime = r.IsContext(3) ? (uint)r.ContextUnsigned(3) : null;
        return (pid, obj, confirmed, lifetime);
    }

    /// <summary>COV notification data (confirmed or unconfirmed: the same encoding).</summary>
    public static byte[] CovNotification(BacnetCovNotification n)
    {
        var w = new BacnetWriter().ContextUnsigned(0, n.SubscriberProcessId).ContextObjectId(1, n.Device).ContextObjectId(2, n.Object).ContextUnsigned(3, n.TimeRemaining).Open(4);
        foreach (var v in n.Values)
        {
            w.ContextEnumerated(0, (uint)v.Reference.Property);
            if (v.Reference.ArrayIndex is { } i) w.ContextUnsigned(1, i);
            w.Open(2);
            foreach (var x in v.Values) w.Application(x);
            w.Close(2);
            if (v.Priority is { } p) w.ContextUnsigned(3, p);
        }

        return w.Close(4).ToArray();
    }

    /// <summary>Decodes a COV notification.</summary>
    public static BacnetCovNotification DecodeCovNotification(ReadOnlySpan<byte> data)
    {
        var r = new BacnetReader(data);
        var pid = (uint)r.ContextUnsigned(0);
        var dev = r.ContextObjectId(1);
        var obj = r.ContextObjectId(2);
        var remaining = (uint)r.ContextUnsigned(3);
        r.Open(4);
        var values = new List<BacnetPropertyValue>();
        while (!r.IsClosing(4))
        {
            var p = (BacnetPropertyId)r.ContextUnsigned(0);
            uint? i = r.IsContext(1) ? (uint)r.ContextUnsigned(1) : null;
            r.Open(2);
            var list = new List<BacnetValue>();
            while (!r.IsClosing(2)) list.Add(r.Application());
            r.Close(2);
            byte? priority = r.IsContext(3) ? (byte)r.ContextUnsigned(3) : null;
            values.Add(new BacnetPropertyValue(new BacnetPropertyReference(p, i), list, priority));
        }

        r.Close(4);
        return new BacnetCovNotification(pid, dev, obj, remaining, values);
    }
}
