---
title: Platforms and permissions
translation-status: synced
---

# Platforms and permissions

## Support matrix

| Platform | RID | Managed packages | Rust engine (`Native.Modbus`) |
|---|---|---|---|
| Windows x64 / arm64 | `win-x64`, `win-arm64` | ✔ | ✔ x64 built locally; arm64 built in CI |
| Linux x64 / arm64 (glibc) | `linux-x64`, `linux-arm64` | ✔ | built in CI (`build/build-native.sh`) |
| Raspberry Pi (32-bit) | `linux-arm` | ✔ | built in CI |
| Alpine / containers (musl) | `linux-musl-x64`, `linux-musl-arm64` | ✔ | built in CI |
| macOS Intel / Apple Silicon | `osx-x64`, `osx-arm64` | ✔ | built on macOS runners |

Every managed package is pure .NET and works wherever .NET 10 runs. The native engine is optional: when its library
is missing, `NativeModbusMaster.IsSupported` is `false` and the managed `ModbusClient` covers the same API.

The loader probes `runtimes/{rid}/native/`, the app directory, and — for development — `rust/target/release`.
Set `IOTCOM_NATIVE_PATH` to point at a custom folder.

## Permissions

| Need | Linux | Windows | macOS |
|---|---|---|---|
| Serial ports | add the user to `dialout` (`sudo usermod -aG dialout $USER`, then log in again) | none | none |
| TCP port < 1024 (e.g. Modbus 502) | run as root, or `setcap 'cap_net_bind_service=+ep'` on the binary, or use 1502 | admin rights or a URL ACL | root |
| UDP broadcast (Art-Net) | none; allow UDP 6454 in the firewall | allow in Windows Defender Firewall | allow in the firewall |
| Multicast (sACN) | the interface must allow multicast | allow UDP 5568 | allow UDP 5568 |
| Containers + serial | `--device=/dev/ttyUSB0` and the `dialout` group | — | — |

## Time and clocks

Protocol timeouts use monotonic clocks, so they are not affected by NTP adjustments. Timestamps in captured frames
and SenML records are UTC.
