---
title: Menambah protokol
translation-status: synced
---

# Menambah protokol

## 1. Tentukan tier

Periksa [tier kurasi](../concepts/architecture.md#tier-kurasi). Bila .NET atau library yang sehat sudah mencakup
protokolnya, tulis adapter (T1). Implementasikan hanya celah nyata (T2). Lalu terapkan aturan Rust-atau-C#.

## 2. Tulis codec lebih dulu

Fungsi murni tanpa I/O: membangun/mengurai frame, menghitung checksum, mendeskripsikan field (`FrameField`) untuk frame
lane. Tambahkan contoh spesifikasi ke `/conformance/<protokol>.json` lewat `conformance/generate.py` agar kedua bahasa
menguji byte yang sama.

## 3. Tulis endpoint

Turunkan dari `EndpointBase`. Terima transport lewat builder yang mengimplementasikan `ITransportBuilder<T>` (client)
atau `IListenerBuilder<T>` (server) sehingga `UseTcp`, `UseSerial`, dan `UseInMemory` langsung berfungsi. Panggil
`Tap(...)` untuk setiap frame, gunakan `SetState`, hormati `CancellationToken`, dan lempar tipe exception IoTCom.

Untuk Rust (T2-R): implementasikan `iotcom_core::Machine` di crate `#![forbid(unsafe_code)]`, tambahkan `cdylib` di
`crates/native/` yang memakai `export_common!()` dan `ffi_guard`, serta paket .NET dengan binding `LibraryImport`,
`SafeHandle`, dan client driver.

## 4. Simulator, sampel, notebook, demo

Setiap protokol membawa simulator dan sampel console (≤ 150 baris, `--simulate`), sepasang notebook yang dihasilkan dari
`build/generate_notebooks.py`, dan demo Galeri bila relevan.

## 5. Dokumentasi dalam dua bahasa

Tambahkan `docs/en/protocols/<protokol>.md` memakai template halaman protokol (ringkasan → kapan dipakai → peran →
transport → instalasi → mulai cepat → konfigurasi → contoh → simulator → pengujian → keamanan → keterbatasan →
pelajari lebih lanjut) beserta kembarannya di `docs/id/`. CI gagal bila sebuah halaman tidak punya terjemahan
(`python build/check_docs_parity.py`).

## Definition of done

- API publik terdokumentasi (XML doc + halaman EN/ID)
- Uji unit, conformance, dan (untuk Rust) uji bergaya fuzz
- Simulator, sampel console, sepasang notebook, demo Galeri bila relevan
- Entri di matriks dukungan dan di `Progress.md`
