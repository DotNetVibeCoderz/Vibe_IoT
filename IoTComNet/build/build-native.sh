#!/usr/bin/env bash
# Builds the Rust native libraries for one or more Rust targets into artifacts/native/{rid}/,
# where `dotnet pack` picks them up (runtimes/{rid}/native/ in IoTCom.Net.Native.Modbus).
#
#   build/build-native.sh                       # host target
#   build/build-native.sh aarch64-unknown-linux-gnu x86_64-unknown-linux-musl
#
# Linux glibc targets use cargo-zigbuild (glibc 2.17 baseline) when available; musl/ARM can use `cross`.
set -euo pipefail
cd "$(dirname "$0")/../rust"
ROOT="$(cd .. && pwd)"

rid_for() {
  case "$1" in
    x86_64-pc-windows-msvc) echo win-x64 ;;
    aarch64-pc-windows-msvc) echo win-arm64 ;;
    x86_64-unknown-linux-gnu) echo linux-x64 ;;
    aarch64-unknown-linux-gnu) echo linux-arm64 ;;
    armv7-unknown-linux-gnueabihf) echo linux-arm ;;
    x86_64-unknown-linux-musl) echo linux-musl-x64 ;;
    aarch64-unknown-linux-musl) echo linux-musl-arm64 ;;
    x86_64-apple-darwin) echo osx-x64 ;;
    aarch64-apple-darwin) echo osx-arm64 ;;
    *) echo "unknown-$1" ;;
  esac
}

lib_for() {
  case "$1" in
    *windows*) echo iotcom_modbus.dll ;;
    *apple*) echo libiotcom_modbus.dylib ;;
    *) echo libiotcom_modbus.so ;;
  esac
}

targets=("$@")
if [ ${#targets[@]} -eq 0 ]; then targets=("$(rustc -vV | sed -n 's/host: //p')"); fi

for t in "${targets[@]}"; do
  rid="$(rid_for "$t")"
  echo "==> $t ($rid)"
  rustup target add "$t" >/dev/null 2>&1 || true
  # musl defaults to +crt-static, which disables cdylib output.
  if [[ "$t" == *musl* ]]; then export RUSTFLAGS="-C target-feature=-crt-static"; else unset RUSTFLAGS; fi
  if [[ "$t" == *linux-gnu* ]] && command -v cargo-zigbuild >/dev/null; then
    cargo zigbuild --release -p iotcom-modbus-native --target "$t.2.17"
  elif [[ "$t" == *linux* ]] && command -v cross >/dev/null && [[ "$t" != "$(rustc -vV | sed -n 's/host: //p')" ]]; then
    cross build --release -p iotcom-modbus-native --target "$t"
  else
    cargo build --release -p iotcom-modbus-native --target "$t"
  fi
  mkdir -p "$ROOT/artifacts/native/$rid"
  cp "target/$t/release/$(lib_for "$t")" "$ROOT/artifacts/native/$rid/"
done
ls -R "$ROOT/artifacts/native"
