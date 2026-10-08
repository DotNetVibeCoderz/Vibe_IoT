---
title: ASTM E1394
translation-status: synced
---

# ASTM E1394 / LIS2-A2 (analyzer lab)

**Ringkasan.** Analyzer laboratorium mengirim hasil ke sistem informasi laboratorium (LIS) dengan record ASTM E1394 /
CLSI LIS2-A2 (header H, pasien P, order O, hasil R, komentar C, query Q, terminator L) lewat link ASTM E1381 / LIS1-A:
`ENQ` untuk memulai, satu record berbingkai setiap kali (`STX FN teks ETX C1 C2 CR LF`), masing-masing di-ACK dengan
`ACK` atau ditolak dengan `NAK`, dan `EOT` untuk mengakhiri. IoTCom.Net menyediakan:

- `AstmMessage` dan `AstmMessageBuilder` (delimiter dari header, hasil bertipe);
- `AstmLink` (frame, checksum, pemecahan record yang lebih dari 240 karakter dengan ETB);
- `AstmReceiver` (sisi LIS) dan `AstmSender` (sisi analyzer, pengiriman ulang setelah NAK);
- `AnalyzerSimulator`, analyzer kimia sintetis, dan `AstmToHl7.ToOru`, jembatan ke HL7 v2.5.1 ORU^R01.

Semua di sini sintetis dan **bukan perangkat medis**.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Astm --prerelease     # juga bagian dari meta-package IoTCom.Net
```

## Mulai cepat: menerima hasil

```csharp
using IoTCom.Net.Protocols.Astm;

await using var lis = AstmReceiver.Create(o => o.UseTcp(IPAddress.Any, 5000));   // atau o.ServeSerial("COM3", 9600)
lis.MessageReceived += (_, m) =>
{
    foreach (var r in m.Results) Console.WriteLine($"{m.SpecimenId} {r.TestCode} {r.Value} {r.Units} {r.Flag}");
};
await lis.StartAsync();
```

## Jembatan ke HL7

```csharp
await using var his = Hl7MllpClient.Create(o => o.UseTcp("10.0.0.20", 2575));
lis.MessageReceived += async (_, m) => await his.SendAsync(AstmToHl7.ToOru(m));
```

Sampel `AstmAnalyzerBridge` melakukan ini dari ujung ke ujung, termasuk frame rusak yang dikirim ulang.

## Alat

```bash
iotcom astm listen --port 5000 --hl7     # tampilkan hasil dan ORU^R01-nya
iotcom astm send --port 5000 -n 3        # hasil kimia sintetis
dotnet run --project samples/console/AstmAnalyzerBridge
```

## Pengujian

Record dan hasil (dengan delimiter dari header, termasuk delimiter kustom), checksum frame dan pemecahan record,
transfer dengan frame rusak (satu NAK, satu pengiriman ulang, pesan identik di LIS), dan jembatan HL7 (diurai kembali
dengan codec HL7).

## Keterbatasan

Belum tersedia: query (Q) yang dijawab oleh LIS (mode host query), resolusi perebutan jalur ketika kedua sisi
mengirim ENQ bersamaan (penerima langsung menerima), dan ekstensi record khusus vendor (record M, S disimpan apa
adanya).

## Pelajari lebih lanjut

Notebook `notebooks/devices/11-at-astm.id.ipynb` · [HL7 v2](hl7.md) · `iotcom astm --help`
