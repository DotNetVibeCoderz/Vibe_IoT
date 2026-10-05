using IoTCom.Net.Protocols.Mavlink;
using IoTCom.Net.Protocols.Mavlink.Common;

namespace IoTCom.Net.Tests.Protocols;

public sealed class MavlinkCodecTests
{
    private static readonly byte[] TestKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Generated_dialect_matches_the_reference_for_every_message()
    {
        var table = Conformance.Load("mavlink_messages.json");
        Assert.Equal(table.Length, CommonDialect.Instance.Messages.Count);
        foreach (var m in table)
        {
            var id = m.GetProperty("id").GetUInt32();
            Assert.True(CommonDialect.Instance.TryGetInfo(id, out var info), $"missing {m.GetProperty("name")}");
            Assert.Equal(m.GetProperty("name").GetString(), info.Name);
            Assert.Equal(m.GetProperty("crcExtra").GetInt32(), info.CrcExtra);
            Assert.Equal(m.GetProperty("minLength").GetInt32(), info.MinLength);
            Assert.Equal(m.GetProperty("maxLength").GetInt32(), info.MaxLength);
        }
    }

    public static IEnumerable<object[]> Frames => Conformance.Cases("mavlink.json");

    [Theory]
    [MemberData(nameof(Frames))]
    public void Shared_frame_vectors(string json)
    {
        var v = Conformance.Parse(json);
        var frame = v.Hex("frame");
        var signed = v.GetProperty("signed").GetBoolean();
        var parser = new MavlinkParser(CommonDialect.Instance, signed ? new MavlinkSigning(TestKey) : null);
        parser.Feed(frame);
        Assert.True(parser.TryRead(out var p), v.GetProperty("name").GetString());
        Assert.Equal(v.GetProperty("version").GetInt32(), (int)p.Version);
        Assert.Equal(v.GetProperty("sequence").GetInt32(), p.Sequence);
        Assert.Equal(v.GetProperty("systemId").GetInt32(), p.SystemId);
        Assert.Equal(v.GetProperty("componentId").GetInt32(), p.ComponentId);
        Assert.Equal(v.GetProperty("messageId").GetUInt32(), p.MessageId);
        Assert.Equal(signed, p.Signed);
        Assert.NotNull(p.Message);
        Assert.Equal(v.GetProperty("message").GetString(), p.Message!.Name);

        // The generated serializer reproduces the reference payload (full length, before truncation)…
        var payload = new byte[p.Message.MaxPayloadLength];
        p.Message.Serialize(payload);
        Assert.Equal(v.Hex("payload"), payload);
        // …and the codec reproduces the frame byte for byte (signed frames carry a fresh timestamp, so compare unsigned only).
        if (!signed)
            Assert.Equal(frame, MavlinkCodec.Encode(p.Message, p.Sequence, p.SystemId, p.ComponentId, p.Version));
    }

    [Fact]
    public void Typed_messages_round_trip()
    {
        var hb = new Heartbeat { Type = MavType.Quadrotor, Autopilot = MavAutopilot.Ardupilotmega, BaseMode = MavModeFlag.SafetyArmed | MavModeFlag.CustomModeEnabled, SystemStatus = MavState.Active };
        var frame = MavlinkCodec.Encode(hb, 5, 1, 1);
        var parser = new MavlinkParser(CommonDialect.Instance);
        parser.Feed(frame);
        Assert.True(parser.TryRead(out var p));
        var back = Assert.IsType<Heartbeat>(p.Message);
        Assert.Equal(MavType.Quadrotor, back.Type);
        Assert.True(back.BaseMode.HasFlag(MavModeFlag.SafetyArmed));

        var text = new Statustext { Severity = MavSeverity.Warning, Text = "PreArm: Battery below minimum" };
        parser.Feed(MavlinkCodec.Encode(text, 6, 1, 1));
        Assert.True(parser.TryRead(out p));
        Assert.Equal("PreArm: Battery below minimum", ((Statustext)p.Message!).Text);
        Assert.Contains("HEARTBEAT", hb.ToString());
    }

    [Fact]
    public void Parser_resynchronises_after_garbage_and_bad_crc()
    {
        var parser = new MavlinkParser(CommonDialect.Instance);
        var good = MavlinkCodec.Encode(new Attitude { Roll = 0.1f, Yaw = 1.5f }, 1, 1, 1);
        var bad = (byte[])good.Clone();
        bad[^1] ^= 0xFF;
        var v1 = MavlinkCodec.Encode(new Heartbeat(), 2, 1, 1, MavlinkVersion.V1);
        // Garbage includes a spurious v2 start with invalid incompatibility flags.
        byte[] stream = [0x00, 0xFD, 0x01, 0x02, .. bad, 0x55, .. good, .. v1, 0xFD];
        var got = new List<MavlinkPacket>();
        foreach (var b in stream)
        {
            parser.Feed([b]); // worst-case chunking
            while (parser.TryRead(out var p)) got.Add(p);
        }
        Assert.Equal([30u, 0u], got.Select(p => p.MessageId));
        Assert.Equal(1, parser.CrcErrors);
    }

    [Fact]
    public void Spurious_long_header_waits_then_recovers()
    {
        // A stray 0xFE followed by 0xFD reads as a v1 header announcing 253 bytes: like every MAVLink parser, we
        // wait for them, then the CRC fails and the real frames that arrived meanwhile are found.
        var parser = new MavlinkParser(CommonDialect.Instance);
        var hb = MavlinkCodec.Encode(new Heartbeat(), 1, 1, 1);
        parser.Feed([0xFE, .. hb]);
        Assert.False(parser.TryRead(out _));
        var got = 0;
        for (var i = 0; i < 20; i++)
        {
            parser.Feed(hb);
            while (parser.TryRead(out _)) got++;
        }
        Assert.Equal(21, got);
    }

    [Fact]
    public void Signing_rejects_forged_and_unsigned_frames()
    {
        var signing = new MavlinkSigning(TestKey, linkId: 2);
        var signed = MavlinkCodec.Encode(new Heartbeat(), 1, 1, 1, signing: signing);
        Assert.Equal(12 + 9 + 13, signed.Length); // last payload byte (mavlink_version = 3) is non-zero: nothing to truncate
        var verifier = new MavlinkParser(CommonDialect.Instance, new MavlinkSigning(TestKey));
        verifier.Feed(signed);
        Assert.True(verifier.TryRead(out var ok));
        Assert.True(ok.Signed);

        var forged = (byte[])signed.Clone();
        forged[^1] ^= 1;
        verifier.Feed(forged);
        verifier.Feed(MavlinkCodec.Encode(new Heartbeat(), 2, 1, 1)); // unsigned
        Assert.False(verifier.TryRead(out _));
        Assert.Equal(2, verifier.SignatureErrors);
        var s1 = MavlinkSigning.FromPassphrase("secret");
        Assert.True(s1.NextTimestamp() < s1.NextTimestamp());
    }
}

public sealed class MavlinkCustomDialectTests
{
    [Fact]
    public void Source_generator_compiles_an_application_dialect()
    {
        var m = new IoTCom.Net.Tests.TestDialect.IotcomSensor
        {
            State = IoTCom.Net.Tests.TestDialect.IotcomSensorState.Degraded, Temperature = 21.5f, Label = "bay-3", TimeUsec = 123, Humidity = 4550, Battery = 87,
        };
        Assert.Equal(42000u, IoTCom.Net.Tests.TestDialect.IotcomSensor.MavlinkId);
        Assert.Equal(23, IoTCom.Net.Tests.TestDialect.IotcomSensor.MinLength);   // 8 + 4 + 2 + 8 + 1, wire-ordered by size
        Assert.Equal(24, IoTCom.Net.Tests.TestDialect.IotcomSensor.MaxLength);   // + battery extension

        // Combine with common: the custom message and HEARTBEAT both decode.
        var dialect = new CompositeDialect(IoTCom.Net.Tests.TestDialect.IotcomTestDialect.Instance, CommonDialect.Instance);
        var parser = new MavlinkParser(dialect);
        parser.Feed(MavlinkCodec.Encode(m, 1, 1, 1));
        parser.Feed(MavlinkCodec.Encode(new Attitude { Roll = 0.5f }, 2, 1, 1));
        Assert.True(parser.TryRead(out var p1));
        var back = Assert.IsType<IoTCom.Net.Tests.TestDialect.IotcomSensor>(p1.Message);
        Assert.Equal(("bay-3", 4550, (byte)87, IoTCom.Net.Tests.TestDialect.IotcomSensorState.Degraded), (back.Label, (int)back.Humidity, back.Battery, back.State));
        Assert.True(parser.TryRead(out var p2));
        Assert.IsType<Attitude>(p2.Message);
    }
}
