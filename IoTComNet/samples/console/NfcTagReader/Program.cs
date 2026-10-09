// NfcTagReader — read NFC tags through a PC/SC contactless reader (ACR122U, ACR1252U, Omnikey 5x22…).
//
//   dotnet run                                  # a virtual reader with a simulated NTAG213 asset tag
//   dotnet run -- --pcsc                        # the first contactless PC/SC reader; tap tags, Ctrl+C to stop
//   dotnet run -- --pcsc ACR122 --write https://example.com/asset/42
//                                               # also write a URI to each tag (overwrites its NDEF message)
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using IoTCom.Net;
using IoTCom.Net.Protocols.Nfc;

var usePcsc = args.Contains("--pcsc");
var match = args.SkipWhile(a => a != "--pcsc").Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
var writeUri = args.SkipWhile(a => a != "--write").Skip(1).FirstOrDefault();

INfcReader reader;
if (usePcsc)
{
    Console.WriteLine("PC/SC readers: " + string.Join(", ", Pcsc.ListReaders()));
    reader = PcscNfcReader.Open(match);
}
else
{
    var virtualReader = new VirtualNfcReader();
    virtualReader.Present(new VirtualType2Tag(Type2TagKind.Ntag213, [0x04, 0x51, 0x7A, 0x22, 0x9C, 0x61, 0x80],
        new NdefMessage([NdefRecord.SmartPoster("https://docs.example.com/p7", "Pump P-0007 manual"), NdefRecord.Text("Asset P-0007")])));
    reader = virtualReader;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
Console.WriteLine($"Waiting for tags on {reader.Name}…");
try
{
    do
    {
        await using var card = await reader.WaitForTagAsync(cts.Token);
        var options = writeUri is null ? new NfcTagOptions() : new NfcTagOptions().AllowWrites();
        var tag = new Type2TagClient(card, options);
        try
        {
            Console.WriteLine($"Tag {Convert.ToHexString(await tag.GetUidAsync(cts.Token))}");
            var message = await tag.ReadNdefAsync(cts.Token);
            foreach (var record in message?.Records ?? []) Console.WriteLine("  " + record);
            if (writeUri is not null)
            {
                await tag.WriteNdefAsync(new NdefMessage([NdefRecord.Uri(writeUri)]), cts.Token);
                Console.WriteLine($"  wrote {writeUri}");
            }
        }
        catch (Exception ex) when (ex is IoTComException)
        {
            Console.WriteLine("  " + ex.Message);   // not NDEF formatted, removed too early, read-only…
        }

        if (usePcsc)
        {
            Console.WriteLine("Remove the tag…");
            await Task.Delay(1500, cts.Token);
        }
    }
    while (usePcsc);
}
catch (OperationCanceledException)
{
}
