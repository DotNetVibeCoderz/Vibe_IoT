---
title: Rilis dan penandatanganan
translation-status: synced
---

# Rilis dan penandatanganan

Sebuah rilis adalah tag `iotcomnet-vX.Y.Z[-sufiks]` di `main`. Workflow `.github/workflows/iotcomnet-release.yml` lalu:

1. membangun pustaka native untuk setiap RID (`iotcomnet-native.yml`);
2. menandatanganinya: DLL Windows dengan Authenticode, dylib macOS dengan Developer ID dan notarisasi Apple;
3. menguji, mem-pack setiap paket NuGet, menandatangani paket sebagai author, dan mendorongnya ke nuget.org;
4. menerbitkan CLI (`iotcom`) dan Gallery sebagai build self-contained satu berkas untuk win-x64, linux-x64, dan
   osx-arm64, menandatangani dan menotarisasinya, lalu melampirkannya ke rilis GitHub yang catatannya diambil dari
   `CHANGELOG.md`.

## Sebelum memberi tag

```bash
dotnet build IoTCom.Net.slnx -c Release
dotnet test tests/IoTCom.Net.Tests -c Release
dotnet pack IoTCom.Net.slnx -c Release -o artifacts/packages
python build/check_docs_parity.py && python build/check_notebooks.py
```

Beberapa aturan analyzer hanya muncul di Release, dan notebook dijalankan terhadap paket yang sudah di-pack.

## Secret

| Secret | Dipakai untuk |
|---|---|
| `NUGET_API_KEY` | mendorong ke nuget.org (cakupan: push paket dan versi baru untuk `IoTCom.Net*`) |
| `WINDOWS_SIGN_PFX_BASE64`, `WINDOWS_SIGN_PFX_PASSWORD` | Authenticode untuk DLL dan executable Windows, serta penandatanganan author NuGet (sertifikat code-signing sebagai PFX base64) |
| `APPLE_DEVELOPER_ID_P12_BASE64`, `APPLE_DEVELOPER_ID_P12_PASSWORD` | sertifikat *Developer ID Application* (P12 base64) |
| `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD` | kredensial `notarytool` (app-specific password) |

Penandatanganan bersifat opsional: tanpa secret, langkahnya dilewati dan rilis tetap terbit tanpa tanda tangan, sehingga
pipeline bekerja di fork dan sebelum sertifikat dibeli. Executable macOS ditandatangani dengan hardened runtime dan
entitlement di `build/macos/entitlements.plist` (JIT, pustaka native yang diekstrak, Bluetooth, USB). Executable dan
pustaka tunggal tidak dapat di-staple; Gatekeeper mengambil tiket notarisasi secara daring.

Ubah sertifikat menjadi secret dengan `base64 -w0 certificate.pfx` (Linux) atau `base64 -i certificate.p12` (macOS).

## Jika push gagal

Jalankan ulang hanya job yang gagal setelah penyebabnya diperbaiki (misalnya `NUGET_API_KEY` yang diperbarui):
`gh run rerun <run-id> --failed`. `--skip-duplicate` membuat push kedua untuk versi yang sama tidak berbahaya.
