#!/usr/bin/env python3
"""Generates the EN/ID Polyglot (.NET Interactive) notebooks from one spec.

Code cells are shared verbatim between languages; only the narrative differs — the docs parity rule
(every EN page has an ID twin) applies to notebooks too.

Run: python build/generate_notebooks.py
"""
import json
import os

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "notebooks")
VERSION = "0.16.0-preview.1"
SETUP = f'#r "nuget: IoTCom.Net, {VERSION}"\n#r "nuget: IoTCom.Net.Native.Modbus, {VERSION}"'
LOCAL = ("> Working from a clone? Run `dotnet pack -c Release -o artifacts/packages` at the repo root and add\n"
         "> `#i \"nuget: <repo>/artifacts/packages\"` before the `#r` lines.",
         "> Bekerja dari hasil clone? Jalankan `dotnet pack -c Release -o artifacts/packages` di root repo lalu tambahkan\n"
         "> `#i \"nuget: <repo>/artifacts/packages\"` sebelum baris `#r`.")
CREDIT = ("*IoTCom.Net — built by Gravicode Studios, led by Kang Fadhil.*", "*IoTCom.Net — dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*")


def md(en, id_):
    return ("md", en, id_)


def code(src):
    return ("code", src)


NOTEBOOKS = {
    "00-start-here": [
        md("# Start here — IoTCom.Net in 10 minutes\n\nIoTCom.Net speaks industrial, navigation, lighting and messaging protocols with one consistent API: "
           "**clients** connect, **servers** serve, **publishers** publish, **subscribers** subscribe. Every protocol ships a simulator, "
           "so every cell here runs without hardware.",
           "# Mulai di sini — IoTCom.Net dalam 10 menit\n\nIoTCom.Net berbicara protokol industri, navigasi, pencahayaan, dan pesan dengan satu API yang konsisten: "
           "**client** terhubung, **server** melayani, **publisher** menerbitkan, **subscriber** berlangganan. Setiap protokol punya simulator, "
           "jadi setiap sel di sini berjalan tanpa perangkat keras."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP),
        md("## The shape of every endpoint\n\nAll endpoints expose `State` and `StateChanged`, are `IAsyncDisposable`, and are configured with a fluent builder. "
           "Transports are swappable: `UseTcp`, `UseSerial`, `UseInMemory`.",
           "## Bentuk setiap endpoint\n\nSemua endpoint memiliki `State` dan `StateChanged`, merupakan `IAsyncDisposable`, dan dikonfigurasi dengan builder fluent. "
           "Transport bisa ditukar: `UseTcp`, `UseSerial`, `UseInMemory`."),
        code("using IoTCom.Net;\nusing IoTCom.Net.Protocols.Modbus;\nusing IoTCom.Net.Transports;\n\n"
             "var link = new InMemoryTransportListener();\nvar server = ModbusServer.Create(o => o.ListenInMemory(link));\n"
             "server.StateChanged += (_, e) => Console.WriteLine($\"server: {e.Previous} -> {e.Current}\");\nawait server.StartAsync();\n\n"
             "var client = ModbusClient.Create(o => o.UseInMemory(link));\nserver.Store.HoldingRegisters[0] = 42;\n"
             "Console.WriteLine($\"HR0 = {(await client.ReadHoldingRegistersAsync(0, 1))[0]}\");"),
        md("## See the bytes\n\nAttach a `RecordingTap` to any endpoint to capture every frame — the same data the Gallery and CLI show as a *frame lane*.",
           "## Lihat byte-nya\n\nPasang `RecordingTap` ke endpoint mana pun untuk menangkap setiap frame — data yang sama ditampilkan Galeri dan CLI sebagai *frame lane*."),
        code("var tap = new RecordingTap();\nclient.AddTap(tap);\nawait client.ReadHoldingRegistersAsync(0, 2);\n"
             "foreach (var f in tap.Snapshot()) Console.WriteLine($\"{f.Direction,-8} {HexDump.ToHex(f.Data.Span),-40} {f.Summary}\");"),
        md("## Where next\n\n| Notebook | Topic |\n|---|---|\n| `industrial/01-modbus` | Modbus master, slave, simulator, Rust engine |\n"
           "| `transport/02-framing-crc` | CRC catalogue, SLIP, COBS, HDLC |\n| `navigation/03-nmea` | GPS/GNSS with NMEA 0183 |\n"
           "| `messaging/04-mqtt-senml` | MQTT pub/sub with SenML payloads |\n| `messaging/12-mdns-sparkplug` | mDNS discovery, Sparkplug B, Protobuf/MessagePack/TLV |\n| `industrial/13-opcua` | OPC UA browse, read, subscribe, write |\n| `devices/14-ble` | Bluetooth LE advertisements, GATT, notifications |\n| `devices/15-usb` | USB control/bulk transfers and HID reports |\n| `industrial/16-canopen` | CANopen SDO, PDO, NMT, heartbeats |\n| `99-protocol-chooser` | Which protocol for which job |\n\n" + CREDIT[0],
           "## Selanjutnya\n\n| Notebook | Topik |\n|---|---|\n| `industrial/01-modbus` | Master, slave, simulator Modbus, mesin Rust |\n"
           "| `transport/02-framing-crc` | Katalog CRC, SLIP, COBS, HDLC |\n| `navigation/03-nmea` | GPS/GNSS dengan NMEA 0183 |\n"
           "| `messaging/04-mqtt-senml` | Pub/sub MQTT dengan payload SenML |\n| `messaging/12-mdns-sparkplug` | Penemuan mDNS, Sparkplug B, Protobuf/MessagePack/TLV |\n| `industrial/13-opcua` | OPC UA: jelajah, baca, subscribe, tulis |\n| `devices/14-ble` | Bluetooth LE: advertisement, GATT, notifikasi |\n| `devices/15-usb` | Transfer USB control/bulk dan report HID |\n| `industrial/16-canopen` | CANopen: SDO, PDO, NMT, heartbeat |\n| `99-protocol-chooser` | Protokol mana untuk tugas apa |\n\n" + CREDIT[1]),
    ],
    "industrial/01-modbus": [
        md("# Modbus — master, slave and simulator\n\n**What it is.** Modbus is the request/response lingua franca of PLCs, meters, drives and sensors. "
           "A *master* (client) asks; a *slave* (server) answers from four tables: coils, discrete inputs, input registers, holding registers.",
           "# Modbus — master, slave, dan simulator\n\n**Apa itu.** Modbus adalah bahasa request/response yang umum di PLC, meter, drive, dan sensor. "
           "*Master* (client) bertanya; *slave* (server) menjawab dari empat tabel: coil, discrete input, input register, holding register."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP),
        md("## Server: a virtual PLC\n`ModbusSimulator.CreateVirtualPlc` animates temperature, motor speed, power and a production counter.",
           "## Server: PLC virtual\n`ModbusSimulator.CreateVirtualPlc` menganimasikan suhu, kecepatan motor, daya, dan penghitung produksi."),
        code("using IoTCom.Net;\nusing IoTCom.Net.Protocols.Modbus;\nusing IoTCom.Net.Transports;\n\nvar link = new InMemoryTransportListener();\n"
             "var store = new ModbusDataStore();\nvar plcServer = ModbusServer.Create(o => o.ListenInMemory(link).WithStore(store));\n"
             "var simulator = ModbusSimulator.CreateVirtualPlc(store);\nawait plcServer.StartAsync();\nfor (var i = 0; i < 20; i++) simulator.Tick(0.25); // deterministic time\n"
             "Console.WriteLine($\"temperature register = {store.InputRegisters[0]}\");"),
        md("## Client: read and convert\nRegisters are 16-bit; 32-bit floats span two registers. `ModbusConvert` handles the four word orders found in the wild.",
           "## Client: baca dan konversi\nRegister berukuran 16-bit; float 32-bit memakai dua register. `ModbusConvert` menangani empat urutan word yang ditemui di lapangan."),
        code("var plc = ModbusClient.Create(o => o.UseInMemory(link).WithUnitId(1));\nvar ir = await plc.ReadInputRegistersAsync(0, 8);\n"
             "Console.WriteLine($\"temperature {ir[0] / 10.0} °C, rpm {ir[3]}, power {ModbusConvert.ToSingle(ir.AsSpan(4, 2)):0.00} kW\");"),
        md("## Write — and the read-only safety mode\nWrites change real equipment. `.AsReadOnly()` blocks them before anything reaches the wire.",
           "## Tulis — dan mode aman read-only\nPenulisan mengubah peralatan sungguhan. `.AsReadOnly()` memblokirnya sebelum apa pun sampai ke jalur."),
        code("await plc.WriteSingleCoilAsync(0, false); // stop the motor\nfor (var i = 0; i < 20; i++) simulator.Tick(0.25);\n"
             "Console.WriteLine($\"rpm after stop: {(await plc.ReadInputRegistersAsync(3, 1))[0]}\");\n\n"
             "var monitor = ModbusClient.Create(o => o.UseInMemory(link).AsReadOnly());\n"
             "try { await monitor.WriteSingleRegisterAsync(0, 300); } catch (ReadOnlyModeException e) { Console.WriteLine(e.Message); }"),
        md("## The Rust engine\n`NativeModbusClient` runs the same protocol through the sans-I/O Rust state machine. Same API, identical wire bytes.",
           "## Mesin Rust\n`NativeModbusClient` menjalankan protokol yang sama lewat state machine Rust sans-I/O. API sama, byte di jalur identik."),
        code("using IoTCom.Net.Native.Modbus;\nif (NativeModbusMaster.IsSupported)\n{\n"
             "    await using var rust = NativeModbusClient.Create(o => o.UseInMemory(link));\n"
             "    Console.WriteLine($\"via Rust: {string.Join(\", \", await rust.ReadHoldingRegistersAsync(0, 3))}\");\n}\n"
             "else Console.WriteLine(\"Native library not available for this platform — the managed client covers everything.\");"),
        md("## Troubleshooting\n- **Timeout**: wrong unit id, wrong port, or RTU parity/baud mismatch (Modbus RTU default is 8E1).\n"
           "- **IllegalDataAddress**: address + count beyond the device map — check 0-based vs 1-based (40001 = HR0).\n"
           "- **Garbage on RTU**: two masters on one bus, or missing termination resistors.\n\n"
           "## Exercise\nAdd `simulator.AddSignal(ModbusTable.HoldingRegisters, 50, t => Math.Sin(t) * 100 + 100)` and plot HR50 over time.\n\n" + CREDIT[0],
           "## Pemecahan masalah\n- **Timeout**: unit id salah, port salah, atau parity/baud RTU tidak cocok (default Modbus RTU adalah 8E1).\n"
           "- **IllegalDataAddress**: alamat + jumlah melewati peta perangkat — periksa basis 0 vs basis 1 (40001 = HR0).\n"
           "- **Data kacau pada RTU**: dua master di satu bus, atau resistor terminasi tidak terpasang.\n\n"
           "## Latihan\nTambahkan `simulator.AddSignal(ModbusTable.HoldingRegisters, 50, t => Math.Sin(t) * 100 + 100)` lalu plot HR50 terhadap waktu.\n\n" + CREDIT[1]),
    ],
    "transport/02-framing-crc": [
        md("# Framing and checksums\n\nSerial links need two things: a way to find frame boundaries (SLIP, COBS, HDLC) and a way to detect corruption (CRC). "
           "IoTCom.Net.Framing has both, allocation-free and streaming.",
           "# Framing dan checksum\n\nLink serial butuh dua hal: cara menemukan batas frame (SLIP, COBS, HDLC) dan cara mendeteksi kerusakan (CRC). "
           "IoTCom.Net.Framing menyediakan keduanya, tanpa alokasi dan streaming."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP),
        md("## The CRC catalogue\nEvery preset is verified against its catalogue check value for ASCII `123456789`.",
           "## Katalog CRC\nSetiap preset diverifikasi dengan nilai check katalog untuk ASCII `123456789`."),
        code("using IoTCom.Net.Framing;\nforeach (var crc in CrcCatalog.All)\n"
             "    Console.WriteLine($\"{crc.Parameters.Name,-18} 0x{crc.Compute(\"123456789\"u8):X}  ok={crc.SelfTest()}\");"),
        md("## Same bytes, three framings", "## Byte yang sama, tiga framing"),
        code("using System.Buffers;\nbyte[] payload = [0x7E, 0xC0, 0x00, 0x11, 0xDB];\nforeach (IFrameEncoder codec in new IFrameEncoder[] { new Slip(), new Cobs(), new Hdlc() })\n{\n"
             "    var w = new ArrayBufferWriter<byte>();\n    codec.Encode(payload, w);\n"
             "    Console.WriteLine($\"{codec.GetType().Name,-5} {IoTCom.Net.HexDump.ToHex(w.WrittenSpan)}\");\n}"),
        md("## Streaming decode\nDecoders work on `ReadOnlySequence<byte>` straight from a `PipeReader`, so frames split across reads are handled.\n\n" + CREDIT[0],
           "## Decode streaming\nDecoder bekerja pada `ReadOnlySequence<byte>` langsung dari `PipeReader`, sehingga frame yang terpotong di beberapa read tetap tertangani.\n\n" + CREDIT[1]),
        code("using System.IO.Pipelines;\nvar pipe = new Pipe();\nvar cobs = new Cobs();\nawait pipe.Writer.WriteFrameAsync(cobs, new byte[] { 1, 0, 2 });\n"
             "await pipe.Writer.WriteFrameAsync(cobs, new byte[] { 3, 4 });\nawait pipe.Writer.CompleteAsync();\n"
             "await foreach (var frame in pipe.Reader.ReadFramesAsync(cobs)) Console.WriteLine(IoTCom.Net.HexDump.ToHex(frame));"),
    ],
    "navigation/03-nmea": [
        md("# NMEA 0183 — GPS and GNSS\n\nGPS receivers, AIS transponders and marine instruments talk NMEA 0183: ASCII lines like `$GPGGA,...*47` with an XOR checksum.",
           "# NMEA 0183 — GPS dan GNSS\n\nPenerima GPS, transponder AIS, dan instrumen kapal berbicara NMEA 0183: baris ASCII seperti `$GPGGA,...*47` dengan checksum XOR."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP),
        md("## Parse a sentence", "## Urai sebuah kalimat"),
        code("using IoTCom.Net.Protocols.Nmea;\nvar gga = (GgaMessage)NmeaParser.Parse(\"$GPGGA,123519,4807.038,N,01131.000,E,1,08,0.9,545.4,M,46.9,M,,*47\")!;\n"
             "Console.WriteLine($\"{gga.Latitude:0.0000}, {gga.Longitude:0.0000} · {gga.Satellites} satellites · {gga.AltitudeMeters} m\");"),
        md("## A simulated receiver and a reader\n`GnssState` merges GGA, RMC, GSA and GSV into one fix.",
           "## Penerima simulasi dan pembaca\n`GnssState` menggabungkan GGA, RMC, GSA, dan GSV menjadi satu fix."),
        code("using IoTCom.Net;\nusing IoTCom.Net.Transports;\nvar link = new InMemoryTransportListener();\n"
             "var receiver = NmeaServer.Create(o => o.ListenInMemory(link));\nawait receiver.StartAsync();\n"
             "var reader = NmeaReader.Create(o => o.UseInMemory(link));\nawait reader.ConnectAsync();\nawait Task.Delay(100);\n\n"
             "var sim = new NmeaSimulator();\nforeach (var s in sim.GenerateEpoch(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30))) await receiver.BroadcastAsync(s);\n"
             "await Task.Delay(200);\nvar fix = reader.Gnss.Current;\nConsole.WriteLine($\"{fix.Latitude:0.00000}, {fix.Longitude:0.00000} · {fix.SpeedKmh} km/h · {fix.SatellitesInView.Count} in view\");"),
        md("## AIS: ships on the same wire\nAIS transponders send `!AIVDM` sentences: six-bit armoured binary, sometimes split over two sentences.",
           "## AIS: kapal di jalur yang sama\nTransponder AIS mengirim kalimat `!AIVDM`: biner six-bit yang di-armour, kadang dipecah menjadi dua kalimat."),
        code("var ais = new AisDecoder();\n"
             "var voyage = (AisStaticData)(ais.Feed(\"!AIVDM,2,1,1,A,55?MbV02;H;s<HtKR20EHE:0@T4@Dn2222222216L961O5Gf0NSQEp6ClRp8,0*1C\")\n"
             "    ?? ais.Feed(\"!AIVDM,2,2,1,A,88888888880,2*25\"))!;\n"
             "Console.WriteLine($\"{voyage.Name} ({Ais.ShipTypeName(voyage.ShipType)}, {voyage.Length} m) → {voyage.Destination}, draught {voyage.Draught} m\");\n\n"
             "var harbour = new AisSimulator();\nvar vessels = new AisTracker();\n"
             "for (var step = 0; step < 6; step++)\n    foreach (var aisLine in harbour.Step(TimeSpan.FromSeconds(10)))\n        if (ais.Feed(aisLine) is { } msg) vessels.Apply(msg);\n"
             "foreach (var v in vessels.Vessels.OrderBy(v => v.Name)) Console.WriteLine($\"{v.Name,-18} {v.Speed,5:0.0} kn  {v.Latitude:0.000}, {v.Longitude:0.000}\");"),
        md("## Troubleshooting\n- No data on serial: most receivers default to 9600 or 4800 baud, 8N1.\n- Fix but no position: check RMC status `A` (active) vs `V` (void).\n\n" + CREDIT[0],
           "## Pemecahan masalah\n- Tidak ada data di serial: kebanyakan penerima default 9600 atau 4800 baud, 8N1.\n- Ada fix tetapi tanpa posisi: periksa status RMC `A` (aktif) vs `V` (void).\n\n" + CREDIT[1]),
    ],
    "messaging/04-mqtt-senml": [
        md("# MQTT with SenML payloads\n\nMQTT moves messages by topic; SenML (RFC 8428) gives sensor payloads a standard shape. IoTCom.Net adapts MQTTnet rather than re-implementing MQTT.",
           "# MQTT dengan payload SenML\n\nMQTT memindahkan pesan berdasarkan topik; SenML (RFC 8428) memberi bentuk standar untuk payload sensor. IoTCom.Net mengadaptasi MQTTnet alih-alih menulis ulang MQTT."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP),
        code("using IoTCom.Net;\nusing IoTCom.Net.Adapters.Mqtt;\nusing IoTCom.Net.Serialization.SenML;\n\n"
             "var broker = MqttBroker.Create(18830);\nawait broker.StartAsync();\nvar sensor = MqttEndpoint.Create(o => o.UseBroker(\"127.0.0.1\", 18830));\n"
             "var dashboard = MqttEndpoint.Create(o => o.UseBroker(\"127.0.0.1\", 18830));\nawait sensor.ConnectAsync();\nawait dashboard.ConnectAsync();"),
        md("## Subscribe with wildcards, publish SenML", "## Subscribe dengan wildcard, publish SenML"),
        code("using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));\nvar received = Task.Run(async () =>\n{\n"
             "    await foreach (var m in dashboard.SubscribeAsync(\"plant/+/sensor\", cts.Token))\n"
             "        return SenMLCodec.Resolve(SenMLCodec.ParseJson(m.Payload.Span));\n    return null;\n});\nawait Task.Delay(300);\n\n"
             "var pack = new SenMLPackBuilder(\"urn:dev:demo:\").At(DateTimeOffset.UtcNow).Add(\"temperature\", 23.4, \"Cel\").Build();\n"
             "await sensor.PublishAsync(\"plant/line1/sensor\", SenMLCodec.ToJson(pack));\n"
             "foreach (var r in (await received)!) Console.WriteLine($\"{r.Name} = {r.Value} {r.Unit} @ {r.Time:T}\");"),
        md("## JSON vs CBOR size\n", "## Ukuran JSON vs CBOR\n"),
        code("Console.WriteLine($\"JSON {SenMLCodec.ToJson(pack).Length} bytes · CBOR {SenMLCodec.ToCbor(pack).Length} bytes\");"),
        md(CREDIT[0], CREDIT[1]),
    ],
    "navigation/08-mavlink": [
        md("# MAVLink: talking to drones\n\nThe complete common dialect is generated from the official XML; a simulated quadcopter "
           "flies on an in-memory UDP link. Commands move real vehicles — keep to the simulator here.",
           "# MAVLink: berbicara dengan drone\n\nDialek common lengkap dihasilkan dari XML resmi; quadcopter simulasi terbang di "
           "link UDP memori. Perintah menggerakkan kendaraan sungguhan — tetap di simulator di sini."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP),
        md("## Generated messages and a frame\nEvery message is a C# class with its id, CRC_EXTRA and wire layout.",
           "## Pesan hasil generator dan sebuah frame\nSetiap pesan adalah kelas C# dengan id, CRC_EXTRA, dan tata letak wire-nya."),
        code("using IoTCom.Net.Protocols.Mavlink;\nusing IoTCom.Net.Protocols.Mavlink.Common;\n\n"
             "Console.WriteLine($\"{CommonDialect.Instance.Messages.Count} messages; HEARTBEAT crc_extra {Heartbeat.MavlinkCrcExtra}\");\n"
             "var hb = new Heartbeat { Type = MavType.Quadrotor, Autopilot = MavAutopilot.Px4, SystemStatus = MavState.Active };\n"
             "var frame = MavlinkCodec.Encode(hb, sequence: 0, systemId: 1, componentId: 1);\n"
             "Console.WriteLine(Convert.ToHexString(frame));\n"
             "foreach (var f in MavlinkAnatomy.Describe(frame, CommonDialect.Instance)) Console.WriteLine($\"{f.Name,-10} {f.Value}\");"),
        md("## Signing\nA signed frame carries link id, timestamp and a 6-byte SHA-256 signature; a forged one is rejected.",
           "## Signing\nFrame bertanda tangan membawa link id, timestamp, dan tanda tangan SHA-256 6 byte; frame palsu ditolak."),
        code("var key = MavlinkSigning.FromPassphrase(\"demo\");\nvar signed = MavlinkCodec.Encode(hb, 1, 1, 1, signing: key);\n"
             "var parser = new MavlinkParser(CommonDialect.Instance, MavlinkSigning.FromPassphrase(\"demo\"));\n"
             "parser.Feed(signed);\nConsole.WriteLine(parser.TryRead(out var ok) ? $\"accepted, signed={ok.Signed}\" : \"rejected\");\n"
             "signed[^1] ^= 1;\nparser.Feed(signed);\nConsole.WriteLine(parser.TryRead(out _) ? \"accepted\" : $\"forged frame rejected ({parser.SignatureErrors})\");"),
        md("## Fly the simulator from a ground station", "## Terbangkan simulator dari ground station"),
        code("using System.Net;\nusing IoTCom.Net.Transports;\n\nvar net = new InMemoryDatagramNetwork();\n"
             "var gcsAddress = new IPEndPoint(IPAddress.Loopback, 14550);\n"
             "await using var vehicle = MavlinkConnection.Create(o => { o.UseInMemory(net).SendTo(gcsAddress); (o.SystemId, o.ComponentId) = (1, 1); });\n"
             "await using var sim = new MavlinkVehicleSimulator(vehicle);\n"
             "await using var link = MavlinkConnection.Create(o => o.UseInMemory(net, gcsAddress));\n"
             "using var gcs = new MavlinkGroundStation(link);\n"
             "await vehicle.ConnectAsync(); await link.ConnectAsync(); sim.Start();\n"
             "await gcs.WaitForHeartbeatAsync(TimeSpan.FromSeconds(5));\n"
             "Console.WriteLine($\"ARM → {await gcs.ArmAsync()}, TAKEOFF → {await gcs.TakeoffAsync(10)}\");\n"
             "await Task.Delay(4000);\n"
             "Console.WriteLine($\"altitude {gcs.State.RelativeAltitude:0.0} m, battery {gcs.State.BatteryVoltage:0.00} V, packets {link.Statistics.PacketsReceived}\");\n"
             "var p = await gcs.ReadParametersAsync();\nConsole.WriteLine(string.Join(\", \", p.Take(4).Select(kv => $\"{kv.Key}={kv.Value}\")));"),
        md("## Going further\nThe Gallery demo *Drone telemetry over MAVLink* adds an artificial horizon and a track map; "
           "`iotcom mavlink simulate` lets QGroundControl connect. See `docs/en/protocols/mavlink.md`.\n\n" + CREDIT[0],
           "## Lebih lanjut\nDemo Galeri *Telemetri drone lewat MAVLink* menambahkan artificial horizon dan peta jejak; "
           "`iotcom mavlink simulate` memungkinkan QGroundControl terhubung. Lihat `docs/id/protocols/mavlink.md`.\n\n" + CREDIT[1]),
    ],
    "messaging/07-coap": [
        md("# CoAP: REST for constrained devices\n\nGET, PUT, Observe and Block-wise over UDP — against a simulated greenhouse node on an "
           "in-memory network whose packet loss you choose.",
           "# CoAP: REST untuk perangkat terbatas\n\nGET, PUT, Observe, dan Block-wise lewat UDP — terhadap node rumah kaca simulasi di "
           "jaringan memori yang kehilangan paketnya bisa Anda atur."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP),
        md("## A message on the wire\nThe RFC 7252 example: CON GET /temperature, 16 bytes.",
           "## Satu pesan di jalur\nContoh RFC 7252: CON GET /temperature, 16 byte."),
        code("using IoTCom.Net.Protocols.Coap;\n\nvar get = new CoapMessage { Code = CoapCode.Get, MessageId = 0x7D34 };\n"
             "get.UriPath = \"/temperature\";\nvar wire = get.Encode();\nConsole.WriteLine(Convert.ToHexString(wire));\n"
             "foreach (var f in CoapAnatomy.Describe(wire)) Console.WriteLine($\"{f.Name,-10} {f.Value}\");"),
        md("## A device and a client", "## Perangkat dan client"),
        code("using System.Net;\nusing IoTCom.Net.Transports;\n\nvar net = new InMemoryDatagramNetwork();\n"
             "var node = new IPEndPoint(IPAddress.Parse(\"10.0.0.40\"), 5683);\n"
             "await using var server = CoapServer.Create(o => o.UseInMemory(net, node));\n"
             "await using var device = new CoapDeviceSimulator(server);\nawait server.StartAsync();\n\n"
             "await using var coap = CoapClient.Create(o => o.UseInMemory(net).UseServer(node));\nawait coap.ConnectAsync();\n"
             "foreach (var link in await coap.DiscoverAsync()) Console.WriteLine($\"{link.Path,-22} {link.ResourceType} {(link.Observable ? \"(observable)\" : \"\")}\");\n"
             "Console.WriteLine((await coap.GetAsync(\"/sensors/temperature\")).PayloadText);\n"
             "Console.WriteLine((await coap.GetAsync(\"/sensors/temperature\", CoapContentFormat.SenMLJson)).PayloadText);"),
        md("## Observe\nThe device pushes a notification when the value changes.",
           "## Observe\nPerangkat mendorong notifikasi saat nilainya berubah."),
        code("using var cts = new CancellationTokenSource();\nvar count = 0;\n"
             "var watch = Task.Run(async () => { await foreach (var n in coap.ObserveAsync(\"/sensors/soil\", ct: cts.Token)) { Console.WriteLine($\"soil {n.PayloadText} (obs {n.ObserveSequence})\"); if (++count == 3) break; } });\n"
             "await coap.PutAsync(\"/actuators/valve\", \"80\");\n"
             "while (!watch.IsCompleted) await device.StepAsync();"),
        md("## A lossy link\n30 % of datagrams vanish; confirmable messages are retransmitted until acknowledged.",
           "## Jaringan yang kehilangan paket\n30 % datagram hilang; pesan confirmable dikirim ulang sampai di-ACK."),
        code("net.LossRate = 0.3;\ncoap.Options.Transmission.AckTimeout = TimeSpan.FromMilliseconds(50);\n"
             "coap.Options.Transmission.MaxRetransmit = 8;\n"
             "var log = await coap.GetAsync(\"/device/log\");   // Block2: several exchanges\n"
             "Console.WriteLine($\"{log.Payload.Length} bytes; {coap.Statistics.RetransmissionCount} retransmissions; {net.Dropped} datagrams lost\");"),
        md("## Going further\nThe Gallery demo *Smart greenhouse over CoAP* draws every datagram on a message sequence chart. "
           "See `docs/en/protocols/coap.md`.\n\n" + CREDIT[0],
           "## Lebih lanjut\nDemo Galeri *Rumah kaca pintar lewat CoAP* menggambar setiap datagram di diagram urutan pesan. "
           "Lihat `docs/id/protocols/coap.md`.\n\n" + CREDIT[1]),
    ],
    "lpwan/09-lorawan": [
        md("# LoRaWAN: sensors kilometres away\n\nA frame on the air, an over-the-air join by hand, the airtime budget, and a whole network — "
           "gateways speaking the Semtech UDP protocol to a light network server — running in memory.",
           "# LoRaWAN: sensor yang jauhnya berkilo-kilometer\n\nSatu frame di udara, join over-the-air secara manual, anggaran airtime, dan "
           "satu jaringan utuh — gateway yang berbicara protokol Semtech UDP ke network server ringan — berjalan di memori."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP),
        md("## A frame on the air\nThe classic example: 17 bytes, the payload is encrypted with the AppSKey and signed with the NwkSKey.",
           "## Satu frame di udara\nContoh klasik: 17 byte, payload dienkripsi dengan AppSKey dan ditandatangani dengan NwkSKey."),
        code("using IoTCom.Net.Protocols.LoRaWan;\n\nvar phy = Convert.FromHexString(\"40F17DBE4900020001954378762B11FF0D\");\n"
             "foreach (var f in LoRaWanAnatomy.Describe(phy)) Console.WriteLine($\"{f.Name,-11} {Convert.ToHexString(phy, f.Offset, f.Length),-10} {f.Value}\");\n"
             "var keys = LoRaWanSessionKeys.FromHex(\"44024241ED4CE9A68C6A8BC055233FD3\", \"EC925802AE430CA77FD3DD73CB2CC588\");\n"
             "var frame = LoRaWanPacket.Decode(phy);\n"
             "Console.WriteLine($\"MIC valid: {frame.VerifyMic(keys.NwkSKey)}, payload: {System.Text.Encoding.ASCII.GetString(frame.DecryptPayload(keys))}\");"),
        md("## Joining over the air, by hand\nThe device and the network derive the same session keys from the AppKey, the DevNonce and the "
           "Join-Accept. `LoRaWanEndDevice` is the device's MAC layer without any I/O.",
           "## Join over-the-air, secara manual\nPerangkat dan jaringan menurunkan session key yang sama dari AppKey, DevNonce, dan "
           "Join-Accept. `LoRaWanEndDevice` adalah lapisan MAC perangkat tanpa I/O."),
        code("var appKey = LoRaWanKeys.Parse(\"2B7E151628AED2A6ABF7158809CF4F3C\");\n"
             "var node = new LoRaWanEndDevice(Eui64.Parse(\"70B3D57ED0000101\"), default, appKey);\n"
             "var joinRequest = LoRaWanPacket.Decode(node.CreateJoinRequest());\nConsole.WriteLine(joinRequest);\n"
             "var accept = new LoRaWanJoinAccept(JoinNonce: 0x00A1B2, NetId: 0x13, DevAddr: DevAddr.Parse(\"26011BDA\"));\n"
             "node.HandleDownlink(accept.Encode(appKey));\n"
             "var networkKeys = accept.DeriveSessionKeys(appKey, joinRequest.DevNonce);\n"
             "Console.WriteLine($\"joined as {node.DevAddr}; same keys on both sides: {node.SessionKeys!.NwkSKey.SequenceEqual(networkKeys.NwkSKey)}\");\n"
             "var uplink = LoRaWanPacket.Decode(node.CreateUplink(1, new CayenneLpp().AddTemperature(1, 28.4).AddHumidity(2, 71).ToArray()));\n"
             "Console.WriteLine($\"{uplink} → {string.Join(\", \", CayenneLpp.Decode(uplink.DecryptPayload(networkKeys)))}\");"),
        md("## The airtime budget\nEvery step from SF7 to SF12 doubles the time on air: more range, less capacity. "
           "In EU868 a 1 % duty cycle turns that into a mandatory wait.",
           "## Anggaran airtime\nSetiap langkah dari SF7 ke SF12 menggandakan waktu di udara: jangkauan bertambah, kapasitas berkurang. "
           "Di EU868, duty cycle 1 % mengubahnya menjadi waktu tunggu wajib."),
        code("foreach (var sf in new[] { 7, 8, 9, 10, 11, 12 })\n"
             "{\n    var air = LoRaAirtime.Compute(12 + 13, sf);\n"
             "    Console.WriteLine($\"SF{sf,-2} {air.TotalMilliseconds,7:0.0} ms   then wait {air.TotalSeconds * 99,6:0.0} s at 1 %\");\n}"),
        md("## A network in memory\nTwo gateways forward over Semtech UDP to the network server; four sensors join and report. "
           "`HonorTimestamps = false` skips the real 5 s join delay so the cell finishes quickly.",
           "## Jaringan di memori\nDua gateway meneruskan lewat Semtech UDP ke network server; empat sensor join dan melapor. "
           "`HonorTimestamps = false` melewati jeda join 5 detik yang sebenarnya agar sel cepat selesai."),
        code("using System.Net;\nusing IoTCom.Net.Transports;\n\nvar radio = new InMemoryDatagramNetwork();\n"
             "var nsAddress = new IPEndPoint(IPAddress.Parse(\"10.0.0.1\"), 1700);\n"
             "await using var ns = LoRaWanNetworkServer.Create(o => o.UseInMemory(radio, nsAddress).Region = LoRaRegion.AS923Group2);\n"
             "var simOptions = LoRaWanSimulatorOptions.Demo(nsAddress);\nsimOptions.GatewayTransportFactory = () => radio.Bind();\n"
             "simOptions.HonorTimestamps = false;\n"
             "await using var sim = new LoRaWanSimulator(simOptions);\nforeach (var r in sim.Registrations) ns.AddDevice(r);\n\n"
             "var reports = new List<LoRaWanUplink>();\nns.UplinkReceived += (_, u) => { lock (reports) reports.Add(u); };\n"
             "await ns.StartAsync();\nawait sim.StartAsync();\n"
             "while (reports.Count < 4) await Task.Delay(100);\n"
             "lock (reports)\n    foreach (var u in reports)\n"
             "        Console.WriteLine($\"{u.Device.Name,-11} {u.DataRate,-9} via {u.Gateways.Count} gw, best SNR {u.Gateways[0].Snr,5:0.0}  {LoRaWanSimulator.DescribePayload(u.FPort, u.Payload)}\");"),
        md("## A downlink\nClass A devices listen only right after they transmit, so a downlink waits in a queue for the next uplink. "
           "FPort 10 tells the simulated sensors how often to report.",
           "## Downlink\nPerangkat Class A hanya mendengar sesaat setelah mengirim, jadi downlink menunggu di antrean sampai uplink berikutnya. "
           "FPort 10 memberi tahu sensor simulasi seberapa sering melapor."),
        code("var weather = ns.Devices.First(d => d.Name == \"weather-01\");\nvar weatherNode = sim.Devices.First(d => d.Name == \"weather-01\");\n"
             "ns.EnqueueDownlink(weather.DevEui, 10, [0x00, 0x3C]);\nsim.TriggerUplink(\"weather-01\");\n"
             "while (weatherNode.Interval != TimeSpan.FromSeconds(60)) await Task.Delay(100);\n"
             "Console.WriteLine($\"weather-01 now reports every {weatherNode.Interval.TotalSeconds} s ({weather.DownlinkCount} downlinks)\");"),
        md("## Going further\nThe Gallery demo *LoRaWAN network monitor* puts gateways and sensors on a map, with every uplink and its RX windows. "
           "`iotcom lorawan server --sim` runs the same network in a terminal, and `iotcom lorawan simulate` drives ChirpStack or The Things Stack. "
           "See `docs/en/protocols/lorawan.md`.\n\n" + CREDIT[0],
           "## Lebih lanjut\nDemo Galeri *Monitor jaringan LoRaWAN* menaruh gateway dan sensor di peta, lengkap dengan setiap uplink dan jendela RX-nya. "
           "`iotcom lorawan server --sim` menjalankan jaringan yang sama di terminal, dan `iotcom lorawan simulate` menggerakkan ChirpStack atau The Things Stack. "
           "Lihat `docs/id/protocols/lorawan.md`.\n\n" + CREDIT[1]),
    ],
    "metering/10-dlms-mbus": [
        md("# Smart metering: DLMS/COSEM and M-Bus\n\nElectricity meters speak DLMS/COSEM (IEC 62056); heat and water meters in buildings hang on a wired "
           "M-Bus. Here a simulated household meter with rooftop solar and a simulated M-Bus segment stand in for the hardware.",
           "# Smart metering: DLMS/COSEM dan M-Bus\n\nMeter listrik berbicara DLMS/COSEM (IEC 62056); meter panas dan air di gedung tergantung "
           "pada M-Bus berkabel. Di sini meter rumah tangga simulasi dengan panel surya atap dan segmen M-Bus simulasi menggantikan perangkat kerasnya."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP),
        md("## OBIS codes and COSEM data\nEvery value in a meter has a six-group logical name; values travel as A-XDR.",
           "## Kode OBIS dan data COSEM\nSetiap nilai di meter punya nama logis enam grup; nilainya dikirim sebagai A-XDR."),
        code("using IoTCom.Net;\nusing IoTCom.Net.Protocols.Dlms;\n\nvar energyCode = ObisCode.Parse(\"1-0:1.8.0*255\");\n"
             "Console.WriteLine($\"{energyCode} = {energyCode.Description}\");\n"
             "var scalerUnit = CosemData.Structure(CosemData.Int8(-1), CosemData.Enum(CosemUnit.Volt));\n"
             "Console.WriteLine($\"{scalerUnit} → {Convert.ToHexString(scalerUnit.Encode())}\");"),
        md("## Read the meter\nThe public client (SAP 16) associates without a password: it may read, never write. "
           "Registers carry a scaler and a unit; the profile is read with selective access by date.",
           "## Membaca meter\nPublic client (SAP 16) berasosiasi tanpa password: boleh membaca, tidak pernah menulis. "
           "Register membawa scaler dan unit; profil dibaca dengan selective access berdasarkan tanggal."),
        code("using IoTCom.Net.Transports;\n\nvar meterLink = new InMemoryTransportListener(\"meter\");\n"
             "await using var meterServer = DlmsServer.Create(o => o.ListenInMemory(meterLink));\n"
             "await using var householdMeter = new DlmsMeterSimulator(meterServer);\nawait meterServer.StartAsync();\n\n"
             "await using var reader = DlmsClient.Create(o => o.UseInMemory(meterLink));\nawait reader.ConnectAsync();\n"
             "foreach (var code in new[] { \"1.0.1.8.0.255\", \"1.0.2.8.0.255\", \"1.0.32.7.0.255\", \"1.0.14.7.0.255\" })\n"
             "    Console.WriteLine($\"{ObisCode.Parse(code).Description,-34} {await reader.ReadRegisterAsync(ObisCode.Parse(code))}\");\n"
             "var meterNow = await reader.ReadClockAsync();\n"
             "var lastHour = await reader.ReadProfileAsync(ObisCode.Parse(\"1.0.99.1.0.255\"), meterNow.AddHours(-1), meterNow);\n"
             "Console.WriteLine($\"{lastHour.Rows.Count} load-profile rows in the last hour\");"),
        md("## Write access is earned\nThe public client cannot open the relay; the management client with a password can.",
           "## Hak tulis harus diperoleh\nPublic client tidak bisa membuka relay; management client dengan password bisa."),
        code("// Even with the client-side read-only guard off, the meter refuses the public client.\n"
             "await using var curious = DlmsClient.Create(o => { o.UseInMemory(meterLink); o.ReadOnly = false; });\n"
             "await curious.ConnectAsync();\n"
             "try { await curious.ActionAsync(CosemClass.DisconnectControl, ObisCode.Parse(\"0.0.96.3.10.255\"), 1); }\n"
             "catch (DlmsException e) { Console.WriteLine($\"public client: {e.Message}\"); }\n"
             "await using var engineer = DlmsClient.Create(o => { o.UseInMemory(meterLink).WithPassword(\"12345678\"); o.ReadOnly = false; });\n"
             "await engineer.ConnectAsync();\n"
             "await engineer.ActionAsync(CosemClass.DisconnectControl, ObisCode.Parse(\"0.0.96.3.10.255\"), 1);\n"
             "Console.WriteLine($\"relay connected: {householdMeter.Relay.Connected}\");\n"
             "await engineer.ActionAsync(CosemClass.DisconnectControl, ObisCode.Parse(\"0.0.96.3.10.255\"), 2);"),
        md("## M-Bus: scan a segment\nA master pings the primary addresses, then reads each slave's variable data records.",
           "## M-Bus: memindai segmen\nMaster melakukan ping ke alamat primer, lalu membaca record data variabel setiap slave."),
        code("using IoTCom.Net.Protocols.MBus;\n\nvar busLink = new InMemoryTransportListener(\"mbus\");\n"
             "await using var segment = MBusSlaveSimulator.Create(o => o.ListenInMemory(busLink)).AddDefaultDevices();\n"
             "await segment.StartAsync();\n"
             "await using var master = MBusMaster.Create(o => { o.UseInMemory(busLink); o.ResponseTimeout = TimeSpan.FromMilliseconds(100); });\n"
             "await master.ConnectAsync();\n"
             "foreach (var address in await master.ScanAsync(0, 5))\n{\n    var telegram = await master.ReadAsync(address);\n"
             "    Console.WriteLine($\"[{address}] {telegram.SecondaryAddress} {telegram.MediumName}\");\n"
             "    foreach (var record in telegram.Records.Take(3)) Console.WriteLine($\"    {record}\");\n}"),
        md("## Going further\nThe Gallery demo *Smart meter reading* draws the household's day from the load profile and the building's "
           "M-Bus meters. `iotcom dlms read --sim` and `iotcom mbus scan --sim` do the same in a terminal. "
           "See `docs/en/protocols/dlms.md` and `docs/en/protocols/mbus.md`.\n\n" + CREDIT[0],
           "## Lebih lanjut\nDemo Galeri *Pembacaan smart meter* menggambar hari rumah tangga dari load profile dan meter M-Bus gedung. "
           "`iotcom dlms read --sim` dan `iotcom mbus scan --sim` melakukan hal yang sama di terminal. "
           "Lihat `docs/id/protocols/dlms.md` dan `docs/id/protocols/mbus.md`.\n\n" + CREDIT[1]),
    ],
    "devices/11-at-astm": [
        md("# Modems and lab analyzers: AT commands and ASTM\n\nTwo serial-port veterans that are still everywhere: cellular modules driven with AT commands, "
           "and laboratory analyzers that send results with ASTM E1394. Both run against simulators here.",
           "# Modem dan analyzer lab: perintah AT dan ASTM\n\nDua veteran port serial yang masih ada di mana-mana: modul seluler yang dikendalikan dengan perintah AT, "
           "dan analyzer laboratorium yang mengirim hasil dengan ASTM E1394. Keduanya berjalan terhadap simulator di sini."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP),
        md("## A cellular module\nCommands end with OK or an error; unsolicited result codes (URCs) such as `+CEREG` and `+CMTI` arrive at any time.",
           "## Modul seluler\nPerintah diakhiri OK atau error; unsolicited result code (URC) seperti `+CEREG` dan `+CMTI` bisa datang kapan saja."),
        code("using IoTCom.Net;\nusing IoTCom.Net.Protocols.AtCommand;\nusing IoTCom.Net.Transports;\n\n"
             "var modemLink = new InMemoryTransportListener(\"modem\");\n"
             "await using var module = AtModemSimulator.Create(o => { o.ListenInMemory(modemLink); o.RegistrationDelay = TimeSpan.FromMilliseconds(300); });\n"
             "await module.StartAsync();\nawait using var modem = AtModem.Create(o => o.UseInMemory(modemLink));\nawait modem.ConnectAsync();\n"
             "await modem.SendCheckedAsync(\"AT+CEREG=2\");\nawait Task.Delay(600);\n"
             "var info = await modem.GetInfoAsync();\nConsole.WriteLine($\"{info.Model} · {info.Registration} on {info.Operator} ({info.AccessTechnology}) · {info.Signal}\");\n"
             "Console.WriteLine(await modem.SendAsync(\"AT+CPIN=\\\"0000\\\"\"));"),
        md("## SMS in and out", "## SMS masuk dan keluar"),
        code("await modem.SendSmsAsync(\"+6281234567890\", \"Pompa air nyala\");\n"
             "var inbox = new TaskCompletionSource<AtUrc>();\nmodem.UrcReceived += (_, u) => { if (u.Name == \"+CMTI\") inbox.TrySetResult(u); };\n"
             "module.DeliverSms(\"+6289876543210\", \"STATUS?\");\nvar cmti = await inbox.Task;\n"
             "var sms = await modem.ReadSmsAsync(int.Parse(cmti.Values[1]));\nConsole.WriteLine($\"{sms.Sender}: {sms.Text}\");"),
        md("## A chemistry analyzer over ASTM\nThe analyzer sends ENQ, then one framed record at a time, each acknowledged; a corrupted frame is answered with NAK and sent again.",
           "## Analyzer kimia lewat ASTM\nAnalyzer mengirim ENQ, lalu satu record berbingkai setiap kali, masing-masing di-ACK; frame yang rusak dijawab NAK dan dikirim ulang."),
        code("using IoTCom.Net.Protocols.Astm;\n\nvar labLink = new InMemoryTransportListener(\"lab\");\n"
             "await using var lis = AstmReceiver.Create(o => o.ListenInMemory(labLink));\nawait lis.StartAsync();\n"
             "var resultArrived = new TaskCompletionSource<AstmMessage>();\nlis.MessageReceived += (_, m) => resultArrived.TrySetResult(m);\n"
             "await using var analyzer = AstmSender.Create(o => o.UseInMemory(labLink));\nawait analyzer.ConnectAsync();\n"
             "analyzer.CorruptNextFrame = true;\nawait analyzer.SendAsync(new AnalyzerSimulator().NextResult());\nvar labResult = await resultArrived.Task;\n"
             "foreach (var r in labResult.Results) Console.WriteLine($\"{r.TestCode,-5} {r.Value,7} {r.Units,-7} {r.Flag}\");\n"
             "Console.WriteLine($\"retransmitted frames: {analyzer.Retransmissions}\");"),
        md("## Bridge to HL7\nMost hospitals want results as HL7 ORU^R01.", "## Jembatan ke HL7\nKebanyakan rumah sakit menginginkan hasil sebagai HL7 ORU^R01."),
        code("Console.WriteLine(AstmToHl7.ToOru(labResult).Encode().Replace(\"\\r\", \"\\n\"));"),
        md("## Going further\n`iotcom at info --sim`, `iotcom astm listen --hl7` and the AstmAnalyzerBridge sample. "
           "Synthetic data only; not a medical device. See `docs/en/protocols/at-commands.md` and `docs/en/protocols/astm.md`.\n\n" + CREDIT[0],
           "## Lebih lanjut\n`iotcom at info --sim`, `iotcom astm listen --hl7`, dan sampel AstmAnalyzerBridge. "
           "Data sintetis saja; bukan perangkat medis. Lihat `docs/id/protocols/at-commands.md` dan `docs/id/protocols/astm.md`.\n\n" + CREDIT[1]),
    ],
    "messaging/12-mdns-sparkplug": [
        md("# Plant networks: mDNS discovery, Sparkplug B and payload codecs\n\nFind devices without a DNS server, give MQTT a state model with Sparkplug B, "
           "and read the binary payloads you meet on the way (Protobuf, MessagePack, TLV). Everything runs in this process.",
           "# Jaringan pabrik: penemuan mDNS, Sparkplug B, dan codec payload\n\nTemukan perangkat tanpa server DNS, beri MQTT model state dengan Sparkplug B, "
           "dan baca payload biner yang Anda temui di jalan (Protobuf, MessagePack, TLV). Semuanya berjalan di proses ini."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP + f'\n#r "nuget: IoTCom.Net.Serialization.Protobuf, {VERSION}"\n#r "nuget: IoTCom.Net.Serialization.MessagePack, {VERSION}"'),
        md("## Who is on the network?\nA browser asks 224.0.0.251:5353 for every service type, then for the instances of each. "
           "`MdnsSimulator` runs a small plant segment in memory; `MdnsBrowser.Create()` without options uses the real network.",
           "## Siapa saja di jaringan?\nBrowser bertanya ke 224.0.0.251:5353 untuk setiap tipe layanan, lalu untuk instance masing-masing. "
           "`MdnsSimulator` menjalankan segmen pabrik kecil di memori; `MdnsBrowser.Create()` tanpa opsi memakai jaringan sungguhan."),
        code("using IoTCom.Net.Protocols.Mdns;\n\n"
             "await using var plant = new MdnsSimulator();\nawait plant.StartAsync();\n"
             "await using var browser = plant.Browser();\nawait browser.StartAsync();\n"
             "foreach (var type in await browser.EnumerateTypesAsync(TimeSpan.FromMilliseconds(500)))\n"
             "    foreach (var svc in await browser.BrowseAsync(type.Replace(\".local\", \"\"), TimeSpan.FromMilliseconds(300)))\n"
             "        Console.WriteLine($\"{svc.Type,-14} {svc.Instance,-24} {svc.Address}:{svc.Port}  {string.Join(\" \", svc.Properties.Select(p => $\"{p.Key}={p.Value}\"))}\");"),
        md("## Sparkplug B: births, data and death\nThe edge node registers NDEATH as its MQTT will and publishes NBIRTH/DBIRTH with every metric and an alias. "
           "The host application resolves aliases in later DDATA messages.",
           "## Sparkplug B: birth, data, dan death\nEdge node mendaftarkan NDEATH sebagai will MQTT dan menerbitkan NBIRTH/DBIRTH dengan setiap metrik beserta alias. "
           "Host application menerjemahkan alias pada pesan DDATA berikutnya."),
        code("using System.Net;\nusing System.Net.Sockets;\nusing IoTCom.Net.Adapters.Mqtt;\nusing IoTCom.Net.Protocols.Sparkplug;\n\n"
             "var probe = new TcpListener(IPAddress.Loopback, 0);\nprobe.Start();\nvar mqttPort = ((IPEndPoint)probe.LocalEndpoint).Port;\nprobe.Stop();\n"
             "var broker = MqttBroker.Create(mqttPort);\nawait broker.StartAsync();\n"
             "var scada = SparkplugHost.Create(o => { o.HostId = \"notebook\"; o.Mqtt = m => m.UseBroker(\"127.0.0.1\", mqttPort); });\nawait scada.StartAsync();\n"
             "var edge = SparkplugEdgeNode.Create(o => { o.Group = \"Plant\"; o.EdgeNode = \"Line1\"; o.Mqtt = m => m.UseBroker(\"127.0.0.1\", mqttPort); });\n"
             "var bottling = new SparkplugLineSimulator(edge).Define();\nawait edge.StartAsync();\n"
             "for (var i = 0; i < 3; i++) { await bottling.Step(); await Task.Delay(200); }\n"
             "foreach (var v in scada.Views.OrderBy(v => v.Key))\n"
             "    Console.WriteLine($\"{v.Key,-20} {(v.Online ? \"online \" : \"offline\")} {string.Join(\", \", v.Metrics.Values.Where(m => m.Name != \"Node Control/Rebirth\").Select(m => m.ToString()))}\");"),
        md("## Commands and a pulled cable\nWrites travel as DCMD and come back as DDATA. When the connection drops, the broker publishes the NDEATH will "
           "and the host marks the node and its devices offline.",
           "## Perintah dan kabel yang dicabut\nPenulisan dikirim sebagai DCMD dan kembali sebagai DDATA. Saat koneksi putus, broker menerbitkan will NDEATH "
           "dan host menandai node beserta perangkatnya offline."),
        code("await scada.WriteAsync(\"Plant\", \"Line1\", \"Filler\", \"Running\", false);\nawait Task.Delay(300);\n"
             "Console.WriteLine($\"Filler running: {scada.Find(\"Plant\", \"Line1\", \"Filler\")!.Metrics[\"Running\"].Value}\");\n"
             "await edge.DropConnectionAsync();\nawait Task.Delay(500);\n"
             "Console.WriteLine($\"Line1 online: {scada.Find(\"Plant\", \"Line1\")!.Online} · next bdSeq {edge.BdSeq}\");\n"
             "await scada.DisposeAsync();\nawait broker.DisposeAsync();"),
        md("## What a Sparkplug payload looks like\nIt is Protobuf: `SparkplugPayload.Describe` names the fields, `ProtobufWire` reads any message without a schema.",
           "## Seperti apa payload Sparkplug\nIni Protobuf: `SparkplugPayload.Describe` memberi nama field, `ProtobufWire` membaca pesan apa pun tanpa skema."),
        code("using IoTCom.Net.Serialization.Protobuf;\n\n"
             "var payload = new SparkplugPayload { Timestamp = 1, Seq = 4, Metrics = [SparkplugMetric.Of(\"Level\", SparkplugDataType.Double, 61.5, alias: 7)] }.Encode();\n"
             "Console.WriteLine(Convert.ToHexString(payload));\n"
             "foreach (var f in SparkplugPayload.Describe(payload)) Console.WriteLine($\"{f.Name,-10} {f.Value}\");\n"
             "ProtobufWire.TryInspect(payload, out var wire);\nforeach (var f in wire) Console.WriteLine($\"#{f.Number} wire {f.WireType}: {f.Value}\");"),
        md("## MessagePack and TLV\nMessagePack is JSON-shaped binary; BER-TLV is the tag-length-value layout of smart cards and EMV.",
           "## MessagePack dan TLV\nMessagePack adalah biner berbentuk JSON; BER-TLV adalah tata letak tag-length-value pada smart card dan EMV."),
        code("using IoTCom.Net.Serialization.MessagePack;\nusing IoTCom.Net.Serialization.Tlv;\n\n"
             "var packed = new MessagePackCodec<Dictionary<string, double>>(MessagePack.MessagePackSerializerOptions.Standard).Encode(new() { [\"temp\"] = 27.5, [\"rh\"] = 71 });\n"
             "Console.WriteLine($\"{Convert.ToHexString(packed)} → {MessagePackView.ToJson(packed)}\");\n"
             "var fci = Convert.FromHexString(\"6F148407A0000000031010A5095004564953419F3800\");\n"
             "void Show(IEnumerable<TlvItem> items, string indent = \"\") { foreach (var i in items) { Console.WriteLine($\"{indent}{i.Tag:X} {(i.Children.Count > 0 ? \"\" : Convert.ToHexString(i.Value))}\"); Show(i.Children, indent + \"  \"); } }\n"
             "Show(BerTlv.Decode(fci));"),
        md("## Going further\n`iotcom mdns browse`, `iotcom sparkplug watch --sim`, `iotcom payload protobuf <hex>`, the Gallery's *Plant network* demo and the "
           "MdnsDiscovery and SparkplugEdgeNode samples. See `docs/en/protocols/mdns.md`, `sparkplug.md` and `payload-codecs.md`.\n\n" + CREDIT[0],
           "## Lebih lanjut\n`iotcom mdns browse`, `iotcom sparkplug watch --sim`, `iotcom payload protobuf <hex>`, demo *Jaringan pabrik* di Gallery, serta "
           "sampel MdnsDiscovery dan SparkplugEdgeNode. Lihat `docs/id/protocols/mdns.md`, `sparkplug.md`, dan `payload-codecs.md`.\n\n" + CREDIT[1]),
    ],
    "industrial/13-opcua": [
        md("# OPC UA — browse, read, subscribe, write\n\nOPC UA is how modern PLCs, SCADA and MES systems expose their data: an address space of objects, "
           "variables and methods, reached over a secure session. IoTCom.Net wraps the OPC Foundation stack; this notebook runs its plant simulator in-process.",
           "# OPC UA — jelajah, baca, subscribe, tulis\n\nOPC UA adalah cara PLC, SCADA, dan MES modern membuka datanya: address space berisi objek, "
           "variabel, dan method, dicapai lewat sesi yang aman. IoTCom.Net membungkus stack OPC Foundation; notebook ini menjalankan simulator pabriknya di dalam proses."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP + f'\n#r "nuget: IoTCom.Net.Adapters.OpcUa, {VERSION}"'),
        md("## Start the plant simulator and connect\nThe client picks the most secure endpoint (Basic256Sha256, SignAndEncrypt); both sides create a "
           "self-signed application certificate on first use. `ReadOnly = true` until we decide to write.",
           "## Jalankan simulator pabrik dan sambungkan\nClient memilih endpoint paling aman (Basic256Sha256, SignAndEncrypt); kedua pihak membuat "
           "sertifikat aplikasi self-signed saat pertama dipakai. `ReadOnly = true` sampai kita memutuskan untuk menulis."),
        code("using IoTCom.Net.Adapters.OpcUa;\n\n"
             "var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);\nprobe.Start();\nvar uaPort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;\nprobe.Stop();\n"
             "var plc = OpcUaPlantServer.Create(o => { o.Port = uaPort; o.Interval = TimeSpan.FromMilliseconds(300); });\nawait plc.StartAsync();\n"
             "var ua = OpcUaClient.Create(o => { o.UseEndpoint(plc.EndpointUrl); o.AcceptUntrustedCertificates = true; o.ReadOnly = true; });\n"
             "await ua.ConnectAsync();\nConsole.WriteLine($\"{plc.EndpointUrl} · {ua.SecurityPolicy} / {ua.SecurityMode}\");"),
        md("## Browse and read\nNode ids carry a namespace index: `ns=2;s=Plant/Line1/Filler/Speed`.",
           "## Jelajah dan baca\nNode id membawa indeks namespace: `ns=2;s=Plant/Line1/Filler/Speed`."),
        code("async Task Tree(string? nodeId, string indent)\n{\n"
             "    foreach (var n in await ua.BrowseAsync(nodeId))\n    {\n"
             "        if (n.NodeId.StartsWith(\"i=\")) continue;\n"
             "        var value = n.NodeClass == \"Variable\" ? \" = \" + (await ua.ReadAsync(n.NodeId)).Text : \"\";\n"
             "        Console.WriteLine($\"{indent}{n.DisplayName}{value}\");\n"
             "        if (n.IsContainer) await Tree(n.NodeId, indent + \"  \");\n    }\n}\nawait Tree(null, \"\");"),
        md("## Subscribe\nThe server samples the monitored items and publishes only changes.",
           "## Subscribe\nServer mengambil sampel monitored item dan hanya menerbitkan perubahan."),
        code("using var window = new CancellationTokenSource(TimeSpan.FromSeconds(2));\nvar changes = 0;\ntry\n{\n"
             "    await foreach (var v in ua.SubscribeAsync([plc.NodeId(\"Line1/Tank7/Level\"), plc.NodeId(\"Line1/Filler/Speed\")], TimeSpan.FromMilliseconds(200), window.Token))\n"
             "        if (changes++ < 8) Console.WriteLine($\"{v.SourceTimestamp?.ToLocalTime():HH:mm:ss.fff} {v.NodeId} = {v.Text}\");\n}\n"
             "catch (OperationCanceledException) { }\nConsole.WriteLine($\"{changes} notifications in 2 s\");"),
        md("## Writes are refused until you allow them",
           "## Penulisan ditolak sampai Anda mengizinkannya"),
        code("try { await ua.WriteAsync(plc.NodeId(\"Line1/Filler/Setpoint\"), 100); }\n"
             "catch (IoTCom.Net.ReadOnlyModeException e) { Console.WriteLine(\"read-only: \" + e.Message); }\n\n"
             "await using var operatorUa = OpcUaClient.Create(o => { o.UseEndpoint(plc.EndpointUrl); o.AcceptUntrustedCertificates = true; });\n"
             "await operatorUa.ConnectAsync();\nawait operatorUa.WriteAsync(plc.NodeId(\"Line1/Filler/Setpoint\"), \"100\");   // converted to Double\n"
             "Console.WriteLine($\"setpoint now {(await ua.ReadAsync(plc.NodeId(\"Line1/Filler/Setpoint\"))).Text}\");\n"
             "var previous = await operatorUa.CallAsync(plc.NodeId(\"Line1\"), plc.NodeId(\"Line1/ResetCounter\"));\n"
             "Console.WriteLine($\"ResetCounter() returned {previous[0]}\");\n"
             "await ua.DisposeAsync();\nawait plc.DisposeAsync();"),
        md("## Going further\n`iotcom opcua browse --sim`, `iotcom opcua watch`, the Gallery's *OPC UA tag browser* and the OpcUaBrowser sample. "
           "See `docs/en/protocols/opcua.md` for certificates and trust.\n\n" + CREDIT[0],
           "## Lebih lanjut\n`iotcom opcua browse --sim`, `iotcom opcua watch`, *Penjelajah tag OPC UA* di Gallery, dan sampel OpcUaBrowser. "
           "Lihat `docs/id/protocols/opcua.md` untuk sertifikat dan trust.\n\n" + CREDIT[1]),
    ],
    "devices/14-ble": [
        md("# Bluetooth Low Energy — advertisements, GATT, notifications\n\nA central scans advertisements, connects to peripherals and reads, writes and subscribes to "
           "characteristics. The virtual radio below behaves like the real one; switch to `o.UseNative()` to use your Bluetooth adapter.",
           "# Bluetooth Low Energy — advertisement, GATT, notifikasi\n\nCentral memindai advertisement, tersambung ke periferal, lalu membaca, menulis, dan subscribe ke "
           "characteristic. Radio virtual di bawah berperilaku seperti yang sungguhan; ganti ke `o.UseNative()` untuk memakai adaptor Bluetooth Anda."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP),
        md("## Decode an advertisement by hand\nAdvertising data is a list of length–type–value structures; an iBeacon is Apple manufacturer data.",
           "## Urai advertisement secara manual\nAdvertising data adalah daftar struktur length–type–value; iBeacon adalah data pabrikan Apple."),
        code("using IoTCom.Net.Transport.Ble;\n\n"
             "var raw = Convert.FromHexString(\"020106\" + \"03030D18\" + \"040948524D\" + \"1AFF4C000215E2C56DB5DFFB48D2B060D0F5A71096E000070001C5\");\n"
             "var parsed = AdvertisingData.Parse(\"demo\", raw, rssi: -68);\n"
             "Console.WriteLine($\"{parsed.Name} · services {string.Join(\",\", parsed.Services.Select(BleUuid.Name))} · iBeacon {parsed.IBeacon} · ≈{parsed.EstimatedDistance} m\");\n"
             "foreach (var f in AdvertisingData.Describe(raw)) Console.WriteLine($\"  {f.Name,-5} {f.Value}\");"),
        md("## Scan a (virtual) room", "## Pindai ruangan (virtual)"),
        code("var room = new VirtualBleNetwork();\nroom.AddHeartRateStrap();\nroom.AddEnvironmentSensor();\nroom.AddBeacon();\nroom.AddSmartPlug();\n"
             "var physics = new VirtualBleSimulator(room);\n"
             "var ble = BleCentral.Create(o => { o.UseVirtual(room); o.ReadOnly = true; });\nawait ble.ConnectAsync();\n"
             "foreach (var ad in await ble.ScanAsync(TimeSpan.FromMilliseconds(600)))\n"
             "    Console.WriteLine($\"{ad.Name,-18} {ad.Rssi,4} dBm  {string.Join(\", \", ad.Services.Select(BleUuid.Name))}{(ad.IBeacon is { } b ? $\" iBeacon {b.Major}/{b.Minor}\" : \"\")}\");"),
        md("## Connect, read, subscribe", "## Sambung, baca, subscribe"),
        code("var strap = await ble.OpenAsync(\"C4:7C:8D:6A:21:0F\");\n"
             "foreach (var svc in strap.Services) Console.WriteLine($\"{svc.Name}: {string.Join(\", \", svc.Characteristics.Select(c => $\"{c.Name} ({c.Properties})\"))}\");\n"
             "Console.WriteLine($\"battery {GattValue.Describe(BleUuid.Parse(\"2a19\"), await strap.ReadAsync(\"2a19\"))}\");\n"
             "using var beatWindow = new CancellationTokenSource(TimeSpan.FromSeconds(3));\n"
             "_ = Task.Run(async () => { while (!beatWindow.IsCancellationRequested) { physics.Step(); await Task.Delay(250); } });\n"
             "try { await foreach (var v in strap.SubscribeAsync(BleUuid.FromShort(0x2A37), beatWindow.Token)) Console.WriteLine(GattValue.Describe(BleUuid.FromShort(0x2A37), v)); }\n"
             "catch (OperationCanceledException) { }"),
        md("## Writes are refused in read-only mode", "## Penulisan ditolak dalam mode read-only"),
        code("var plug = await ble.OpenAsync(\"D0:8E:3A:55:10:C2\");\n"
             "try { await plug.WriteAsync(VirtualBleNetwork.SmartPlugRelay, new byte[] { 1 }); }\n"
             "catch (IoTCom.Net.ReadOnlyModeException e) { Console.WriteLine(\"read-only: \" + e.Message); }\n"
             "await ble.DisposeAsync();"),
        md("## Going further\n`iotcom ble scan` (real radio) or `--sim`, `iotcom ble watch --sim C4:7C:8D:6A:21:0F 2a37`, the Gallery's *Nearby Bluetooth devices* "
           "and the BleHeartRate sample. See `docs/en/protocols/ble.md`.\n\n" + CREDIT[0],
           "## Lebih lanjut\n`iotcom ble scan` (radio sungguhan) atau `--sim`, `iotcom ble watch --sim C4:7C:8D:6A:21:0F 2a37`, *Perangkat Bluetooth di sekitar* di Gallery, "
           "dan sampel BleHeartRate. Lihat `docs/id/protocols/ble.md`.\n\n" + CREDIT[1]),
    ],
    "devices/15-usb": [
        md("# USB and HID — enumerate, control, bulk, reports\n\nRaw USB transfers and HID reports through one API. The cells use a virtual bus; replace the backend "
           "with `NativeUsbBackend.Instance` (the default) to reach real devices.",
           "# USB dan HID — enumerasi, control, bulk, report\n\nTransfer USB mentah dan report HID lewat satu API. Sel-selnya memakai bus virtual; ganti backend "
           "dengan `NativeUsbBackend.Instance` (bawaan) untuk menjangkau perangkat sungguhan."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP),
        md("## Enumerate", "## Enumerasi"),
        code("using System.Text;\nusing IoTCom.Net.Transport.Usb;\n\n"
             "var usbBus = new VirtualUsbBus();\nusbBus.Add(new VirtualLoopbackDevice());\nvar boardSim = usbBus.AddHid(new VirtualHidRelayBoard(relays: 4));\n"
             "foreach (var d in UsbDevice.List(usbBus)) Console.WriteLine($\"{d.Id}  {d.Manufacturer} {d.Product}  [{string.Join(\", \", d.Interfaces.Select(i => i.ClassName))}]\");\n"
             "foreach (var h in HidDevice.List(usbBus)) Console.WriteLine($\"{h.VendorId:x4}:{h.ProductId:x4}  {h.Product}  {h.UsageName}\");\n"
             "Console.WriteLine(UsbIds.Known(0x1D50, 0x606F));"),
        md("## Control and bulk transfers\nA vendor request reads the firmware version; data written to bulk OUT 0x01 comes back on bulk IN 0x81.",
           "## Transfer control dan bulk\nVendor request membaca versi firmware; data yang ditulis ke bulk OUT 0x01 kembali di bulk IN 0x81."),
        code("var loopDev = UsbDevice.Create(o => { o.UseVirtual(usbBus); o.DeviceId = \"1209:0001\"; });\nawait loopDev.ConnectAsync();\n"
             "Console.WriteLine(Encoding.ASCII.GetString(await loopDev.ControlInAsync(UsbSetup.Vendor(0x01), 16)));\n"
             "await loopDev.WriteAsync(0x01, Encoding.ASCII.GetBytes(\"ping\"));\nConsole.WriteLine(Encoding.ASCII.GetString((await loopDev.ReadAsync(0x81))!));\n"
             "await loopDev.DisposeAsync();"),
        md("## A USB HID relay board\nFeature report `00 FF n` switches relay n on; reading feature report 0 returns the serial and the relay bits.",
           "## Papan relay USB HID\nFeature report `00 FF n` menyalakan relay n; membaca feature report 0 mengembalikan serial dan bit relay."),
        code("var relayHid = HidDevice.Create(o => { o.UseVirtual(usbBus); o.UseDevice(HidRelayBoard.VendorId, HidRelayBoard.ProductId); });\n"
             "await relayHid.ConnectAsync();\nvar relayBoard = new HidRelayBoard(relayHid);\n"
             "await relayBoard.SetAsync(3, true);\nConsole.WriteLine($\"{await relayBoard.GetSerialAsync()}: {string.Join(\" \", await relayBoard.GetStatesAsync())} (bits {boardSim.State})\");\n"
             "var readOnlyHid = HidDevice.Create(o => { o.UseVirtual(usbBus); o.UseDevice(HidRelayBoard.VendorId, HidRelayBoard.ProductId); o.ReadOnly = true; });\n"
             "await readOnlyHid.ConnectAsync();\n"
             "try { await new HidRelayBoard(readOnlyHid).SetAllAsync(true); } catch (IoTCom.Net.ReadOnlyModeException e) { Console.WriteLine(\"read-only: \" + e.Message); }"),
        md("## Going further\n`iotcom usb list`, `iotcom usb hid`, `iotcom usb relay --sim`, the Gallery's *USB bench* and the UsbRelay sample. "
           "See `docs/en/protocols/usb.md` for drivers and permissions.\n\n" + CREDIT[0],
           "## Lebih lanjut\n`iotcom usb list`, `iotcom usb hid`, `iotcom usb relay --sim`, *Meja kerja USB* di Gallery, dan sampel UsbRelay. "
           "Lihat `docs/id/protocols/usb.md` untuk driver dan izin.\n\n" + CREDIT[1]),
    ],
    "industrial/16-canopen": [
        md("# CANopen — SDO, PDO, NMT and heartbeats\n\nA master and a simulated CiA 401-style I/O module share a virtual CAN bus. Replace the bus with "
           "`await CanBus.OpenAsync(\"socketcan:can0\")` (or `slcan:`, `gsusb:`, `pcan:usb1`) to reach real nodes.",
           "# CANopen — SDO, PDO, NMT, dan heartbeat\n\nMaster dan modul I/O simulasi bergaya CiA 401 berbagi bus CAN virtual. Ganti bus dengan "
           "`await CanBus.OpenAsync(\"socketcan:can0\")` (atau `slcan:`, `gsusb:`, `pcan:usb1`) untuk menjangkau node sungguhan."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP),
        md("## Boot a node and scan the network\nThe module sends a boot-up message, then heartbeats; the scan reads 0x1000, 0x1008 and 0x1018 over SDO.",
           "## Nyalakan node dan pindai jaringan\nModul mengirim pesan boot-up, lalu heartbeat; pemindaian membaca 0x1000, 0x1008, dan 0x1018 lewat SDO."),
        code("using IoTCom.Net.Protocols.CanOpen;\nusing IoTCom.Net.Transport.Can;\n\n"
             "var coNet = new VirtualCanNetwork(\"notebook\");\nvar ioModule = CanOpenIoModuleSimulator.Create(coNet.CreateNode(), nodeId: 5, heartbeatMs: 200, eventTimerMs: 150);\n"
             "var coBus = coNet.CreateNode();\nvar coMaster = CanOpenMaster.Create(coBus);\nawait coMaster.StartAsync();\nawait ioModule.StartAsync();\n"
             "foreach (var n in await coMaster.ScanAsync(1, 10, TimeSpan.FromMilliseconds(50)))\n"
             "    Console.WriteLine($\"node {n.Id}: {n.Name}, type 0x{n.DeviceType:X8}, vendor 0x{n.Identity?.Vendor:X8}, serial {n.Identity?.Serial:X8}, {n.State}\");"),
        md("## SDO: expedited, segmented and aborts\nValues up to 4 bytes fit one frame; longer ones are split into 7-byte segments with a toggle bit.",
           "## SDO: expedited, bersegmen, dan abort\nNilai hingga 4 byte muat dalam satu frame; yang lebih panjang dipecah menjadi segmen 7 byte dengan bit toggle."),
        code("Console.WriteLine(await coMaster.ReadAsync(5, 0x6401, 1, CanOpenDataType.Integer16));             // expedited\n"
             "Console.WriteLine(await coMaster.ReadAsync(5, 0x2100, 0, CanOpenDataType.VisibleString));          // segmented\n"
             "await coMaster.WriteAsync(5, 0x2100, 0, CanOpenDataType.VisibleString, \"Pump skid 9, Bekasi\");\n"
             "Console.WriteLine(ioModule.Node.Dictionary[0x2100, 0]);\n"
             "try { await coMaster.UploadAsync(5, 0x9999, 0); } catch (CanOpenSdoException e) { Console.WriteLine(e.Message); }\n"
             "try { await coMaster.WriteAsync(5, 0x1000, 0, CanOpenDataType.Unsigned32, 1); } catch (CanOpenSdoException e) { Console.WriteLine(e.Message); }"),
        md("## NMT start, PDOs and SYNC\nOnly operational nodes send PDOs. TPDO1 is event-driven with an event timer; TPDO2 answers every SYNC.",
           "## NMT start, PDO, dan SYNC\nHanya node operational yang mengirim PDO. TPDO1 digerakkan event dengan event timer; TPDO2 menjawab setiap SYNC."),
        code("var mapping = await coMaster.ReadTpdoMappingAsync(5, 1);\nConsole.WriteLine(string.Join(\", \", mapping.Objects));\n"
             "var pdoCount = 0;\ncoMaster.PdoReceived += (node, pdo, data) => { if (pdoCount++ < 4) Console.WriteLine($\"TPDO{pdo} node {node}: {Convert.ToHexString(data)}\"); };\n"
             "await coMaster.NmtAsync(NmtCommand.Start, 5);\n"
             "await coMaster.WriteAsync(5, 0x6200, 1, CanOpenDataType.Unsigned8, 1);                             // pump on\n"
             "ioModule.Step();\nawait coMaster.SyncAsync();\nawait Task.Delay(400);\n"
             "Console.WriteLine($\"flow {ioModule.Node.Dictionary[0x6401, 2].Value} (0.1 l/min), state {coMaster.Nodes.First().State}\");"),
        md("## On the wire", "## Di jalur"),
        code("foreach (var f in CanOpenCodec.Describe(new CanFrame(0x585, Convert.FromHexString(\"410810000B000000\"))))\n"
             "    Console.WriteLine($\"{f.Name,-8} {f.Value}\");\n"
             "await ioModule.DisposeAsync();\nawait coMaster.DisposeAsync();"),
        md("## Going further\n`iotcom canopen scan --can sim`, `iotcom canopen monitor --can sim`, the Gallery's *CANopen I/O modules* and the CanOpenMaster sample. "
           "See `docs/en/protocols/canopen.md`.\n\n" + CREDIT[0],
           "## Lebih lanjut\n`iotcom canopen scan --can sim`, `iotcom canopen monitor --can sim`, *Modul I/O CANopen* di Gallery, dan sampel CanOpenMaster. "
           "Lihat `docs/id/protocols/canopen.md`.\n\n" + CREDIT[1]),
    ],
    "automotive/06-can-uds": [
        md("# Automotive: CAN, ISO-TP, UDS and OBD-II\n\nA scan tool and a simulated engine ECU share a virtual CAN bus. Swap the URI for "
           "`socketcan:can0` or `slcan:COM5` to talk to real hardware — only on vehicles you are authorised to service.",
           "# Otomotif: CAN, ISO-TP, UDS, dan OBD-II\n\nScan tool dan ECU mesin simulasi berbagi bus CAN virtual. Ganti URI menjadi "
           "`socketcan:can0` atau `slcan:COM5` untuk perangkat sungguhan — hanya pada kendaraan yang Anda berwenang servis."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP + f'\n#r "nuget: IoTCom.Net.Protocols.Uds, {VERSION}"'),
        md("## CAN frames in candump notation\nFrames are validated (11/29-bit identifiers, classic or CAN FD lengths).",
           "## Frame CAN dalam notasi candump\nFrame divalidasi (identifier 11/29-bit, panjang klasik atau CAN FD)."),
        code("using IoTCom.Net.Transport.Can;\n\nvar request = CanFrame.Parse(\"7DF#02010C\");   // OBD-II: engine rpm\n"
             "Console.WriteLine($\"{request} → id 0x{request.Id:X3}, {request.Data.Length} bytes, DLC {request.Dlc}\");\n"
             "var fd = new CanFrame(0x18DAF110, CanDlc.Pad(new byte[20]), CanFrameFlags.Extended | CanFrameFlags.Fd);\n"
             "Console.WriteLine($\"CAN FD: {fd.Data.Length} bytes (padded), DLC {fd.Dlc}\");"),
        md("## An ECU simulator and a scan tool on a virtual bus", "## Simulator ECU dan scan tool di bus virtual"),
        code("using IoTCom.Net.Protocols.Uds;\n\nvar net = new VirtualCanNetwork();\n"
             "await using var ecu = EcuSimulator.Create(net.CreateNode());\nawait ecu.StartAsync();\n"
             "var bus = net.CreateNode();\nawait bus.ConnectAsync();\nusing var sniffer = bus.OpenReader();\n\n"
             "await using var obd = ObdClient.Create(bus);\nawait obd.ConnectAsync();\n"
             "foreach (var pid in new[] { ObdPids.EngineRpm, ObdPids.CoolantTemperature, ObdPids.ModuleVoltage })\n"
             "    Console.WriteLine(await obd.ReadPidAsync(pid));\n"
             "Console.WriteLine($\"VIN {await obd.ReadVinAsync()}\");"),
        md("## ISO-TP on the wire\nThe 17-character VIN needs a first frame, a flow control and consecutive frames — segmented by the Rust state machine.",
           "## ISO-TP di jalur\nVIN 17 karakter butuh first frame, flow control, dan consecutive frame — dipecah oleh state machine Rust."),
        code("while (sniffer.TryRead(out var f)) if (f.Data.Length > 0 && f.Data.Span[0] >> 4 is 1 or 2 or 3) Console.WriteLine(f);"),
        md("## UDS: identification, trouble codes and security access", "## UDS: identifikasi, kode kerusakan, dan security access"),
        code("await using var uds = UdsClient.Create(bus);\nawait uds.ConnectAsync();\n"
             "Console.WriteLine(await uds.ReadStringAsync(UdsDid.SoftwareVersion));\n"
             "foreach (var dtc in await uds.ReadDtcsAsync()) Console.WriteLine($\"{dtc}  {dtc.Status}\");\n\n"
             "try { await uds.WriteDataByIdentifierAsync(UdsDid.RepairShopCode, \"WS-01\"u8.ToArray()); }\n"
             "catch (UdsNegativeResponseException ex) { Console.WriteLine(ex.Message); }\n\n"
             "await uds.StartSessionAsync(UdsSession.Extended);\nawait uds.SecurityAccessAsync(0x01, EcuSimulator.ComputeKey);\n"
             "await uds.WriteDataByIdentifierAsync(UdsDid.RepairShopCode, \"WS-01\"u8.ToArray());\n"
             "Console.WriteLine(await uds.ReadStringAsync(UdsDid.RepairShopCode));"),
        md("## A USB CAN adapter\n`gsusb:` opens a candleLight / CANable adapter over raw USB and `pcan:usb1` a PEAK PCAN-USB. Here a virtual "
           "candleLight sits on a virtual USB bus and is wired to its own virtual CAN network, so the gs_usb protocol runs end to end.",
           "## Adapter CAN USB\n`gsusb:` membuka adapter candleLight / CANable lewat USB mentah dan `pcan:usb1` membuka PEAK PCAN-USB. Di sini "
           "candleLight virtual berada di bus USB virtual dan tersambung ke jaringan CAN virtualnya sendiri, sehingga protokol gs_usb berjalan dari ujung ke ujung."),
        code("using IoTCom.Net.Transport.Can.Adapters;\nusing IoTCom.Net.Transport.Usb;\n\n"
             "var benchCan = new VirtualCanNetwork(\"bench\");\nvar benchUsb = new VirtualUsbBus();\nvar candle = benchUsb.Add(new VirtualGsUsbDevice(benchCan));\n"
             "var benchEcu = benchCan.CreateNode();\nawait benchEcu.ConnectAsync();\nusing var atEcu = benchEcu.OpenReader();\n"
             "await using var gs = new GsUsbCanBus(backend: benchUsb, options: new CanBusOptions { Bitrate = 500_000 });\nawait gs.ConnectAsync();\n"
             "Console.WriteLine($\"{gs.Channel}: brp {gs.Timing!.Brp}, {gs.Timing.Quanta} tq, sample point {gs.Timing.SamplePoint:P1}\");\n"
             "await gs.SendAsync(CanFrame.Parse(\"7DF#02010C0000000000\"));\nConsole.WriteLine($\"ECU received {await atEcu.ReadAsync()}\");\n"
             "Console.WriteLine(Convert.ToHexString(GsUsbCodec.EncodeFrame(GsUsbCodec.EchoIdRx, CanFrame.Parse(\"7E8#03410C1A\"))));"),
        md("## Going further\nThe Gallery demo *Vehicle diagnostics* shows the same stack with a live dashboard; the CLI has "
           "`iotcom obd live --can sim` and `iotcom can list` (it finds candleLight and PCAN adapters). See `docs/en/protocols/uds.md` and `can.md`.\n\n" + CREDIT[0],
           "## Lebih lanjut\nDemo Galeri *Diagnostik kendaraan* menampilkan tumpukan yang sama dengan dasbor langsung; CLI punya "
           "`iotcom obd live --can sim` dan `iotcom can list` (menemukan adapter candleLight dan PCAN). Lihat `docs/id/protocols/uds.md` dan `can.md`.\n\n" + CREDIT[1]),
    ],
    "medical/05-hl7-dicom": [
        md("# Healthcare: HL7 v2 and DICOM\n\nBedside monitors, analyzers and modalities talk **HL7 v2** (events and results over MLLP) and "
           "**DICOM** (images over C-STORE). Everything here is synthetic — fictional patients and schematic phantoms, never for clinical use.",
           "# Kesehatan: HL7 v2 dan DICOM\n\nMonitor pasien, analyzer, dan modalitas berbicara **HL7 v2** (kejadian dan hasil lewat MLLP) dan "
           "**DICOM** (gambar lewat C-STORE). Semua di sini sintetis — pasien fiktif dan phantom skematis, tidak untuk penggunaan klinis."),
        md("## Setup\n" + LOCAL[0], "## Persiapan\n" + LOCAL[1]),
        code(SETUP + f'\n#r "nuget: IoTCom.Net.Adapters.Dicom, {VERSION}"'),
        md("## Build and parse an ORU^R01\nOne OBX segment per measurement, LOINC-coded.",
           "## Bangun dan urai ORU^R01\nSatu segmen OBX per pengukuran, berkode LOINC."),
        code("using IoTCom.Net.Protocols.Hl7;\n\nvar (patient, scenario, bed) = PatientMonitorSimulator.DemoWard[0];\n"
             "var monitor = new PatientMonitorSimulator(patient, scenario, bed, onset: TimeSpan.Zero);\n"
             "var oru = monitor.ToOru(monitor.Next(TimeSpan.FromMinutes(8), DateTimeOffset.Now));\n"
             "Console.WriteLine(oru.Encode().Replace('\\r', '\\n'));\n"
             "foreach (var o in Hl7Message.Parse(oru.Encode()).GetObservations())\n"
             "    Console.WriteLine($\"{o.Code.Text,-36} {o.Value,6} {o.Units,-8} {o.AbnormalFlag}\");"),
        md("## Send over MLLP and receive the ACK", "## Kirim lewat MLLP dan terima ACK"),
        code("using IoTCom.Net;\nusing IoTCom.Net.Transports;\n\nvar link = new InMemoryTransportListener();\n"
             "await using var receiver = Hl7MllpServer.Create(o => o.ListenInMemory(link));\n"
             "receiver.MessageReceived += (_, e) => Console.WriteLine($\"received {e.Message.MessageType} {e.Message.ControlId}\");\n"
             "await receiver.StartAsync();\n"
             "await using var sender = Hl7MllpClient.Create(o => o.UseInMemory(link));\n"
             "var ack = await sender.SendAsync(oru);\n"
             "Console.WriteLine($\"ACK {ack.AckCode()} for {ack[\"MSA.2\"]}\");"),
        md("## DICOM: a synthetic study over C-STORE, rendered with a window\n"
           "The planted finding is the ground truth (useful for testing AI pipelines).",
           "## DICOM: studi sintetis lewat C-STORE, dirender dengan window\n"
           "Temuan yang ditanam adalah ground truth (berguna untuk menguji alur AI)."),
        code("using IoTCom.Net.Adapters.Dicom;\n\nawait using var pacs = DicomStoreServer.Create(o => o.Port = 11120);\nawait pacs.StartAsync();\n"
             "DicomReceived? got = null;\npacs.ImageReceived += r => got = r;\n"
             "await using var modality = DicomStoreClient.Create(o => o.Port = 11120);\nawait modality.EchoAsync();\n"
             "var study = SyntheticImaging.Generate(SyntheticModality.ChestCt, SyntheticFinding.Pneumothorax);\n"
             "await modality.StoreAsync(study.File);\n"
             "var image = DicomRenderer.Render(got!.File.Dataset, DicomRenderer.Presets[\"CT lung\"]);\n"
             "Console.WriteLine($\"{got.Modality} {image.Width}x{image.Height}, PNG {image.ToPng().Length / 1024} KB — planted: {study.FindingDescription}\");"),
        md("## Going further\nThe Gallery demos *ICU bedside monitors* and *Imaging AI pre-read* add NEWS2 scoring, trend analysis and an LLM "
           "(SBAR notes and vision pre-reads) on top of these building blocks — see `docs/en/guides/medical-ai.md`.\n\n" + CREDIT[0],
           "## Lebih lanjut\nDemo Galeri *Monitor pasien ICU* dan *Pra-baca gambar dengan AI* menambahkan skor NEWS2, analisis tren, dan LLM "
           "(catatan SBAR dan pra-baca vision) di atas blok-blok ini — lihat `docs/id/guides/medical-ai.md`.\n\n" + CREDIT[1]),
    ],
    "99-protocol-chooser": [
        md("# Which protocol for which job?\n\n"
           "| You need to… | Use | IoTCom.Net package |\n|---|---|---|\n"
           "| Read/write a PLC, meter, VFD or I/O module | Modbus TCP/RTU | `Protocols.Modbus` |\n"
           "| Ship telemetry to the cloud or many consumers | MQTT (+ SenML payloads) | `Adapters.Mqtt`, `Serialization.SenML` |\n"
           "| Read position/time from a GPS or marine instruments | NMEA 0183 | `Protocols.Nmea` |\n"
           "| Drive stage or architectural lighting | Art-Net or sACN | `Protocols.Dmx` |\n"
           "| Collect battery sensors kilometres away | LoRaWAN (Semtech UDP gateways) | `Protocols.LoRaWan` |\n"
           "| Read electricity meters / building sub-meters | DLMS/COSEM / M-Bus | `Protocols.Dlms`, `Protocols.MBus` |\n"
           "| Talk to a microcontroller over UART/USB with your own messages | COBS or SLIP + CRC | `Framing` |\n"
           "| Debug a link byte by byte | Traffic tap, `iotcom modbus decode`, Gallery workbench | `Core`, CLI |\n\n"
           "**Rules of thumb.** Polling one device on a LAN → Modbus TCP. Fan-out to many consumers or over the internet → MQTT with TLS. "
           "Sensor payloads crossing vendor boundaries → SenML. Never expose Modbus to the internet: put a gateway in front.\n\n" + CREDIT[0],
           "# Protokol mana untuk tugas apa?\n\n"
           "| Anda perlu… | Gunakan | Paket IoTCom.Net |\n|---|---|---|\n"
           "| Membaca/menulis PLC, meter, VFD, atau modul I/O | Modbus TCP/RTU | `Protocols.Modbus` |\n"
           "| Mengirim telemetri ke cloud atau banyak konsumen | MQTT (+ payload SenML) | `Adapters.Mqtt`, `Serialization.SenML` |\n"
           "| Membaca posisi/waktu dari GPS atau instrumen kapal | NMEA 0183 | `Protocols.Nmea` |\n"
           "| Mengendalikan lampu panggung atau arsitektural | Art-Net atau sACN | `Protocols.Dmx` |\n"
           "| Mengumpulkan sensor baterai yang jauhnya berkilo-kilometer | LoRaWAN (gateway Semtech UDP) | `Protocols.LoRaWan` |\n"
           "| Membaca meter listrik / sub-meter gedung | DLMS/COSEM / M-Bus | `Protocols.Dlms`, `Protocols.MBus` |\n"
           "| Berkomunikasi dengan mikrokontroler lewat UART/USB dengan pesan sendiri | COBS atau SLIP + CRC | `Framing` |\n"
           "| Debug link byte per byte | Traffic tap, `iotcom modbus decode`, workbench Galeri | `Core`, CLI |\n\n"
           "**Aturan praktis.** Membaca satu perangkat di LAN → Modbus TCP. Distribusi ke banyak konsumen atau lewat internet → MQTT dengan TLS. "
           "Payload sensor lintas vendor → SenML. Jangan pernah membuka Modbus ke internet: pasang gateway di depannya.\n\n" + CREDIT[1]),
    ],
}


def cell(kind, text):
    lines = text.split("\n")
    source = [l + "\n" for l in lines[:-1]] + [lines[-1]]
    if kind == "md":
        return {"cell_type": "markdown", "metadata": {}, "source": source}
    return {
        "cell_type": "code", "execution_count": None, "outputs": [], "source": source,
        "metadata": {"dotnet_interactive": {"language": "csharp"}, "polyglot_notebook": {"kernelName": "csharp"}},
    }


def notebook(cells):
    return {
        "cells": cells,
        "metadata": {
            "kernelspec": {"display_name": ".NET (C#)", "language": "C#", "name": ".net-csharp"},
            "language_info": {"name": "polyglot-notebook"},
            "polyglot_notebook": {"kernelInfo": {"defaultKernelName": "csharp", "items": [{"aliases": [], "name": "csharp"}]}},
        },
        "nbformat": 4,
        "nbformat_minor": 5,
    }


for name, spec in NOTEBOOKS.items():
    for lang in ("en", "id"):
        cells = [cell("md", s[1] if lang == "en" else s[2]) if s[0] == "md" else cell("code", s[1]) for s in spec]
        path = os.path.join(ROOT, f"{name}.{lang}.ipynb")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            json.dump(notebook(cells), f, indent=1, ensure_ascii=False)
            f.write("\n")
        print("wrote", os.path.relpath(path, ROOT))
