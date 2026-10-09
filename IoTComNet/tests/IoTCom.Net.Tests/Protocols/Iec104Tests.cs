using System.Buffers;
using System.Collections.Concurrent;
using IoTCom.Net.Framing;
using IoTCom.Net.Protocols.Iec104;
using IoTCom.Net.Transports;
using Sim = IoTCom.Net.Protocols.Iec104.Iec104SubstationSimulator;

namespace IoTCom.Net.Tests.Protocols;

public class Iec104CodecTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    [Theory]
    [InlineData("680407000000", Iec104UFunction.StartDtActivation)]
    [InlineData("68040B000000", Iec104UFunction.StartDtConfirmation)]
    [InlineData("680413000000", Iec104UFunction.StopDtActivation)]
    [InlineData("680423000000", Iec104UFunction.StopDtConfirmation)]
    [InlineData("680443000000", Iec104UFunction.TestFrActivation)]
    [InlineData("680483000000", Iec104UFunction.TestFrConfirmation)]
    public void U_frames(string hex, Iec104UFunction function)
    {
        var apdu = Iec104Apdu.Parse(H(hex));
        Assert.Equal(Iec104Format.U, apdu.Format);
        Assert.Equal(function, apdu.Function);
        Assert.Equal(H(hex), Iec104Apdu.U(function).Encode());
    }

    [Fact]
    public void S_and_I_frames_with_a_general_interrogation()
    {
        Assert.Equal(H("68 04 01 00 04 00"), Iec104Apdu.S(2).Encode());
        Assert.Equal(32767, Iec104Apdu.Parse(H("68 04 01 00 FE FF")).ReceiveSequence);

        // C_IC_NA_1 act, CA 1, QOI 20 — the classic first I frame of every SCADA session.
        var gi = H("68 0E 00 00 00 00 64 01 06 00 01 00 00 00 00 14");
        var apdu = Iec104Apdu.Parse(gi);
        Assert.Equal((Iec104Format.I, (ushort)0, (ushort)0), (apdu.Format, apdu.SendSequence, apdu.ReceiveSequence));
        var asdu = Iec104Asdu.Parse(apdu.Asdu.Span);
        Assert.Equal(Iec104TypeId.Interrogation, asdu.Type);
        Assert.Equal(Iec104Cause.Activation, asdu.Cause);
        Assert.Equal(20, asdu.Objects.Single().Qualifier);
        Assert.Equal(gi, Iec104Apdu.I(0, 0, new Iec104Asdu(Iec104TypeId.Interrogation, Iec104Cause.Activation, 1, [new Iec104Object(0, Qualifier: 20)]).Encode()).Encode());
        var seq = Iec104Apdu.Parse(Iec104Apdu.I(300, 32767, gi.AsMemory(6)).Encode());
        Assert.Equal((300, 32767), (seq.SendSequence, seq.ReceiveSequence));
    }

    [Fact]
    public void Time_tagged_float_with_cp56()
    {
        var time = new Cp56Time2a(new DateTime(2026, 10, 9, 6, 30, 15, 250));
        var asdu = new Iec104Asdu(Iec104TypeId.MeasuredFloatTime, Iec104Cause.Spontaneous, 1, [new Iec104Object(2001, 20, Iec104Quality.None, 0, time)]);
        var bytes = asdu.Encode();
        Assert.Equal(H("24 01 03 00 01 00 D1 07 00 0000A041 00 923B 1E 06 09 0A 1A"), bytes);
        var back = Iec104Asdu.Parse(bytes);
        Assert.Equal(20, back.Objects[0].Value);
        Assert.Equal(time, back.Objects[0].Time);
        Assert.Equal(new Cp56Time2a(time.Value, Invalid: true, SummerTime: true), Cp56Time2a.Decode(H("923B 9E 86 09 0A 1A")));
        Assert.Throws<ProtocolException>(() => Cp56Time2a.Decode(H("923B 1E 06 1F 02 1A")));   // 31 February
        Assert.Throws<ProtocolException>(() => Cp56Time2a.Decode(H("70EA 1E 06 09 0A 1A")));   // 60 000 ms
    }

    [Fact]
    public void Elements_quality_and_sequences()
    {
        // M_ST_NA_1: −3 with the transient bit, quality invalid.
        var step = Iec104Asdu.Parse(H("05 01 14 00 01 00 D7 07 00 FD 80"));
        Assert.Equal(-3, step.Objects[0].Value);
        Assert.Equal(Iec104Quality.Transient | Iec104Quality.Invalid, step.Objects[0].Quality);
        Assert.Equal(Iec104Cause.InterrogatedByStation, step.Cause);
        Assert.Equal(H("05 01 14 00 01 00 D7 07 00 FD 80"), step.Encode());

        // M_ME_NA_1 normalized −0.5 blocked, M_ME_NB_1 scaled −1234, M_DP_NA_1 SQ=1 with three objects.
        Assert.Equal(-0.5, Iec104Asdu.Parse(H("09 01 03 00 01 00 01 00 00 00C0 10")).Objects[0].Value);
        Assert.Equal(-1234, Iec104Asdu.Parse(H("0B 01 03 00 01 00 01 00 00 2EFB 00")).Objects[0].Value);
        var dp = Iec104Asdu.Parse(H("03 83 14 00 01 00 E9 03 00 02 01 43"));
        Assert.True(dp.Sequence);
        Assert.Equal([1001u, 1002u, 1003u], dp.Objects.Select(o => o.Address));
        Assert.Equal([Iec104DoublePoint.On, Iec104DoublePoint.Off, Iec104DoublePoint.Indeterminate], dp.Objects.Select(o => o.DoublePoint));
        Assert.Equal(Iec104Quality.NotTopical, dp.Objects[2].Quality);
        Assert.Equal(H("03 83 14 00 01 00 E9 03 00 02 01 43"), dp.Encode());

        // M_IT_NA_1 counter with sequence 7, carry and adjusted.
        var it = Iec104Asdu.Parse(H("0F 01 25 00 01 00 B9 0B 00 4E8E1300 67"));
        Assert.Equal(1_281_614, it.Objects[0].Value);
        Assert.Equal(7, it.Objects[0].Qualifier);
        Assert.Equal(Iec104Quality.Carry | Iec104Quality.Adjusted, it.Objects[0].Quality);

        // C_DC_NA_1 select, close; C_SE_NC_1 with QOS.
        var sel = Iec104Asdu.Parse(H("2E 01 06 00 01 00 89 13 00 82"));
        Assert.True(sel.Objects[0].Select);
        Assert.Equal(Iec104DoublePoint.On, sel.Objects[0].DoublePoint);
        Assert.Equal(1.5, Iec104Asdu.Parse(H("32 01 06 00 01 00 71 17 00 0000C03F 00")).Objects[0].Value);

        Assert.Equal(Iec104TypeId.MeasuredFloat, Iec104Types.WithoutTime(Iec104TypeId.MeasuredFloatTime));
        Assert.Equal(Iec104TypeId.IntegratedTotals, Iec104Types.WithoutTime(Iec104TypeId.IntegratedTotalsTime));
        Assert.Equal(Iec104TypeId.DoubleCommand, Iec104Types.WithoutTime(Iec104TypeId.DoubleCommandTime));
        Assert.Equal(Iec104TypeId.SinglePointTime, Iec104Types.WithTime(Iec104TypeId.SinglePoint));
        Assert.Equal("M_ME_TF_1", Iec104Types.Mnemonic(Iec104TypeId.MeasuredFloatTime));
    }

    [Fact]
    public void Malformed_input_is_rejected()
    {
        Assert.Throws<ProtocolException>(() => Iec104Asdu.Parse(H("0D 02 03 00 01 00 D1 07 00 000000A041 00")));   // 2 objects announced, 1 present
        Assert.Throws<ProtocolException>(() => Iec104Asdu.Parse(H("FA 01 03 00 01 00 D1 07 00 00")));            // unknown type
        Assert.Throws<ProtocolException>(() => Iec104Asdu.Parse(H("0D 00 03 00 01 00")));                         // no objects
        Assert.Throws<ProtocolException>(() => Iec104Apdu.Parse(H("68 04 03 00 00 00")));                         // U frame with no function
        Assert.Throws<ProtocolException>(() => Iec104Apdu.Parse(H("68 05 01 00 00 00 00")));                      // S frame with payload
        Assert.Throws<ArgumentException>(() => new Iec104Asdu(Iec104TypeId.MeasuredFloatTime, Iec104Cause.Spontaneous, 1,
            [.. Enumerable.Range(0, 20).Select(i => new Iec104Object((uint)i, Time: new Cp56Time2a(DateTime.Now)))]).Encode());   // 6 + 20 × 15 > 249

        var fields = Iec104Apdu.Describe(H("68 0E 00 00 02 00 64 01 07 00 01 00 00 00 00 14"));
        Assert.Contains(fields, f => f.Name == "Type" && f.Value == "C_IC_NA_1 (100)");
        Assert.Contains(fields, f => f.Name == "COT" && f.Value == "activation confirmation");
        Assert.Contains(fields, f => f.Name == "Control" && f.Value == "I N(S)=0 N(R)=1");
    }

    [Fact]
    public void Framing_skips_noise_and_waits_for_whole_frames()
    {
        var framing = new Iec104Framing();
        var stream = new List<byte> { 0x00, 0xFF };
        stream.AddRange(H("680407000000"));
        stream.AddRange(H("68 0E 00 00 00 00 64 01 06 00 01 00 00 00 00 14"));
        var frames = new List<byte[]>();
        var buffer = new ReadOnlySequence<byte>(stream.ToArray()[..12]);   // second frame incomplete
        var payload = new ArrayBufferWriter<byte>();
        while (framing.TryDecode(ref buffer, payload) == FrameDecodeStatus.Frame)
        {
            frames.Add(payload.WrittenSpan.ToArray());
            payload.ResetWrittenCount();
        }

        Assert.Single(frames);
        buffer = new ReadOnlySequence<byte>([.. buffer.ToArray(), .. stream.ToArray()[12..]]);
        Assert.Equal(FrameDecodeStatus.Frame, framing.TryDecode(ref buffer, payload));
        Assert.Equal(16, payload.WrittenCount);
        var bad = new ReadOnlySequence<byte>(H("68 02 00 00"));                 // length below 4
        Assert.Equal(FrameDecodeStatus.Invalid, framing.TryDecode(ref bad, payload));
    }
}

public class Iec104SessionTests
{
    private static async Task Until(Func<bool> condition, string what)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), $"timed out waiting for {what}");
    }

    private static async Task<(Sim Sim, InMemoryTransportListener Listener)> StationAsync(Action<Iec104ServerOptions>? configure = null)
    {
        var listener = new InMemoryTransportListener("rtu");
        var sim = Sim.Create(o =>
        {
            o.ListenInMemory(listener);
            configure?.Invoke(o);
        });
        await sim.StartAsync();
        return (sim, listener);
    }

    [Fact]
    public async Task Interrogations_reads_and_spontaneous_changes()
    {
        var (sim, listener) = await StationAsync();
        await using var _ = sim;
        await using var scada = Iec104Client.Create(o => o.UseInMemory(listener));
        var spontaneous = new ConcurrentQueue<Iec104PointValue>();
        scada.PointReceived += p => { if (p.Cause == Iec104Cause.Spontaneous) spontaneous.Enqueue(p); };
        await scada.ConnectAsync();

        var gi = await scada.InterrogateAsync();
        Assert.Equal(13, gi.Count);   // everything but the counter
        Assert.All(gi, p => Assert.Equal(Iec104Cause.InterrogatedByStation, p.Cause));
        Assert.Equal(Iec104DoublePoint.On, gi.Single(p => p.Object.Address == Sim.Ioa.Breaker).Object.DoublePoint);
        Assert.Equal(Iec104TypeId.MeasuredFloat, gi.Single(p => p.Object.Address == Sim.Ioa.Voltage).Type);

        var group1 = await scada.InterrogateAsync(21);
        Assert.Equal(6, group1.Count);
        Assert.All(group1, p => Assert.Equal((Iec104Cause)21, p.Cause));

        var counters = await scada.CounterInterrogateAsync();
        var energy = Assert.Single(counters);
        Assert.InRange(energy.Object.Value, 1_284_000, 1_300_000);
        Assert.Equal(1, energy.Object.Qualifier);   // sequence number
        Assert.Equal(2, (await scada.CounterInterrogateAsync()).Single().Object.Qualifier);

        var tap = await scada.ReadAsync(Sim.Ioa.TapPosition);
        Assert.Equal(Iec104TypeId.StepPosition, tap.Type);
        Assert.Equal(Iec104Cause.Request, tap.Cause);
        var unknown = await Assert.ThrowsAsync<DeviceException>(() => scada.ReadAsync(4242));
        Assert.Equal((int)Iec104Cause.UnknownObjectAddress, unknown.Code);

        sim.Trip();
        await Until(() => spontaneous.Any(p => p.Object.Address == Sim.Ioa.Breaker && p.Object.DoublePoint == Iec104DoublePoint.Off), "breaker open");
        var trip = spontaneous.First(p => p.Object.Address == Sim.Ioa.ProtectionTrip);
        Assert.Equal(Iec104TypeId.SinglePointTime, trip.Type);
        Assert.NotNull(trip.Object.Time);
        Assert.Equal(1, scada.Points[(1, Sim.Ioa.ProtectionTrip)].Object.Value);
    }

    [Fact]
    public async Task Commands_follow_select_before_operate_interlocks_and_read_only()
    {
        var (sim, listener) = await StationAsync(o => o.RequireSelectBeforeOperate = true);
        await using var _ = sim;
        var results = new ConcurrentQueue<Iec104CommandResult>();
        sim.Server.CommandHandled += results.Enqueue;

        await using (var viewer = Iec104Client.Create(o => o.UseInMemory(listener)))
        {
            await viewer.ConnectAsync();
            await Assert.ThrowsAsync<ReadOnlyModeException>(() => viewer.DoubleCommandAsync(Sim.Ioa.BreakerCommand, false, true));
            await Assert.ThrowsAsync<ReadOnlyModeException>(() => viewer.ClockSyncAsync());
        }

        await using var scada = Iec104Client.Create(o => o.UseInMemory(listener).AllowCommands());
        var breaker = new ConcurrentQueue<double>();
        scada.PointReceived += p => { if (p.Object.Address == Sim.Ioa.Breaker && p.Cause == Iec104Cause.ReturnRemote) breaker.Enqueue(p.Object.Value); };
        await scada.ConnectAsync();

        var direct = await Assert.ThrowsAsync<DeviceException>(() => scada.DoubleCommandAsync(Sim.Ioa.BreakerCommand, false));   // execute without select
        Assert.Contains("negative", direct.Message, StringComparison.Ordinal);
        Assert.Equal("not selected", results.Last().Reason);

        var term = await scada.DoubleCommandAsync(Sim.Ioa.BreakerCommand, false, selectBeforeOperate: true);   // open
        Assert.Equal(Iec104Cause.ActivationTermination, term.Cause);
        await Until(() => breaker.Contains(1), "breaker open");
        Assert.Equal(0, breaker.First());                                                        // intermediate while travelling

        await Assert.ThrowsAsync<DeviceException>(() => scada.DoubleCommandAsync(Sim.Ioa.EarthingCommand, true, true));  // disconnector still closed
        await scada.DoubleCommandAsync(Sim.Ioa.DisconnectorCommand, false, true);
        await scada.DoubleCommandAsync(Sim.Ioa.EarthingCommand, true, true);
        await Until(() => sim.Server.Points[Sim.Ioa.EarthingSwitch].Value == 2, "earthed");
        await Assert.ThrowsAsync<DeviceException>(() => scada.DoubleCommandAsync(Sim.Ioa.BreakerCommand, true, true)); // never close onto earth

        await scada.RegulatingStepAsync(Sim.Ioa.TapCommand, higher: true, selectBeforeOperate: true);
        Assert.Equal(1, sim.Server.Points[Sim.Ioa.TapPosition].Value);
        await scada.SetpointAsync(Sim.Ioa.ReactiveSetpoint, 2.5, selectBeforeOperate: true);
        await Assert.ThrowsAsync<DeviceException>(() => scada.SetpointAsync(Sim.Ioa.ReactiveSetpoint, 50, selectBeforeOperate: true));
        var noSuchCommand = await Assert.ThrowsAsync<DeviceException>(() => scada.SingleCommandAsync(9999, true, true));
        Assert.Equal((int)Iec104Cause.UnknownObjectAddress, noSuchCommand.Code);

        await scada.ClockSyncAsync(DateTime.Now.AddHours(-1));
        Assert.InRange(sim.Server.ClockOffset.TotalMinutes, -61, -59);
    }

    [Fact]
    public async Task Windows_test_frames_and_timeouts()
    {
        // k = 2, w = 1: a general interrogation of 300 points needs many acknowledged round trips.
        var listener = new InMemoryTransportListener("big");
        await using var rtu = Iec104Server.Create(o =>
        {
            o.ListenInMemory(listener);
            o.Link.K = 2;
            o.Link.W = 1;
        });
        for (uint i = 0; i < 300; i++) rtu.Define(100 + i, Iec104TypeId.MeasuredScaled, i);
        await rtu.StartAsync();
        await using (var scada = Iec104Client.Create(o =>
        {
            o.UseInMemory(listener);
            o.Link.W = 1;
            o.Link.T3 = TimeSpan.FromMilliseconds(150);
        }))
        {
            Assert.Equal(300, (await scada.InterrogateAsync()).Count);
            var test = 0;
            scada.AddTap(new CountingTap(f => { if (f.Direction == FrameDirection.Outbound && f.Data.Span.SequenceEqual(Iec104Apdu.U(Iec104UFunction.TestFrActivation).Encode())) Interlocked.Increment(ref test); }));
            await Task.Delay(1000);
            Assert.True(test >= 1, "TESTFR act after t3");
            Assert.True(scada.IsConnected);
        }

        // A peer that never acknowledges: the station gives up after t1.
        var silent = new InMemoryTransportListener("silent");
        await using var rtu2 = Iec104Server.Create(o =>
        {
            o.ListenInMemory(silent);
            o.Link.T1 = TimeSpan.FromMilliseconds(300);
            o.Link.T3 = TimeSpan.FromSeconds(30);
        });
        rtu2.Define(1, Iec104TypeId.SinglePoint);
        await rtu2.StartAsync();
        await using var raw = silent.Connect();
        await raw.OpenAsync(default);
        await raw.Pipe.Output.WriteAsync(Iec104Apdu.U(Iec104UFunction.StartDtActivation).Encode());
        await Until(() => rtu2.ActiveConnectionCount == 1, "STARTDT");
        rtu2.Update(1, 1);
        await Until(() => rtu2.ConnectionCount == 0, "t1 close");

        // A peer that skips a sequence number is disconnected.
        await using var raw2 = silent.Connect();
        await raw2.OpenAsync(default);
        await Until(() => rtu2.ConnectionCount == 1, "second peer");
        await raw2.Pipe.Output.WriteAsync(Iec104Apdu.I(5, 0, new Iec104Asdu(Iec104TypeId.Interrogation, Iec104Cause.Activation, 1, [new Iec104Object(0, Qualifier: 20)]).Encode()).Encode());
        await Until(() => rtu2.ConnectionCount == 0, "sequence error close");
    }

    [Fact]
    public async Task Unknown_types_causes_and_stations_are_answered_negatively()
    {
        var (sim, listener) = await StationAsync();
        await using var _ = sim;
        await using var raw = listener.Connect();
        await raw.OpenAsync(default);
        var replies = new ConcurrentQueue<Iec104Asdu>();
        var frames = Task.Run(async () =>
        {
            await foreach (var f in raw.Pipe.Input.ReadFramesAsync(new Iec104Framing()))
            {
                var a = Iec104Apdu.Parse(f);
                if (a.Format == Iec104Format.I) replies.Enqueue(Iec104Asdu.ParseHeader(a.Asdu.Span));
            }
        });
        ushort ns = 0;
        async Task Send(byte[] asdu) => await raw.Pipe.Output.WriteAsync(Iec104Apdu.I(ns++, 0, asdu).Encode());
        await raw.Pipe.Output.WriteAsync(Iec104Apdu.U(Iec104UFunction.StartDtActivation).Encode());
        await Send([0xFA, 0x01, 0x06, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00]);                                                   // type 250
        await Send(new Iec104Asdu(Iec104TypeId.Interrogation, Iec104Cause.Activation, 77, [new Iec104Object(0, Qualifier: 20)]).Encode());   // CA 77
        await Send(new Iec104Asdu(Iec104TypeId.SingleCommand, Iec104Cause.Spontaneous, 1, [new Iec104Object(Sim.Ioa.TripReset, 1)]).Encode()); // wrong cause
        await Until(() => replies.Count(r => r.Negative) >= 3, "three negative replies");
        Assert.Contains(replies, r => r.Cause == Iec104Cause.UnknownType && (byte)r.Type == 0xFA);
        Assert.Contains(replies, r => r.Cause == Iec104Cause.UnknownCommonAddress && r.CommonAddress == 77);
        Assert.Contains(replies, r => r.Cause == Iec104Cause.UnknownCause && r.Type == Iec104TypeId.SingleCommand);
        Assert.Contains(replies, r => r.Type == Iec104TypeId.EndOfInitialisation);
    }

    private sealed class CountingTap(Action<TrafficFrame> onFrame) : ITrafficTap
    {
        public void OnFrame(in TrafficFrame frame) => onFrame(frame);
    }
}

public class Iec104ConformanceTests
{
    public static IEnumerable<object[]> Vectors => Conformance.Cases("iec104.json");

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Shared_vectors(string json)
    {
        var v = Conformance.Parse(json);
        var data = v.Hex("data");
        var expected = v.GetProperty("fields").GetString();
        if (expected == "error")
        {
            Assert.ThrowsAny<ProtocolException>(() => Canonical(data));
            return;
        }

        Assert.Equal(expected, Canonical(data));
    }

    /// <summary>The shared canonical text: APCI, then ASDU header and objects with raw-ish values.</summary>
    internal static string Canonical(byte[] frame)
    {
        var apdu = Iec104Apdu.Parse(frame);
        var head = apdu.Format switch
        {
            Iec104Format.I => $"I|{apdu.SendSequence}|{apdu.ReceiveSequence}",
            Iec104Format.S => $"S|{apdu.ReceiveSequence}",
            _ => $"U|{(byte)apdu.Function}",
        };
        if (apdu.Format != Iec104Format.I) return head;
        var a = Iec104Asdu.Parse(apdu.Asdu.Span);
        var objects = a.Objects.Select(o =>
            $"{o.Address}:{Num(o.Value)}:{(ushort)o.Quality}:{o.Qualifier}:{(o.Time is { } t ? $"{t.Value:yyyy-MM-dd HH:mm:ss.fff}{(t.Invalid ? "I" : "")}{(t.SummerTime ? "S" : "")}" : "-")}");
        return $"{head}|{(byte)a.Type}|{(a.Sequence ? 1 : 0)}|{(byte)a.Cause}|{(a.Negative ? 1 : 0)}|{(a.Test ? 1 : 0)}|{a.Originator}|{a.CommonAddress}|{string.Join(";", objects)}";
    }

    private static string Num(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
