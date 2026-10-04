---
title: Platform dan izin
translation-status: synced
---

# Platform dan izin

## Matriks dukungan

| Platform | RID | Paket managed | Mesin Rust (`Native.Modbus`) |
|---|---|---|---|
| Windows x64 / arm64 | `win-x64`, `win-arm64` | ✔ | ✔ x64 dibuild lokal; arm64 dibuild di CI |
| Linux x64 / arm64 (glibc) | `linux-x64`, `linux-arm64` | ✔ | dibuild di CI (`build/build-native.sh`) |
| Raspberry Pi (32-bit) | `linux-arm` | ✔ | dibuild di CI |
| Alpine / container (musl) | `linux-musl-x64`, `linux-musl-arm64` | ✔ | dibuild di CI |
| macOS Intel / Apple Silicon | `osx-x64`, `osx-arm64` | ✔ | dibuild di runner macOS |

Setiap paket managed adalah .NET murni dan berjalan di mana pun .NET 10 berjalan. Mesin native bersifat opsional: bila
library-nya tidak ada, `NativeModbusMaster.IsSupported` bernilai `false` dan `ModbusClient` managed menyediakan API
yang sama.

Loader mencari di `runtimes/{rid}/native/`, direktori aplikasi, dan — untuk pengembangan — `rust/target/release`.
Atur `IOTCOM_NATIVE_PATH` untuk menunjuk folder lain.

## Izin

| Kebutuhan | Linux | Windows | macOS |
|---|---|---|---|
| Port serial | tambahkan user ke grup `dialout` (`sudo usermod -aG dialout $USER`, lalu login ulang) | tidak perlu | tidak perlu |
| Port TCP < 1024 (mis. Modbus 502) | jalankan sebagai root, atau `setcap 'cap_net_bind_service=+ep'` pada binary, atau gunakan 1502 | hak admin atau URL ACL | root |
| Broadcast UDP (Art-Net) | izinkan UDP 6454 di firewall | izinkan di Windows Defender Firewall | izinkan di firewall |
| Multicast (sACN) | interface harus mengizinkan multicast | izinkan UDP 5568 | izinkan UDP 5568 |
| Container + serial | `--device=/dev/ttyUSB0` dan grup `dialout` | — | — |

## Waktu dan jam

Timeout protokol memakai jam monotonik, sehingga tidak terpengaruh penyesuaian NTP. Timestamp pada frame yang
ditangkap dan record SenML memakai UTC.
