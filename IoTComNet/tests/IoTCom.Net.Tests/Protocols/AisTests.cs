using IoTCom.Net.Protocols.Nmea;

namespace IoTCom.Net.Tests.Protocols;

public class AisTests
{
    [Fact]
    public void Published_position_report_decodes()
    {
        var m = Assert.IsType<AisPositionReport>(new AisDecoder().Feed("!AIVDM,1,1,,B,15NG6V0P01G?cFhE`R2IU?wn28R>,0*05"));
        Assert.Equal(1, m.Type);
        Assert.Equal(367_380_120u, m.Mmsi);                  // cross-checked with an independent decoder
        Assert.Equal(AisNavigationStatus.UnderWayUsingEngine, m.Status);
        Assert.Equal(0.1, m.SpeedOverGround);
        Assert.False(m.HighAccuracy);
        Assert.Equal(-122.404333, m.Longitude!.Value, 5);
        Assert.Equal(37.806948, m.Latitude!.Value, 5);
        Assert.Equal(245.2, m.CourseOverGround);
        Assert.Null(m.Heading);                               // 511 = not available
        Assert.Equal(59, m.Second);
    }

    [Fact]
    public void Two_sentence_static_and_voyage_data_is_reassembled()
    {
        var decoder = new AisDecoder();
        Assert.Null(decoder.Feed("!AIVDM,2,1,1,A,55?MbV02;H;s<HtKR20EHE:0@T4@Dn2222222216L961O5Gf0NSQEp6ClRp8,0*1C"));
        var s = Assert.IsType<AisStaticData>(decoder.Feed("!AIVDM,2,2,1,A,88888888880,2*25"));
        Assert.Equal(351_759_000u, s.Mmsi);
        Assert.Equal(9_134_270u, s.Imo);
        Assert.Equal("3FOF8", s.CallSign);
        Assert.Equal("EVER DIADEM", s.Name);
        Assert.Equal(70, s.ShipType);
        Assert.Equal("Cargo", Ais.ShipTypeName(s.ShipType));
        Assert.Equal(295, s.Length);
        Assert.Equal(32, s.Beam);
        Assert.Equal(12.2, s.Draught);
        Assert.Equal("NEW YORK", s.Destination);
        Assert.Equal("05-15 14:00", s.Eta);
    }

    [Fact]
    public void A_second_fragment_without_its_first_is_dropped()
    {
        var decoder = new AisDecoder();
        Assert.Null(decoder.Feed("!AIVDM,2,2,1,A,88888888880,2*25"));
        Assert.Equal(1, decoder.Dropped);
        Assert.Null(decoder.Decode("15NG6V0P01G?cFhE`R2IU?wn28R~"));   // '~' is not a 6-bit character
        Assert.Equal(2, decoder.Dropped);
        Assert.Null(decoder.Feed("$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47"));
    }

    [Fact]
    public void Encoders_round_trip_through_sentences()
    {
        var position = new AisPositionReport(1, 0, 525_005_123)
        {
            Status = AisNavigationStatus.UnderWayUsingEngine, RateOfTurn = 0, SpeedOverGround = 11.5, HighAccuracy = true,
            Latitude = -6.0912, Longitude = 106.8834, CourseOverGround = 175.3, Heading = 175, Second = 12,
        };
        var decoder = new AisDecoder();
        var back = Assert.IsType<AisPositionReport>(decoder.Feed(Assert.Single(AisBits.ToSentences(AisBits.EncodePosition(position)))));
        Assert.Equal(position with { RateOfTurn = 0 }, back);

        var classB = new AisPositionReport(18, 0, 525_200_654) { SpeedOverGround = 7, Latitude = -6.06, Longitude = 106.76, CourseOverGround = 290, Heading = 290, Second = 5, HighAccuracy = true };
        Assert.Equal(classB, decoder.Feed(Assert.Single(AisBits.ToSentences(AisBits.EncodePosition(classB)))));

        var stat = new AisStaticData(5, 0, 525_012_456) { Imo = 9_812_345, CallSign = "PMLQ", Name = "PERTAMINA GAS 2", ShipType = 84, Length = 230, Beam = 36, Eta = "10-12 06:00", Draught = 10.6, Destination = "BALONGAN" };
        var lines = AisBits.ToSentences(AisBits.EncodeStatic(stat), 'B', 4);
        Assert.Equal(2, lines.Count);
        Assert.Null(decoder.Feed(lines[0]));
        Assert.Equal(stat, decoder.Feed(lines[1]));
    }

    [Fact]
    public void Simulator_feeds_a_tracker()
    {
        var sim = new AisSimulator();
        var decoder = new AisDecoder();
        var tracker = new AisTracker();
        for (var i = 0; i < 6; i++)
            foreach (var line in sim.Step(TimeSpan.FromSeconds(10)))
                if (decoder.Feed(line) is { } m) tracker.Apply(m);
        Assert.Equal(0, decoder.Dropped);
        Assert.Equal(7, tracker.Vessels.Count);
        var tanker = tracker.Vessels.Single(v => v.Mmsi == 525_012_456);
        Assert.Equal("PERTAMINA GAS 2", tanker.Name);
        Assert.Equal("BALONGAN", tanker.Destination);
        Assert.InRange(tanker.Latitude!.Value, -6.1, -5.9);
        var fisher = tracker.Vessels.Single(v => v.Mmsi == 525_200_654);
        Assert.True(fisher.ClassB);
        Assert.Equal("KM BINTANG LAUT", fisher.Name);
        Assert.Equal(30, fisher.ShipType);                   // type 24 part B
    }
}
