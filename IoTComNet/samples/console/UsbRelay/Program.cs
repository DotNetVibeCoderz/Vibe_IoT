// UsbRelay — list USB and HID devices, then blink relay 1 of a "USBRelayN" HID relay board.
//
//   dotnet run            # a simulated 2-channel board on a virtual bus
//   dotnet run -- --real  # your USB HID relay board (16c0:05df); this switches a real relay
//
// IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.
using IoTCom.Net.Transport.Usb;

var real = args.Contains("--real");
var bus = new VirtualUsbBus();
var simulated = bus.AddHid(new VirtualHidRelayBoard(relays: 2));
simulated.Changed += state => Console.WriteLine($"  (simulated board: relay bits {Convert.ToString(state, 2).PadLeft(2, '0')})");
IUsbBackend backend = real ? NativeUsbBackend.Instance : bus;

if (real)
{
    Console.WriteLine("USB devices:");
    foreach (var d in UsbDevice.List()) Console.WriteLine($"  {d.VendorId:x4}:{d.ProductId:x4}  {d.Manufacturer ?? d.VendorName} {d.Product ?? d.Kind}");
}

var boards = HidDevice.List(backend).Where(d => d.VendorId == HidRelayBoard.VendorId && d.ProductId == HidRelayBoard.ProductId).ToList();
if (boards.Count == 0)
{
    Console.WriteLine("No USB HID relay board (16c0:05df) is connected.");
    return;
}

await using var hid = HidDevice.Create(o => { o.Backend = backend; o.Path = boards[0].Path; });
await hid.ConnectAsync();
var board = new HidRelayBoard(hid);
Console.WriteLine($"{hid.Info!.Product} serial {await board.GetSerialAsync()} with {board.Relays} relays");
for (var i = 0; i < 3; i++)
{
    await board.SetAsync(1, true);
    Console.WriteLine($"relay 1 on  → {string.Join(" ", await board.GetStatesAsync())}");
    await Task.Delay(500);
    await board.SetAsync(1, false);
    Console.WriteLine($"relay 1 off → {string.Join(" ", await board.GetStatesAsync())}");
    await Task.Delay(500);
}
