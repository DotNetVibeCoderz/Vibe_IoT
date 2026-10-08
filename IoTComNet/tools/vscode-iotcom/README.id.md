# IoTCom.Net Tools untuk VS Code

Urai frame industri, otomotif, dan IoT per field, pantau lalu lintas langsung, dan simpan untuk Wireshark, tanpa
meninggalkan editor. Semuanya berjalan di CLI `iotcom` lewat JSON-RPC, sehingga ekstensi dan pustaka mengurai dengan
cara yang persis sama.

Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil · [English](README.md)

## Fitur

- **Penampil frame:** tempel frame atau pilih byte di file apa pun, lalu jalankan **IoTCom: Urai byte yang dipilih**.
  Frame tampil sebagai *frame lane* dengan setiap field diberi warna, nama, dan nilai terurai. Arahkan kursor ke field
  di tabel untuk menyorot byte-nya. Decoder: Modbus TCP/RTU/ASCII, CAN (notasi candump), UDS/OBD-II, CoAP, dan
  MAVLink.
- **Pemantau lalu lintas:** pantau sumber langsung sebagai daftar frame terurai yang terus bergulir. Saring daftarnya,
  klik frame untuk membuka lane-nya, lalu **Simpan .pcapng** untuk membuka capture di Wireshark. Sumber:
  - simulator bawaan: PLC Modbus, ECU CAN, rumah kaca CoAP, dan drone MAVLink;
  - antarmuka CAN (`socketcan:can0`, `slcan:COM5`);
  - port UDP MAVLink.
- **Tampilan Protokol:** setiap protokol menautkan paket NuGet-nya (klik untuk menyalin `dotnet add package …`),
  dokumentasinya dalam English atau Bahasa Indonesia, notebook, sampel, dan decoder-nya.
- **Tampilan Perangkat:** port serial, antarmuka SocketCAN, dan simulator. Klik salah satunya untuk mulai
  memantaunya.
- **Snippet:** ketik `iotcom-` di file C# untuk snippet Modbus, CAN, UDS, CoAP, MAVLink, MQTT, dan capture pcapng.
- **English dan Bahasa Indonesia:** ekstensi mengikuti bahasa tampilan VS Code.

## Kebutuhan

CLI `iotcom` (.NET 10):

```bash
dotnet tool install -g IoTCom.Net.Cli --prerelease
```

Di clone repositori IoTCom.Net, ekstensi memakai CLI yang di-build di workspace (`dotnet build tools/iotcom-cli`).
Jika tidak, atur **iotcom.cliPath**, misalnya `dotnet /path/to/iotcom.dll`.

## Pengaturan

| Pengaturan | Bawaan | |
|---|---|---|
| `iotcom.cliPath` | (kosong) | perintah yang menjalankan CLI; kosong berarti build workspace, lalu global tool |
| `iotcom.docsLanguage` | `auto` | `en`, `id`, atau ikuti bahasa tampilan VS Code |

## Pengembangan

```bash
npm install
npm test            # kompilasi, lalu uji klien RPC ujung ke ujung terhadap CLI sungguhan
npm run package     # membangun iotcom-net-tools.vsix
```

Tekan F5 di VS Code dengan folder ini terbuka untuk menjalankan Extension Development Host.
