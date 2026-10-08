---
title: Perintah AT
translation-status: synced
---

# Perintah AT (modul seluler dan GNSS)

**Ringkasan.** Modul NB-IoT, LTE-M, LTE Cat-1, dan GNSS dikendalikan lewat port serial dengan perintah AT
(3GPP TS 27.007 dan 27.005, ITU-T V.250). Perintah diakhiri `OK`, `ERROR`, `+CME ERROR`, atau `+CMS ERROR`; modul juga
mengirim **unsolicited result code** (URC) kapan saja, bahkan di tengah respons. IoTCom.Net menyediakan:

- `AtParser`, parser sans-I/O yang membuang echo, mengumpulkan baris informasi, mengenali setiap hasil akhir dan
  prompt SMS, serta memisahkan URC;
- `AtModem`, client dengan timeout per perintah, aliran URC, SMS mode teks, helper identitas, sinyal, dan registrasi,
  serta mode read-only;
- `AtModemSimulator`, modul LTE-M dengan PIN SIM, registrasi jaringan (URC +CEREG), sinyal yang berubah-ubah, dan SMS.

## Kapan digunakan

- Menyalakan modul seluler di papan atau dongle USB, dan membuat skrip konfigurasinya.
- Memantau sinyal dan registrasi di lapangan.
- Mengirim dan menerima SMS untuk alarm dan perintah jarak jauh.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.AtCommand --prerelease     # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat

```csharp
using IoTCom.Net.Protocols.AtCommand;
using IoTCom.Net.Transport.Serial;

await using var modem = AtModem.Create(o => o.UseSerial("COM7", 115200));
await modem.ConnectAsync();                                 // ATE0, AT+CMEE=2
var info = await modem.GetInfoAsync();
Console.WriteLine($"{info.Model} {info.Registration} {info.Operator} {info.AccessTechnology} {info.Signal}");

modem.UrcReceived += (_, urc) => Console.WriteLine($"URC {urc.Line}");   // +CEREG: 1 · +CMTI: "ME",3 · RING
var r = await modem.SendAsync("AT+QCFG=\"nwscanmode\"");               // perintah vendor diteruskan apa adanya
```

`SendAsync` mengembalikan `AtResponse` (baris, hasil, kode atau teks error) dan tidak pernah melempar exception
saat `ERROR`; `SendCheckedAsync` melempar `DeviceException` dengan error yang sudah diurai. `Values("+CSQ")` memecah
sebuah baris menjadi nilai dengan memperhatikan tanda kutip.

## Keselamatan

Dengan `ReadOnly = true`, perintah yang mengubah modul atau memakan biaya (AT+CFUN, panggilan, SMS, kunci SIM,
pemilihan operator, penyimpanan pengaturan) melempar `ReadOnlyModeException`. CLI mewajibkan `--allow-write` untuk
perintah tersebut.

## Alat

```bash
iotcom at info --sim                        # modul simulasi
iotcom at send --serial COM7 ATI AT+CSQ "AT+COPS?"
iotcom at sms --serial COM7 +6281234567890 "pompa nyala" --allow-write
iotcom at simulate --port 2000              # modul di TCP untuk alat lain
```

## Pengujian

Pengujian parser mencakup echo, respons yang terpecah di beberapa pembacaan, URC di tengah respons, prefix perintah
itu sendiri yang tidak dianggap URC, setiap kode hasil akhir, dan prompt. Pengujian endpoint berjalan terhadap
simulator: identitas, URC registrasi, sinyal, SMS keluar dan masuk (+CMTI, AT+CMGR), PIN SIM, dan mode read-only.

## Keterbatasan

Belum tersedia: SMS mode PDU, multiplexing (CMUX), mode data (PPP), dan stack socket vendor (+QIOPEN, +USOCR) selain
meneruskan perintahnya.

## Pelajari lebih lanjut

Notebook `notebooks/devices/11-at-astm.id.ipynb` · `iotcom at --help`
