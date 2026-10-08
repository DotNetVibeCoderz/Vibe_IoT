using IoTCom.Net.Protocols.Dlms;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Protocols;

public class DlmsCodecTests
{
    [Theory]
    [InlineData("1.0.1.8.0.255", "1-0:1.8.0*255")]
    [InlineData("1-0:32.7.0*255", "1-0:32.7.0*255")]
    [InlineData("1.8.0", "1-0:1.8.0*255")]
    [InlineData("0.0.96.1.0", "0-0:96.1.0*255")]
    public void Obis_codes_parse_in_every_notation(string text, string display) =>
        Assert.Equal(display, ObisCode.Parse(text).ToDisplayString());

    [Fact]
    public void Obis_catalog_and_bad_input()
    {
        Assert.Equal("Active energy import (+A), total", ObisCode.Parse("1.0.1.8.0.255").Description);
        Assert.False(ObisCode.TryParse("1.0.1.8.0.256", out _));
        Assert.False(ObisCode.TryParse("hello", out _));
    }

    [Fact]
    public void Axdr_round_trips_every_type()
    {
        var value = CosemData.Structure(
            CosemData.Null, CosemData.Boolean(true), CosemData.Int8(-5), CosemData.Int16(-300), CosemData.Int32(-70000), CosemData.Int64(-1),
            CosemData.UInt8(200), CosemData.UInt16(60000), CosemData.UInt32(4_000_000_000), CosemData.UInt64(ulong.MaxValue),
            CosemData.Enum(3), CosemData.Float32(1.5f), CosemData.Float64(-2.25), CosemData.OctetString(new byte[200]),
            CosemData.VisibleString("ABC"), CosemData.Utf8String("Rp 1.500"), CosemData.BitString([0xA0], 3),
            CosemData.Array(CosemData.UInt16(1), CosemData.UInt16(2)),
            CosemData.DateTime(new DateTimeOffset(2026, 10, 8, 19, 45, 30, 120, TimeSpan.FromHours(7))));
        var encoded = value.Encode();
        Assert.Equal(value, CosemData.Decode(encoded));
        Assert.Equal(0x02, encoded[0]);
        Assert.Equal(19, encoded[1]);
        Assert.Equal("18446744073709551615", CosemData.Decode(CosemData.UInt64(ulong.MaxValue).Encode()).ToString());
        // 200-byte octet string uses the 0x81 long form.
        Assert.Equal(new byte[] { 0x09, 0x81, 0xC8 }, CosemData.OctetString(new byte[200]).Encode()[..3]);
    }

    [Fact]
    public void Date_time_uses_deviation_from_local_to_utc()
    {
        var t = new DateTimeOffset(2026, 10, 8, 19, 45, 30, TimeSpan.FromHours(7));
        var b = CosemDateTime.Encode(t);
        Assert.Equal("07EA0A0804132D1E00FE5C00", Convert.ToHexString(b));   // deviation 0xFE5C = −420
        Assert.Equal(t, CosemDateTime.Decode(b));
        Assert.Equal("2026-10-08 19:45:30 +07:00", CosemData.DateTime(t).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("01")]
    [InlineData("0105")]
    [InlineData("0902AB")]
    [InlineData("12")]
    [InlineData("FF")]
    [InlineData("0184FFFFFFFF")]
    public void Malformed_data_is_rejected_without_throwing(string hex) =>
        Assert.False(CosemData.TryDecode(Convert.FromHexString(hex), out _, out _, out _));

    [Fact]
    public void Deeply_nested_data_is_rejected() =>
        Assert.False(CosemData.TryDecode(Enumerable.Repeat(new byte[] { 0x02, 0x01 }, 100).SelectMany(b => b).ToArray(), out _, out _, out _));

    [Fact]
    public void Hdlc_snrm_matches_the_classic_frame()
    {
        var snrm = new HdlcFrame(new HdlcAddress(1), new HdlcAddress(16), HdlcControl.Snrm, []);
        Assert.Equal("7EA0070321930F017E", Convert.ToHexString(snrm.Encode()));
        Assert.Equal(HdlcFrame.ReadStatus.Frame, HdlcFrame.TryRead(snrm.Encode(), out var back, out var n, out _));
        Assert.Equal(9, n);
        Assert.Equal("SNRM", HdlcControl.Describe(back!.Control));
    }

    [Fact]
    public void Hdlc_frames_round_trip_with_four_byte_addresses_and_detect_corruption()
    {
        var frame = new HdlcFrame(new HdlcAddress(1, 0x3FFF), new HdlcAddress(1), HdlcControl.I(3, 5), [0xE6, 0xE6, 0x00, 0xC0, 0x01], Segmented: true);
        var bytes = frame.Encode();
        Assert.Equal(HdlcFrame.ReadStatus.Frame, HdlcFrame.TryRead(bytes, out var back, out _, out _));
        Assert.Equal(frame.Destination.Lower, back!.Destination.Lower);
        Assert.True(back.Segmented);
        Assert.Equal("I(S=3,R=5)", HdlcControl.Describe(back.Control));
        bytes[^4] ^= 0x01;
        Assert.Equal(HdlcFrame.ReadStatus.Skipped, HdlcFrame.TryRead(bytes, out _, out _, out var error));
        Assert.Contains("FCS", error, StringComparison.Ordinal);
        Assert.Equal(HdlcFrame.ReadStatus.NeedMore, HdlcFrame.TryRead(frame.Encode()[..6], out _, out _, out _));
    }

    [Fact]
    public void Aarq_matches_a_public_client_reference()
    {
        var aarq = DlmsApdu.Encode(new AarqPdu { Conformance = DlmsConformance.DefaultClient, MaxPduSize = 0x04B0 });
        Assert.Equal("601DA109060760857405080101BE10040E01000000065F1F0400007E1F04B0", Convert.ToHexString(aarq));
        var back = Assert.IsType<AarqPdu>(DlmsApdu.Decode(aarq));
        Assert.Equal(DlmsConformance.DefaultClient, back.Conformance);
        Assert.Equal(0x04B0, back.MaxPduSize);
    }

    [Fact]
    public void Get_request_and_responses_round_trip()
    {
        var get = new GetRequestPdu(new CosemReference(3, ObisCode.Parse("1.0.1.8.0.255"), 2)) { InvokeId = 0xC1 };
        Assert.Equal("C001C100030100010800FF0200", Convert.ToHexString(DlmsApdu.Encode(get)));
        Assert.Equal(get, DlmsApdu.Decode(DlmsApdu.Encode(get)));
        var ok = new GetResponsePdu { InvokeId = 0xC1, Data = CosemData.UInt32(12345) };
        Assert.Equal("C401C1000600003039", Convert.ToHexString(DlmsApdu.Encode(ok)));
        var denied = DlmsApdu.Decode(DlmsApdu.Encode(new GetResponsePdu { InvokeId = 0xC1, Result = DataAccessResult.ReadWriteDenied }));
        Assert.Equal(DataAccessResult.ReadWriteDenied, ((GetResponsePdu)denied).Result);
        var block = (GetResponsePdu)DlmsApdu.Decode(DlmsApdu.Encode(new GetResponsePdu { IsBlock = true, BlockNumber = 2, LastBlock = true, Block = [1, 2, 3] }));
        Assert.True(block.IsBlock && block.LastBlock && block.BlockNumber == 2);
        Assert.Equal(new byte[] { 1, 2, 3 }, block.Block);
    }

    [Fact]
    public void Ciphering_round_trips_and_rejects_tampering_and_replay()
    {
        var keys = (St: Convert.FromHexString("4D4D4D0000BC614E"), Ek: Convert.FromHexString("000102030405060708090A0B0C0D0E0F"), Ak: Convert.FromHexString("D0D1D2D3D4D5D6D7D8D9DADBDCDDDEDF"));
        var client = new DlmsSecurity(keys.St, keys.Ek, keys.Ak) { InvocationCounter = 0x01234567 };
        var server = new DlmsSecurity(Convert.FromHexString("4D4D4D0000000001"), keys.Ek, keys.Ak) { PeerSystemTitle = keys.St };
        var apdu = DlmsApdu.Encode(new GetRequestPdu(new CosemReference(8, ObisCode.Parse("0.0.1.0.0.255"), 2)));
        var protectedApdu = client.Protect(0xC8, apdu);
        Assert.Equal(0xC8, protectedApdu[0]);
        var pdu = (CipheredPdu)DlmsApdu.Decode(protectedApdu);
        Assert.Equal(0x30, pdu.SecurityControl);
        Assert.Equal(0x01234567u, pdu.InvocationCounter);
        Assert.Equal(apdu, server.Unprotect(pdu));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => server.Unprotect(pdu));                   // replay
        var tampered = pdu with { InvocationCounter = pdu.InvocationCounter + 1, Payload = [.. pdu.Payload[..^1], (byte)(pdu.Payload[^1] ^ 1)] };
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => server.Unprotect(tampered));
        var gmac = DlmsSecurity.Gmac(keys.St, keys.Ek, keys.Ak, 7, "challenge"u8);
        Assert.True(server.VerifyGmac(keys.St, gmac, "challenge"u8));
        Assert.False(server.VerifyGmac(keys.St, gmac, "other"u8));
    }

    [Fact]
    public void Truncated_tags_match_native_twelve_byte_gcm_where_available()
    {
        var key = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        var st = Convert.FromHexString("4D4D4D0000BC614E");
        var apdu = Convert.FromHexString("C001C1000801000100FF0200");
        var ours = DlmsSecurity.Encrypt(st, key, key, 0x30, 0x01234567, apdu);
        Assert.Equal(apdu.Length + 12, ours.Length);
        Assert.Equal(apdu, DlmsSecurity.Decrypt(st, key, key, 0x30, 0x01234567, ours));
        if (!System.Security.Cryptography.AesGcm.TagByteSizes.MinSize.Equals(16))
        {
            using var native = new System.Security.Cryptography.AesGcm(key, 12);
            var cipher = new byte[apdu.Length];
            var tag = new byte[12];
            native.Encrypt((byte[])[.. st, 0x01, 0x23, 0x45, 0x67], apdu, cipher, tag, (byte[])[0x30, .. key]);
            Assert.Equal([.. cipher, .. tag], ours);
        }
    }

    [Fact]
    public void Anatomy_covers_hdlc_frames()
    {
        var info = new byte[] { 0xE6, 0xE6, 0x00 }.Concat(DlmsApdu.Encode(new GetRequestPdu(new CosemReference(3, ObisCode.Parse("1.0.1.8.0.255"), 2)))).ToArray();
        var frame = new HdlcFrame(new HdlcAddress(1, 17), new HdlcAddress(16), HdlcControl.I(0, 0), info).Encode();
        var lane = DlmsAnatomy.Describe(frame);
        Assert.Equal(frame.Length, lane.Sum(f => f.Length));
        Assert.Contains(lane, f => f.Name == "OBIS" && f.Value == "1.0.1.8.0.255");
    }
}

public class DlmsSessionTests
{
    private static readonly DlmsSecurityKeys MeterKeys = DlmsSecurityKeys.FromHex("49534B0000000017", "000102030405060708090A0B0C0D0E0F", "D0D1D2D3D4D5D6D7D8D9DADBDCDDDEDF");
    private static readonly DlmsSecurityKeys ClientKeys = DlmsSecurityKeys.FromHex("4943540000000001", "000102030405060708090A0B0C0D0E0F", "D0D1D2D3D4D5D6D7D8D9DADBDCDDDEDF");

    private static async Task<(DlmsServer Server, DlmsMeterSimulator Meter, InMemoryTransportListener Listener)> MeterAsync(DlmsFraming framing, int maxInfo = 128)
    {
        var listener = new InMemoryTransportListener("meter");
        var server = DlmsServer.Create(o =>
        {
            o.ListenInMemory(listener);
            o.Framing = framing;
            o.Security = MeterKeys;
            o.MaxPduSize = 256;
            o.Hdlc = new HdlcParameters(maxInfo, maxInfo);
        });
        var meter = new DlmsMeterSimulator(server, clock: () => new DateTimeOffset(2026, 10, 8, 19, 30, 0, TimeSpan.FromHours(7)));
        await server.StartAsync();
        return (server, meter, listener);
    }

    [Theory]
    [InlineData(DlmsFraming.Hdlc)]
    [InlineData(DlmsFraming.Wrapper)]
    public async Task Public_client_reads_registers_clock_object_list_and_profile(DlmsFraming framing)
    {
        var (server, meter, listener) = await MeterAsync(framing, maxInfo: 64);
        await using var _ = server;
        await using var client = DlmsClient.Create(o => { o.UseInMemory(listener); o.Framing = framing; o.Hdlc = new HdlcParameters(64, 64); });
        await client.ConnectAsync();
        Assert.True(client.Conformance.HasFlag(DlmsConformance.Get));

        var energy = await client.ReadRegisterAsync(ObisCode.Parse("1.0.1.8.0.255"));
        Assert.Equal("Wh", energy.UnitSymbol);
        Assert.Equal(Math.Round(meter.ImportWh), energy.Value);
        var voltage = await client.ReadRegisterAsync(ObisCode.Parse("1.0.32.7.0.255"));
        Assert.Equal(-1, voltage.Scaler);
        Assert.InRange(voltage.Value, 220, 235);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 19, 30, 0, TimeSpan.FromHours(7)), await client.ReadClockAsync());

        var objects = await client.ReadObjectListAsync();                                // several kB: block transfer + segmentation
        Assert.Contains(objects, o => o.LogicalName == ObisCode.Parse("1.0.99.1.0.255") && o.ClassId == CosemClass.ProfileGeneric);

        var all = await client.ReadProfileAsync(ObisCode.Parse("1.0.99.1.0.255"));
        Assert.Equal(4, all.Columns.Count);
        Assert.Equal(193, all.Rows.Count);                                              // 2 days of 15-minute rows
        var lastHour = await client.ReadProfileAsync(ObisCode.Parse("1.0.99.1.0.255"),
            new DateTimeOffset(2026, 10, 8, 18, 30, 0, TimeSpan.FromHours(7)), new DateTimeOffset(2026, 10, 8, 19, 30, 0, TimeSpan.FromHours(7)));
        Assert.Equal(5, lastHour.Rows.Count);
        Assert.True(lastHour.Rows[^1][1].AsDouble() >= lastHour.Rows[0][1].AsDouble());

        var denied = await Assert.ThrowsAsync<DlmsException>(() => client.GetAsync(3, ObisCode.Parse("9.9.9.9.9.255"), 2));
        Assert.Equal(DataAccessResult.ObjectUndefined, denied.Result);
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => client.ActionAsync(CosemClass.DisconnectControl, ObisCode.Parse("0.0.96.3.10.255"), 1));
    }

    [Fact]
    public async Task Public_client_cannot_write_even_without_client_side_read_only()
    {
        var (server, _, listener) = await MeterAsync(DlmsFraming.Hdlc);
        await using var _s = server;
        await using var client = DlmsClient.Create(o => { o.UseInMemory(listener); o.ReadOnly = false; });
        await client.ConnectAsync();
        var ex = await Assert.ThrowsAsync<DlmsException>(() => client.SetAsync(CosemClass.Clock, ObisCode.Parse("0.0.1.0.0.255"), 2, CosemData.DateTime(DateTimeOffset.Now)));
        Assert.Equal(DataAccessResult.ReadWriteDenied, ex.Result);
    }

    [Fact]
    public async Task Low_level_security_unlocks_the_relay_and_rejects_a_wrong_password()
    {
        var (server, meter, listener) = await MeterAsync(DlmsFraming.Hdlc);
        await using var _s = server;
        await using (var wrong = DlmsClient.Create(o => o.UseInMemory(listener).WithPassword("00000000")))
        {
            var ex = await Assert.ThrowsAsync<DlmsException>(async () => await wrong.ConnectAsync());
            Assert.Contains("authentication failure", ex.Message, StringComparison.Ordinal);
        }

        await using var client = DlmsClient.Create(o => { o.UseInMemory(listener).WithPassword("12345678"); o.ReadOnly = false; });
        await client.ConnectAsync();
        await client.ActionAsync(CosemClass.DisconnectControl, ObisCode.Parse("0.0.96.3.10.255"), 1);
        Assert.False(meter.Relay.Connected);
        Assert.False((await client.GetAsync(CosemClass.DisconnectControl, ObisCode.Parse("0.0.96.3.10.255"), 2)).AsBoolean());
        await client.ActionAsync(CosemClass.DisconnectControl, ObisCode.Parse("0.0.96.3.10.255"), 2);
        Assert.True(meter.Relay.Connected);
        await client.SetAsync(CosemClass.Clock, ObisCode.Parse("0.0.1.0.0.255"), 2, CosemData.DateTime(new DateTimeOffset(2026, 10, 8, 19, 31, 0, TimeSpan.FromHours(7))));
        Assert.Equal(TimeSpan.FromMinutes(1), meter.MeterClock.Adjustment);
    }

    [Theory]
    [InlineData(DlmsFraming.Hdlc)]
    [InlineData(DlmsFraming.Wrapper)]
    public async Task High_level_security_authenticates_both_ways_and_ciphers_every_apdu(DlmsFraming framing)
    {
        var (server, meter, listener) = await MeterAsync(framing);
        await using var _s = server;
        var tap = new RecordingTap(200);
        await using var client = DlmsClient.Create(o => { o.UseInMemory(listener).WithHighSecurity(ClientKeys); o.Framing = framing; });
        client.AddTap(tap);
        await client.ConnectAsync();
        Assert.Equal(MeterKeys.SystemTitle, client.ServerSystemTitle);
        var energy = await client.ReadRegisterAsync(ObisCode.Parse("1.0.1.8.0.255"));
        Assert.Equal(Math.Round(meter.ImportWh), energy.Value);
        Assert.True((await client.ReadProfileAsync(ObisCode.Parse("1.0.99.1.0.255"))).Rows.Count > 100);
        // After the AARQ/AARE, no GET travels in clear text.
        Assert.DoesNotContain(tap.Snapshot(), f => f.Summary?.StartsWith("GET", StringComparison.Ordinal) == true);
        Assert.Contains(tap.Snapshot(), f => f.Summary?.Contains("ciphered 0xC8", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task High_level_security_with_wrong_keys_is_refused()
    {
        var (server, _, listener) = await MeterAsync(DlmsFraming.Hdlc);
        await using var _s = server;
        var impostor = ClientKeys with { AuthenticationKey = new byte[16] };
        await using var client = DlmsClient.Create(o => o.UseInMemory(listener).WithHighSecurity(impostor));
        await Assert.ThrowsAnyAsync<IoTComException>(async () => await client.ConnectAsync());
    }

    [Fact]
    public async Task Meter_simulator_integrates_energy_and_captures_profile_rows()
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.FromHours(7));
        var listener = new InMemoryTransportListener("sim");
        await using var server = DlmsServer.Create(o => o.ListenInMemory(listener));
        await using var meter = new DlmsMeterSimulator(server, clock: () => now);
        var rows = meter.LoadProfile.EntriesInUse;
        var export = meter.ExportWh;
        now = now.AddMinutes(30);
        meter.Step();
        Assert.Equal(rows + 2, meter.LoadProfile.EntriesInUse);
        Assert.True(meter.ExportWh > export);                 // midday: the solar system exports
        Assert.Equal(1, meter.Tariff);
        now = now.AddHours(6.5);                               // 19:00, WBP peak
        meter.Step();
        Assert.Equal(2, meter.Tariff);
        Assert.True(meter.ImportPower > 0);
    }
}

public class DlmsConformanceTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("dlms.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Shared_vectors(string json)
    {
        var v = Conformance.Parse(json);
        var wire = v.Hex("wire");
        var valid = v.GetProperty("valid").GetBoolean();
        if (v.GetProperty("kind").GetString() == "hdlc")
        {
            var status = HdlcFrame.TryRead(wire, out var frame, out var n, out _);
            if (!valid)
            {
                Assert.NotEqual(HdlcFrame.ReadStatus.Frame, status);
                return;
            }

            Assert.Equal(HdlcFrame.ReadStatus.Frame, status);
            Assert.Equal(wire.Length, n);
            Assert.Equal(v.GetProperty("control").GetInt32(), frame!.Control);
            Assert.Equal(v.GetProperty("dest").GetString(), frame.Destination.ToString());
            Assert.Equal(v.GetProperty("src").GetString(), frame.Source.ToString());
            Assert.Equal(v.Hex("info"), frame.Information);
            Assert.Equal(v.GetProperty("segmented").GetBoolean(), frame.Segmented);
            Assert.Equal(wire, frame.Encode());
            return;
        }

        var ok = CosemData.TryDecode(wire, out var value, out var consumed, out _) && consumed == wire.Length;
        Assert.Equal(valid, ok);
        if (!valid) return;
        Assert.Equal(v.GetProperty("canonical").GetString(), Canonical(value!));
        Assert.Equal(wire, value!.Encode());
    }

    internal static string Canonical(CosemData d) => d.Type switch
    {
        CosemDataType.Null => "null",
        CosemDataType.Boolean => d.AsBoolean() ? "bool:true" : "bool:false",
        CosemDataType.Int8 => $"i8:{d.AsInt64()}",
        CosemDataType.Int16 => $"i16:{d.AsInt64()}",
        CosemDataType.Int32 => $"i32:{d.AsInt64()}",
        CosemDataType.Int64 => $"i64:{d.AsInt64()}",
        CosemDataType.UInt8 => $"u8:{d.AsInt64()}",
        CosemDataType.UInt16 => $"u16:{d.AsInt64()}",
        CosemDataType.UInt32 => $"u32:{d.AsInt64()}",
        CosemDataType.UInt64 => $"u64:{d}",
        CosemDataType.Enum => $"enum:{d.AsInt64()}",
        CosemDataType.Bcd => $"bcd:{d.AsInt64()}",
        CosemDataType.Float32 => $"f32:{Convert.ToHexString(d.Encode()[1..])}",
        CosemDataType.Float64 => $"f64:{Convert.ToHexString(d.Encode()[1..])}",
        CosemDataType.OctetString => $"octets:{Convert.ToHexString(d.AsBytes())}",
        CosemDataType.VisibleString => $"vis:{Convert.ToHexString(d.AsBytes())}",
        CosemDataType.Utf8String => $"utf8:{Convert.ToHexString(d.AsBytes())}",
        CosemDataType.BitString => $"bits:{d.BitLength}:{Convert.ToHexString(d.AsBytes())}",
        CosemDataType.DateTime => $"dt:{Convert.ToHexString(d.AsBytes())}",
        CosemDataType.Date => $"date:{Convert.ToHexString(d.AsBytes())}",
        CosemDataType.Time => $"time:{Convert.ToHexString(d.AsBytes())}",
        CosemDataType.Array => $"array[{string.Join(",", d.Items.Select(Canonical))}]",
        CosemDataType.Structure => $"struct[{string.Join(",", d.Items.Select(Canonical))}]",
        _ => "?",
    };
}
