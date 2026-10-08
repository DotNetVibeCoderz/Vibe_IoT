using System.Text;
using IoTCom.Net.Protocols.AtCommand;
using IoTCom.Net.Transports;

namespace IoTCom.Net.Tests.Protocols;

public class AtParserTests
{
    private static (List<AtResponse> Responses, List<AtUrc> Urcs) Run(string command, params string[] chunks)
    {
        var p = new AtParser();
        p.Begin(command);
        foreach (var c in chunks) p.Feed(Encoding.ASCII.GetBytes(c));
        var responses = new List<AtResponse>();
        var urcs = new List<AtUrc>();
        while (p.TryTakeResponse(out var r)) responses.Add(r!);
        while (p.TryTakeUrc(out var u)) urcs.Add(u!);
        return (responses, urcs);
    }

    [Fact]
    public void Echo_information_and_ok_split_across_reads()
    {
        var (responses, urcs) = Run("AT+CSQ", "AT+CSQ\r\r\n+CS", "Q: 21,99\r\n\r\nO", "K\r\n");
        var r = Assert.Single(responses);
        Assert.Equal(AtResult.Ok, r.Result);
        Assert.Equal(["21", "99"], r.Values("+CSQ"));
        Assert.Empty(urcs);
    }

    [Fact]
    public void Urcs_in_the_middle_of_a_response_are_separated()
    {
        var (responses, urcs) = Run("AT+CGMR", "\r\n+CEREG: 1\r\nIOTSIM01A07M1G\r\n+CMTI: \"ME\",3\r\n\r\nOK\r\n");
        Assert.Equal(["IOTSIM01A07M1G"], Assert.Single(responses).Lines);
        Assert.Equal(["+CEREG", "+CMTI"], urcs.Select(u => u.Name));
        Assert.Equal(["ME", "3"], urcs[1].Values);
    }

    [Fact]
    public void The_command_own_prefix_is_not_a_urc()
    {
        var (responses, urcs) = Run("AT+CEREG?", "\r\n+CEREG: 2,1,\"1A2B\",\"01C3F07\",7\r\n\r\nOK\r\n");
        Assert.Equal(["2", "1", "1A2B", "01C3F07", "7"], Assert.Single(responses).Values("+CEREG"));
        Assert.Empty(urcs);
    }

    [Theory]
    [InlineData("\r\n+CME ERROR: 10\r\n", AtResult.CmeError, 10, null)]
    [InlineData("\r\n+CME ERROR: SIM PIN required\r\n", AtResult.CmeError, null, "SIM PIN required")]
    [InlineData("\r\n+CMS ERROR: 331\r\n", AtResult.CmsError, 331, null)]
    [InlineData("\r\nERROR\r\n", AtResult.Error, null, null)]
    [InlineData("\r\nNO CARRIER\r\n", AtResult.NoCarrier, null, null)]
    [InlineData("\r\n> ", AtResult.Prompt, null, null)]
    [InlineData("\r\nCONNECT 115200\r\n", AtResult.Connect, null, null)]
    public void Final_result_codes(string reply, AtResult result, int? code, string? text)
    {
        var r = Assert.Single(Run("AT+X", reply).Responses);
        Assert.Equal((result, code, text), (r.Result, r.ErrorCode, r.ErrorText));
    }

    [Fact]
    public void Lines_without_a_pending_command_are_urcs() =>
        Assert.Equal(["RDY", "+CPIN: READY", "RING"], Run("AT", "\r\nOK\r\n\r\nRDY\r\n+CPIN: READY\r\nRING\r\n").Urcs.Select(u => u.Line));

    [Fact]
    public void Values_respect_quotes() => Assert.Equal(["0", "0", "Telkomsel, ID", "8"], AtParser.SplitValues(" 0,0,\"Telkomsel, ID\",8"));
}

public class AtModemTests
{
    private static async Task<(AtModemSimulator Sim, AtModem Modem)> StartAsync(string? pin = null, bool readOnly = false)
    {
        var link = new InMemoryTransportListener("modem");
        var sim = AtModemSimulator.Create(o => { o.ListenInMemory(link); o.RegistrationDelay = TimeSpan.FromMilliseconds(300); o.Pin = pin; });
        await sim.StartAsync();
        var modem = AtModem.Create(o => { o.UseInMemory(link); o.ReadOnly = readOnly; });
        await modem.ConnectAsync();
        return (sim, modem);
    }

    [Fact]
    public async Task Info_registration_urc_and_signal()
    {
        var (sim, modem) = await StartAsync();
        await using var _s = sim;
        await using var _m = modem;
        await modem.SendCheckedAsync("AT+CEREG=2");
        var urc = new TaskCompletionSource<AtUrc>(TaskCreationOptions.RunContinuationsAsynchronously);
        modem.UrcReceived += (_, u) => { if (u.Name == "+CEREG") urc.TrySetResult(u); };
        var registered = await urc.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("1", registered.Values[0]);

        var info = await modem.GetInfoAsync();
        Assert.Equal("867123050123456", info.Imei);
        Assert.Equal("READY", info.SimStatus);
        Assert.Equal(AtRegistration.Home, info.Registration);
        Assert.Equal("Telkomsel", info.Operator);
        Assert.Equal("LTE-M", info.AccessTechnology);
        Assert.NotNull(info.Signal.Dbm);
        Assert.Equal(AtResult.Error, (await modem.SendAsync("AT+NOPE")).Result);
    }

    [Fact]
    public async Task Sms_out_and_in()
    {
        var (sim, modem) = await StartAsync();
        await using var _s = sim;
        await using var _m = modem;
        await Task.Delay(500);   // registration
        var reference = await modem.SendSmsAsync("+6281234567890", "Pompa air nyala");
        Assert.Equal(1, reference);
        Assert.Equal(("+6281234567890", "Pompa air nyala"), Assert.Single(sim.SentSms));

        var arrived = new TaskCompletionSource<AtUrc>(TaskCreationOptions.RunContinuationsAsynchronously);
        modem.UrcReceived += (_, u) => { if (u.Name == "+CMTI") arrived.TrySetResult(u); };
        sim.DeliverSms("+6289876543210", "STATUS?");
        var cmti = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var sms = await modem.ReadSmsAsync(int.Parse(cmti.Values[1], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(("REC UNREAD", "+6289876543210", "STATUS?"), (sms.Status, sms.Sender, sms.Text));
    }

    [Fact]
    public async Task Pin_errors_and_read_only_mode()
    {
        var (sim, modem) = await StartAsync(pin: "1234", readOnly: true);
        await using var _s = sim;
        await using var _m = modem;
        var locked = await modem.SendAsync("AT+CSQ");
        Assert.Equal((AtResult.CmeError, "SIM PIN required"), (locked.Result, locked.ErrorText));
        Assert.Equal("incorrect password", (await modem.SendAsync("AT+CPIN=\"0000\"")).ErrorText);
        await modem.SendCheckedAsync("AT+CPIN=\"1234\"");
        Assert.True((await modem.SendAsync("AT+CSQ")).IsSuccess);
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => modem.SendAsync("AT+CFUN=0"));
        await Assert.ThrowsAsync<ReadOnlyModeException>(() => modem.SendSmsAsync("+62811", "x"));
    }
}
