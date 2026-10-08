---
title: Lapisan native — Rust dan C ABI
translation-status: synced
---

# Lapisan native — Rust dan C ABI

## Workspace

```
rust/
├─ Cargo.toml                       workspace, lint, profil release (thin LTO, panic=unwind)
├─ include/                         header C hasil generate (iotcom_common.h + satu per pustaka)
├─ tools/iotcom-bindgen/            membuat ulang header (cbindgen) dan referensi C# (csbindgen)
└─ crates/
   ├─ iotcom-core/                  trait Machine, Instant, Error, helper CRC (#![forbid(unsafe_code)])
   ├─ iotcom-ffi-support/           ffi_guard, kode status, last error per thread, ABI_VERSION
   ├─ iotcom-modbus/                codec frame + MasterMachine (#![forbid(unsafe_code)])
   ├─ native/iotcom-modbus-native/  cdylib "iotcom_modbus" — C ABI
   ├─ native/iotcom-isotp-native/   cdylib "iotcom_isotp"
   ├─ native/iotcom-ble-native/     cdylib "iotcom_ble" (btleplug)
   └─ native/iotcom-usb-native/     cdylib "iotcom_usb" (nusb, hidapi)
```

```bash
cd rust
cargo test --workspace
cargo clippy --workspace --all-targets -- -D warnings
cargo build --release -p iotcom-modbus-native          # → target/release/iotcom_modbus.{dll,so,dylib}
```

## Kontrak FFI

1. Setiap ekspor adalah `extern "C"` dan dibungkus `ffi_guard`, yang menangkap panic (`catch_unwind`) dan mengubahnya
   menjadi `IOTCOM_ERR_PANIC`. Panic Rust tidak pernah merambat ke .NET.
2. Objek adalah handle buram dari `*_new`, dilepas tepat sekali oleh `*_free`. Di sisi .NET disimpan dalam
   `SafeHandle`.
3. Hasil berupa kode status `i32` (`IOTCOM_OK = 0`, error negatif). Pesannya disimpan per thread:
   `iotcom_last_error(buf, len)`.
4. Buffer dimiliki pemanggil. Pointer data event tetap valid sampai panggilan berikutnya pada handle yang sama.
5. `iotcom_abi_version()` diperiksa sebelum apa pun; ketidakcocokan menghasilkan `PlatformNotSupportedException`
   dengan pesan yang jelas.

| Kode | Arti |
|---|---|
| 0 | OK |
| -1 | pointer null |
| -2 | argumen tidak valid |
| -3 | panic tertangkap |
| -4 | buffer terlalu kecil (ukuran yang dibutuhkan dikembalikan) |
| -5 | error protokol |
| -6 | sibuk (antrean penuh) |

## Ekspor Modbus

```c
uint32_t iotcom_abi_version(void);
int32_t  iotcom_last_error(uint8_t* buf, size_t len);
int32_t  iotcom_modbus_master_new(const ModbusMasterCfg* cfg, ModbusMaster** out);
void     iotcom_modbus_master_free(ModbusMaster* m);
int32_t  iotcom_modbus_master_submit(ModbusMaster* m, uint64_t now_us, uint8_t unit, const uint8_t* pdu, size_t len, uint32_t* out_id);
int32_t  iotcom_modbus_master_handle_input(ModbusMaster* m, uint64_t now_us, const uint8_t* data, size_t len);
int32_t  iotcom_modbus_master_handle_timeout(ModbusMaster* m, uint64_t now_us);
int32_t  iotcom_modbus_master_poll_transmit(ModbusMaster* m, uint8_t* buf, size_t cap, size_t* out_required);
int32_t  iotcom_modbus_master_poll_event(ModbusMaster* m, ModbusEvent* out);
int32_t  iotcom_modbus_master_poll_timeout(ModbusMaster* m, uint64_t* out_deadline_us);
int32_t  iotcom_modbus_master_pending(ModbusMaster* m);
```

## Sisi .NET

`IoTCom.Net.Native.Modbus` mengikat fungsi-fungsi ini dengan `[LibraryImport]` hasil source generator (ramah AOT).
`NativeModbusMaster` adalah pembungkus sans-I/O tipis; `NativeModbusClient` adalah driver loop-nya:

```
PipeReader ──► HandleInput ──► event ──► TaskCompletionSource per request
PipeWriter ◄── PollTransmit ◄── Submit(unit, pdu)
timer      ──► PollTimeout / HandleTimeout (timeout dan serialisasi RTU ada di Rust)
```

Panggilan pada satu handle diserialisasi dengan lock; panggilan FFI dibatch (semua frame dan event tertunda dikuras
per panggilan). Machine bekerja dengan timestamp mikrodetik dari jam monotonik yang disuplai .NET.

## Binding dan header hasil generate

`cargo run -p iotcom-bindgen` (di `rust/`) menulis `rust/include/iotcom_*.h` dengan cbindgen, untuk pemanggil C dan C++,
serta `tests/IoTCom.Net.Tests/Interop/Generated/*.g.cs` dengan csbindgen. Paket tetap memakai binding `LibraryImport`
tulisan tangan, yang memakai SafeHandle dan parameter `out`. `BindingDriftTests` membandingkannya dengan deklarasi
hasil generate: setiap ekspor yang di-bind harus ada dengan jumlah parameter yang sama dan ukuran ABI yang sama per
parameter, dan struct `repr(C)` harus punya ukuran serta offset field yang sama. CI membuat ulang kedua keluaran dan
gagal bila berbeda dari file yang di-commit, serta mengompilasi setiap header dengan gcc dan g++. Ubah signature Rust,
jalankan alatnya, lalu perbaiki binding C# yang ditunjuk oleh pengujian.

## Pemuatan

`NativeLibraryLoader` mendaftarkan `DllImportResolver` yang mencari, berurutan: `IOTCOM_NATIVE_PATH`, direktori
aplikasi, `runtimes/{rid}/native/`, direktori assembly, dan `rust/target/{release,debug}` saat berjalan dari clone.

## Cross-compile

`build/build-native.sh` / `build/build-native.ps1` membangun setiap RID ke `artifacts/native/{rid}/`, yang diambil oleh
pack NuGet. CI memakai `cargo-zigbuild` (baseline glibc lama untuk kompatibilitas Linux yang luas), `cross` untuk musl
dan ARM, MSVC untuk Windows, dan runner macOS untuk target Apple.

## Pengujian

Uji unit Rust mencakup codec, master machine (pipelining, input terpotong, timeout, back-pressure) dan C ABI (round
trip, pointer null). Vector bersama di `/conformance` berjalan di kedua sisi, dan uji .NET memastikan mesin Rust dan
framing managed menghasilkan frame yang identik byte per byte.
