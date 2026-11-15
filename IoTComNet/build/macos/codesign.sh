#!/usr/bin/env bash
# codesign.sh <file> [entitlements.plist] — Developer ID signature with the hardened runtime and a secure timestamp,
# as notarisation requires. SIGN_IDENTITY comes from import-certificate.sh.
set -euo pipefail
file="$1"
args=(--force --timestamp --options runtime --sign "$SIGN_IDENTITY")
if [ $# -ge 2 ]; then args+=(--entitlements "$2"); fi
codesign "${args[@]}" "$file"
codesign --verify --strict --verbose=1 "$file"
