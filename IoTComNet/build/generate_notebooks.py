#!/usr/bin/env python3
"""Generates the EN/ID Polyglot (.NET Interactive) notebooks from one spec.

Code cells are shared verbatim between languages; only the narrative differs — the docs parity rule
(every EN page has an ID twin) applies to notebooks too.

Run: python build/generate_notebooks.py
"""
import json
import os

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "notebooks")
VERSION = "0.6.0-preview.1"
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
           "| `messaging/04-mqtt-senml` | MQTT pub/sub with SenML payloads |\n| `99-protocol-chooser` | Which protocol for which job |\n\n" + CREDIT[0],
           "## Selanjutnya\n\n| Notebook | Topik |\n|---|---|\n| `industrial/01-modbus` | Master, slave, simulator Modbus, mesin Rust |\n"
           "| `transport/02-framing-crc` | Katalog CRC, SLIP, COBS, HDLC |\n| `navigation/03-nmea` | GPS/GNSS dengan NMEA 0183 |\n"
           "| `messaging/04-mqtt-senml` | Pub/sub MQTT dengan payload SenML |\n| `99-protocol-chooser` | Protokol mana untuk tugas apa |\n\n" + CREDIT[1]),
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
        md("## Going further\nThe Gallery demo *Vehicle diagnostics* shows the same stack with a live dashboard; the CLI has "
           "`iotcom obd live --can sim`. See `docs/en/protocols/uds.md`.\n\n" + CREDIT[0],
           "## Lebih lanjut\nDemo Galeri *Diagnostik kendaraan* menampilkan tumpukan yang sama dengan dasbor langsung; CLI punya "
           "`iotcom obd live --can sim`. Lihat `docs/id/protocols/uds.md`.\n\n" + CREDIT[1]),
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
