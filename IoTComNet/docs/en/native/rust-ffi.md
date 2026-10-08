---
title: Native layer — Rust and the C ABI
translation-status: synced
---

# Native layer — Rust and the C ABI

## Workspace

```
rust/
├─ Cargo.toml                       workspace, lints, release profile (thin LTO, panic=unwind)
├─ include/                         generated C headers (iotcom_common.h + one per library)
├─ tools/iotcom-bindgen/            regenerates the headers (cbindgen) and the C# reference (csbindgen)
└─ crates/
   ├─ iotcom-core/                  Machine trait, Instant, Error, CRC helpers (#![forbid(unsafe_code)])
   ├─ iotcom-ffi-support/           ffi_guard, status codes, thread-local last error, ABI_VERSION
   ├─ iotcom-modbus/                frame codecs + MasterMachine (#![forbid(unsafe_code)])
   ├─ native/iotcom-modbus-native/  cdylib "iotcom_modbus" — the C ABI
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

## The FFI contract

1. Every export is `extern "C"` and wrapped in `ffi_guard`, which catches panics (`catch_unwind`) and turns them into
   `IOTCOM_ERR_PANIC`. A Rust panic never unwinds into .NET.
2. Objects are opaque handles from `*_new`, released exactly once by `*_free`. On the .NET side they live in a
   `SafeHandle`.
3. Results are `i32` status codes (`IOTCOM_OK = 0`, negative errors). The message is kept per thread:
   `iotcom_last_error(buf, len)`.
4. Buffers belong to the caller. Event data pointers stay valid until the next call on the same handle.
5. `iotcom_abi_version()` is checked before anything else; a mismatch raises `PlatformNotSupportedException` with a
   clear message.

| Code | Meaning |
|---|---|
| 0 | OK |
| -1 | null pointer |
| -2 | invalid argument |
| -3 | panic caught |
| -4 | buffer too small (required size returned) |
| -5 | protocol error |
| -6 | busy (queue full) |

## Modbus exports

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

## The .NET side

`IoTCom.Net.Native.Modbus` binds these with source-generated `[LibraryImport]` (AOT friendly). `NativeModbusMaster`
is the thin sans-I/O wrapper; `NativeModbusClient` is the driver loop:

```
PipeReader ──► HandleInput ──► events ──► TaskCompletionSource per request
PipeWriter ◄── PollTransmit ◄── Submit(unit, pdu)
timer      ──► PollTimeout / HandleTimeout (timeouts and RTU serialisation live in Rust)
```

Calls on one handle are serialised by a lock; FFI calls are batched (all pending frames and events are drained per
call). The machine works with microsecond timestamps from a monotonic clock supplied by .NET.

## Generated bindings and headers

`cargo run -p iotcom-bindgen` (in `rust/`) writes `rust/include/iotcom_*.h` with cbindgen, for C and C++ callers, and
`tests/IoTCom.Net.Tests/Interop/Generated/*.g.cs` with csbindgen. The packages keep their hand-written `LibraryImport`
bindings, which use SafeHandles and `out` parameters. `BindingDriftTests` checks them against the generated
declarations: every bound export must exist with the same number of parameters and the same ABI size per parameter,
and the `repr(C)` structs must have the same size and field offsets. CI regenerates both outputs and fails when they
differ from the committed files, and it compiles every header with gcc and g++. Change a Rust signature, run the
tool, then fix the C# binding the test points at.

## Loading

`NativeLibraryLoader` registers a `DllImportResolver` that probes, in order: `IOTCOM_NATIVE_PATH`, the app directory,
`runtimes/{rid}/native/`, the assembly directory, and `rust/target/{release,debug}` when running from a clone.

## Cross-compiling

`build/build-native.sh` / `build/build-native.ps1` build every RID into `artifacts/native/{rid}/`, which the NuGet
pack picks up. CI uses `cargo-zigbuild` (old glibc baseline for broad Linux compatibility), `cross` for musl and ARM,
MSVC for Windows and macOS runners for Apple targets.

## Testing

Rust unit tests cover codecs, the master machine (pipelining, split input, timeouts, back-pressure) and the C ABI
(round trip, null pointers). `BindingDriftTests` keeps the C# declarations in line with the Rust exports. Shared vectors in `/conformance` run on both sides, and a .NET test checks that the Rust
engine and the managed framing emit byte-identical frames.
