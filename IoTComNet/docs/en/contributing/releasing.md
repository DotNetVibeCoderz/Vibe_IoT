---
title: Releasing and signing
translation-status: synced
---

# Releasing and signing

A release is a tag `iotcomnet-vX.Y.Z[-suffix]` on `main`. The workflow `.github/workflows/iotcomnet-release.yml` then:

1. builds the native libraries for every RID (`iotcomnet-native.yml`);
2. signs them: Windows DLLs with Authenticode, macOS dylibs with a Developer ID and Apple notarisation;
3. tests, packs every NuGet package, author-signs the packages and pushes them to nuget.org;
4. publishes the CLI (`iotcom`) and the Gallery as self-contained, single-file builds for win-x64, linux-x64 and
   osx-arm64, signs and notarises them, and attaches them to a GitHub release whose notes come from `CHANGELOG.md`.

## Before tagging

```bash
dotnet build IoTCom.Net.slnx -c Release
dotnet test tests/IoTCom.Net.Tests -c Release
dotnet pack IoTCom.Net.slnx -c Release -o artifacts/packages
python build/check_docs_parity.py && python build/check_notebooks.py
```

Some analyzer rules only fire in Release, and the notebooks run against the packed packages.

## Secrets

| Secret | Used for |
|---|---|
| `NUGET_API_KEY` | pushing to nuget.org (scope: push new packages and versions for `IoTCom.Net*`) |
| `WINDOWS_SIGN_PFX_BASE64`, `WINDOWS_SIGN_PFX_PASSWORD` | Authenticode for Windows DLLs and executables, and NuGet author signing (a code-signing certificate as a base64 PFX) |
| `APPLE_DEVELOPER_ID_P12_BASE64`, `APPLE_DEVELOPER_ID_P12_PASSWORD` | the *Developer ID Application* certificate (base64 P12) |
| `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD` | `notarytool` credentials (an app-specific password) |

Signing is optional: without a secret, its step is skipped and the release ships unsigned, so the pipeline works on
forks and before certificates are bought. macOS executables are signed with the hardened runtime and the entitlements
in `build/macos/entitlements.plist` (JIT, extracted native libraries, Bluetooth, USB). Bare executables and libraries
cannot be stapled; Gatekeeper fetches the notarisation ticket online.

Encode a certificate for a secret with `base64 -w0 certificate.pfx` (Linux) or `base64 -i certificate.p12` (macOS).

## If a push fails

Re-run only the failed jobs after fixing the cause (for example a renewed `NUGET_API_KEY`):
`gh run rerun <run-id> --failed`. `--skip-duplicate` makes a second push of the same version harmless. The push runs in
its own job (`nuget-push`), so an expired key does not hold back the GitHub release: the packages are attached to it too.
