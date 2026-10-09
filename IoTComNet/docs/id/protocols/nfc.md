---
title: NFC / NDEF
translation-status: synced
---

# NFC / NDEF

**Ringkasan.** Tag NFC berupa stiker, kartu, dan gantungan kunci yang diberi daya oleh medan dari ponsel dan pembaca,
lalu dibaca dalam jarak beberapa sentimeter. Tag menyimpan pesan NDEF: URI ke manual, teks dengan nomor aset,
kredensial Wi-Fi, tautan aplikasi. Di PC, pembaca contactless membuka akses ke tag lewat PC/SC.
`IoTCom.Net.Protocols.Nfc` menyediakan:

- **Codec NDEF** (`NdefMessage`, `NdefRecord`):
  - Text (UTF-8 dan UTF-16), URI dengan kode prefiks, Smart Poster, MIME, absolute URI, dan tipe eksternal
  - Android Application Record dan kredensial Wi-Fi (WSC, `WifiCredential`; kuncinya tidak pernah ditampilkan oleh
    `ToString`)
  - record pendek dan panjang, id, serta record bersegmen yang digabung saat diurai; frame lane
    (`NdefMessage.Describe`)
- **Tag Type 2** (`Type2Tag`, `Type2TagClient`) — NTAG213/215/216 dan MIFARE Ultralight:
  - halaman UID dengan byte pemeriksa, capability container, penguraian TLV (NULL, lock dan memory control, NDEF,
    terminator), panjang TLV 3 byte, dan peta halaman untuk setiap byte (`Type2Tag.Map`)
  - pembacaan lewat perintah storage-card PC/SC GET DATA, READ BINARY, dan UPDATE BINARY
  - penulisan NDEF hanya bila diizinkan, hanya halaman yang berubah, dikosongkan lalu diisi sehingga tag yang ditarik
    di tengah penulisan tidak pernah berisi setengah pesan, dan tidak pernah halaman 0–3; tag yang capability
    container-nya menyatakan hanya-baca ditolak
- **Pembaca**:
  - `PcscNfcReader` di winscard (Windows), pcsc-lite (Linux), dan framework PCSC (macOS); `Open()` mengutamakan
    pembaca contactless (PICC, ACR122, Omnikey 5x22…) dibanding pembaca kontak dan smart card virtual
  - `VirtualNfcReader` dengan `VirtualType2Tag`, yang menjawab APDU yang sama, untuk pengujian, notebook, dan Gallery

Sesuai desain, NFC hanya ditulis dalam C# terkelola: PC/SC adalah API sistem operasi, dan NDEF adalah codec kecil yang
diperiksa dengan `/conformance/ndef.json`, dihasilkan oleh referensi Python independen.

## Kapan dipakai

- Tag aset dan perawatan: membuka manual, mencatat kunjungan servis, memeriksa nomor seri.
- Commissioning: membagikan kredensial Wi-Fi atau konfigurasi dengan sekali tempel.
- Akses dan identitas: membaca UID kartu dan gantungan kunci di gerbang atau kios.
- Menyiapkan banyak tag sekaligus dengan URI atau teks dari PC.

## Instalasi

```bash
dotnet add package IoTCom.Net.Protocols.Nfc --prerelease    # juga bagian dari meta-package IoTCom.Net
```

Di Linux pasang `pcscd` dan `libpcsclite1` (serta driver pembaca, misalnya `libacsccid1`); Windows dan macOS sudah
menyertakan PC/SC.

## Mulai cepat

```csharp
using IoTCom.Net.Protocols.Nfc;

var reader = PcscNfcReader.Open();                       // atau Open("ACR122")
await using var card = await reader.WaitForTagAsync();
var tag = new Type2TagClient(card);
Console.WriteLine(Convert.ToHexString(await tag.GetUidAsync()));
foreach (var record in (await tag.ReadNdefAsync())?.Records ?? []) Console.WriteLine(record);
```

Menulis:

```csharp
var writer = new Type2TagClient(card, new NfcTagOptions().AllowWrites());
await writer.WriteNdefAsync(new NdefMessage([NdefRecord.Uri("https://example.com/asset/42"), NdefRecord.Text("Asset 42")]));
```

## Keamanan

Penulisan mengganti isi tag, jadi penulisan mati sampai `NfcTagOptions.AllowWrites()`, dan `iotcom nfc write`
memerlukan `--allow-write` serta konfirmasi. Pustaka ini tidak pernah menulis halaman UID, kunci, atau capability
container dan tidak pernah mengunci tag: penguncian bersifat permanen, jadi lakukan dengan alat dari vendor tag bila
memang dimaksudkan. Kredensial Wi-Fi pada tag bisa dibaca siapa pun yang menempelkannya; gunakan jaringan tamu atau
jaringan commissioning.

## Alat

```bash
iotcom nfc readers                                  # pembaca PC/SC
iotcom nfc read --sim --dump                        # NTAG213 simulasi, NDEF, dan setiap halaman
iotcom nfc read --reader ACR122
iotcom nfc write --uri https://example.com/asset/42 --text "Asset 42" --reader ACR122 --allow-write
iotcom nfc decode "D101085502 6E78702E636F6D"
```

Ekstensi VS Code mengurai NDEF (`ndef`). Gallery: *Tag aset NFC*.

## Pengujian

Uji codec mencakup:
- contoh URI NFC Forum (https://www.nxp.com), kode prefiks, dan record Text dalam UTF-8 serta UTF-16 dengan byte-order
  mark;
- pesan dengan Smart Poster, teks, kredensial Wi-Fi, tipe eksternal, Android app record, dan record panjang 300 byte;
- record bersegmen, pesan kosong, dan pesan rusak (tanpa MB atau ME, TNF 7, segmen nyasar, terpotong).

Uji tag mencakup:
- byte pemeriksa UID dan capability container untuk ketiga ukuran NTAG;
- penguraian TLV dengan TLV lock-control dan NULL, panjang 3 byte, dan pesan yang tidak muat;
- pembacaan, penulisan yang ditolak dan diizinkan, pesan yang tidak berubah tidak menulis apa pun, halaman yang
  dilindungi, dan peta halaman;
- tag hanya-baca, NTAG216 kosong yang diisi 700 byte, tag yang dilepas, dan menunggu tag;
- panggilan PC/SC sungguhan yang mendaftar pembaca atau menjelaskan mengapa tidak bisa.

Ke-30 vektor bersama dijalankan di suite C#.

## Keterbatasan

Hanya tag NFC Forum Type 2 yang dibaca dan ditulis. MIFARE Classic (dengan kuncinya), Type 4 (DESFire, aplikasi NDEF
ISO 14443-4), dan Type 5 (ISO 15693) belum didukung. Pembaca yang tidak mengimplementasikan perintah storage-card
PC/SC part 3 juga belum didukung: sebagian pembaca butuh perintah escape vendor. Proteksi kata sandi NTAG, originality
signature, dan counter belum dibuka.

## Pelajari lebih lanjut

Notebook `notebooks/devices/20-nfc.id.ipynb` · sampel `samples/console/NfcTagReader` · [USB dan HID](usb.md) ·
[Bluetooth LE](ble.md)
