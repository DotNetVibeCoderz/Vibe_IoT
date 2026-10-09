using System.ComponentModel;
using IoTCom.Net.Protocols.Nfc;
using Spectre.Console;
using Spectre.Console.Cli;

namespace IoTCom.Net.Cli.Commands;

internal class NfcSettings : CommandSettings
{
    [CommandOption("-r|--reader"), Description("PC/SC reader name or part of it (default: the first reader).")]
    public string? Reader { get; init; }

    [CommandOption("--sim"), Description("Use a virtual reader with a simulated NTAG213 asset tag.")]
    public bool Sim { get; init; }

    [CommandOption("--timeout"), Description("Seconds to wait for a tag (default 30).")]
    public int TimeoutSeconds { get; init; } = 30;

    public static VirtualType2Tag SampleTag() => new(Type2TagKind.Ntag213, [0x04, 0xA2, 0x4B, 0x1A, 0x2C, 0x5E, 0x80],
        new NdefMessage([NdefRecord.SmartPoster("https://docs.example.com/pump-7", "Pump 7 manual"), NdefRecord.Text("Asset P-0007")]), label: "Pump 7 asset tag");

    public async Task<ISmartCardChannel> WaitAsync(CancellationToken ct)
    {
        INfcReader reader;
        if (Sim)
        {
            var virtualReader = new VirtualNfcReader();
            virtualReader.Present(SampleTag());
            reader = virtualReader;
        }
        else
        {
            reader = PcscNfcReader.Open(Reader);
        }

        Ui.Success($"Hold a tag on {Markup.Escape(reader.Name)}…");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        try
        {
            return await reader.WaitForTagAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IoTComTimeoutException($"No tag within {TimeoutSeconds} s.");
        }
    }
}

internal sealed class NfcReadersCommand : Command<EmptyCommandSettings>
{
    public override int Execute(CommandContext context, EmptyCommandSettings settings, CancellationToken ct)
    {
        var readers = Pcsc.ListReaders();
        if (readers.Count == 0)
        {
            Ui.Warn("No PC/SC readers are connected (or the smart card service is not running).");
            return 0;
        }

        foreach (var r in readers) AnsiConsole.MarkupLine($"  [{Ui.Hex(Ui.CableBlue)}]●[/] {Markup.Escape(r)}");
        return 0;
    }
}

internal sealed class NfcReadCommand : AsyncCommand<NfcReadCommand.Settings>
{
    public sealed class Settings : NfcSettings
    {
        [CommandOption("--dump"), Description("Also print the memory page by page.")]
        public bool Dump { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        await using var card = await s.WaitAsync(ct);
        var client = new Type2TagClient(card);
        var uid = await client.GetUidAsync(ct);
        AnsiConsole.MarkupLine($"UID [bold]{Convert.ToHexString(uid)}[/]  ATR {Convert.ToHexString(card.Atr.Span)}");
        var (cc, data) = await client.ReadDataAreaAsync(ct);
        AnsiConsole.MarkupLine($"Capability container {Convert.ToHexString(cc)}: NDEF {cc[1] >> 4}.{cc[1] & 0xF}, {cc[2] * 8} bytes, {((cc[3] & 0x0F) == 0 ? "writable" : "read-only")}");
        var message = Type2Tag.ReadNdef(data);
        if (message is null || message.Records.Count == 0)
        {
            Ui.Warn("The tag holds no NDEF message.");
        }
        else
        {
            var ndef = message.Encode();
            AnsiConsole.Write(Ui.FrameLane(ndef, NdefMessage.Describe(ndef)));
            foreach (var r in message.Records) AnsiConsole.MarkupLine($"  [{Ui.Hex(Ui.Amber)}]●[/] {Markup.Escape(r.ToString())}");
        }

        if (s.Dump)
        {
            var memory = await client.DumpAsync(ct: ct);
            var map = Type2Tag.Map(memory, cc[2] * 8);
            for (var page = 0; page < memory.Length / 4; page++)
                AnsiConsole.MarkupLine($"  [{Ui.Hex(Ui.Muted)}]{page,3}[/]  {Convert.ToHexString(memory, page * 4, 4)}  [{Ui.Hex(Ui.Muted)}]{map[page * 4]}[/]");
        }

        return 0;
    }
}

internal sealed class NfcWriteCommand : AsyncCommand<NfcWriteCommand.Settings>
{
    public sealed class Settings : NfcSettings
    {
        [CommandOption("--uri"), Description("Write a URI record.")]
        public string? Uri { get; init; }

        [CommandOption("--text"), Description("Write a Text record (repeatable; after the URI).")]
        public string[] Text { get; init; } = [];

        [CommandOption("--lang"), Description("Language of the text records (default en).")]
        public string Language { get; init; } = "en";

        [CommandOption("--allow-write"), Description("Required: confirms you intend to overwrite the tag.")]
        public bool AllowWrite { get; init; }

        [CommandOption("-y|--yes"), Description("Skip the interactive confirmation.")]
        public bool Yes { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s, CancellationToken ct)
    {
        if (!s.AllowWrite)
        {
            Ui.Warn("Writing replaces the NDEF message on the tag. Re-run with [bold]--allow-write[/] to confirm you intend to.");
            return 3;
        }

        var records = new List<NdefRecord>();
        if (s.Uri is not null) records.Add(NdefRecord.Uri(s.Uri));
        records.AddRange(s.Text.Select(t => NdefRecord.Text(t, s.Language)));
        if (records.Count == 0) throw new ArgumentException("Give --uri and/or --text.");
        var message = new NdefMessage(records);
        await using var card = await s.WaitAsync(ct);
        var client = new Type2TagClient(card, new NfcTagOptions().AllowWrites());
        var uid = Convert.ToHexString(await client.GetUidAsync(ct));
        if (!s.Sim && !s.Yes && !AnsiConsole.Confirm($"Overwrite the NDEF message on tag {uid} with {Markup.Escape(message.ToString())}?", false)) return 4;
        await client.WriteNdefAsync(message, ct);
        Ui.Success($"Wrote {message.Encode().Length} bytes to tag {uid}: {Markup.Escape(message.ToString())}");
        return 0;
    }
}

internal sealed class NfcDecodeCommand : Command<NfcDecodeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<hex>"), Description("An NDEF message, or a Type 2 data area starting with a TLV (03 …).")]
        public string Hex { get; init; } = "";
    }

    public override int Execute(CommandContext context, Settings s, CancellationToken ct)
    {
        var bytes = Convert.FromHexString(new string(s.Hex.Where(Uri.IsHexDigit).ToArray()));
        var message = bytes.Length > 0 && bytes[0] is 0x00 or 0x01 or 0x02 or 0x03 ? Type2Tag.ReadNdef(bytes) : NdefMessage.Parse(bytes);
        if (message is null)
        {
            Ui.Warn("No NDEF TLV in this data area.");
            return 0;
        }

        var ndef = message.Encode();
        AnsiConsole.Write(Ui.FrameLane(ndef, NdefMessage.Describe(ndef)));
        foreach (var r in message.Records) AnsiConsole.MarkupLine($"  [{Ui.Hex(Ui.Amber)}]●[/] {Markup.Escape(r.ToString())}");
        return 0;
    }
}
