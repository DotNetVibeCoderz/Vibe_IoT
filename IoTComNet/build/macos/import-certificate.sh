#!/usr/bin/env bash
# Imports the Developer ID Application certificate (APPLE_DEVELOPER_ID_P12_BASE64 / _PASSWORD) into a temporary keychain
# and exports its identity as SIGN_IDENTITY for later steps. Used by .github/workflows/iotcomnet-release.yml.
set -euo pipefail
keychain="$RUNNER_TEMP/iotcom-signing.keychain-db"
password="$(uuidgen)"
echo "$APPLE_DEVELOPER_ID_P12_BASE64" | base64 --decode > "$RUNNER_TEMP/developer-id.p12"
security create-keychain -p "$password" "$keychain"
security set-keychain-settings -lut 21600 "$keychain"
security unlock-keychain -p "$password" "$keychain"
security import "$RUNNER_TEMP/developer-id.p12" -k "$keychain" -P "$APPLE_DEVELOPER_ID_P12_PASSWORD" -T /usr/bin/codesign
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$password" "$keychain" > /dev/null
security list-keychains -d user -s "$keychain" $(security list-keychains -d user | tr -d '"')
rm -f "$RUNNER_TEMP/developer-id.p12"
identity="$(security find-identity -v -p codesigning "$keychain" | grep 'Developer ID Application' | head -1 | sed -E 's/.*"(.*)"/\1/')"
if [ -z "$identity" ]; then
  echo "::error::The certificate is not a Developer ID Application certificate."
  exit 1
fi
echo "SIGN_IDENTITY=$identity" >> "$GITHUB_ENV"
echo "Imported: $identity"
