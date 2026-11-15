#!/usr/bin/env bash
# notarise.sh <archive.zip> — submits signed binaries to Apple's notary service and waits for the verdict.
# Needs APPLE_ID, APPLE_TEAM_ID and APPLE_APP_PASSWORD (an app-specific password). Bare executables and libraries
# cannot be stapled; Gatekeeper finds the ticket online.
set -euo pipefail
archive="$1"
if [ -z "${APPLE_ID:-}" ] || [ -z "${APPLE_TEAM_ID:-}" ] || [ -z "${APPLE_APP_PASSWORD:-}" ]; then
  echo "::warning::APPLE_ID, APPLE_TEAM_ID or APPLE_APP_PASSWORD is missing: signed but not notarised."
  exit 0
fi
result="$(xcrun notarytool submit "$archive" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" --wait --output-format json)"
echo "$result"
status="$(echo "$result" | python3 -c 'import json,sys; print(json.load(sys.stdin)["status"])')"
if [ "$status" != "Accepted" ]; then
  id="$(echo "$result" | python3 -c 'import json,sys; print(json.load(sys.stdin)["id"])')"
  xcrun notarytool log "$id" --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" || true
  echo "::error::Notarisation $status."
  exit 1
fi
