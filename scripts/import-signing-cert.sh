#!/bin/bash
# CI: load the Mac signing certificate from the MACOS_CERTIFICATE_P12 (base64 .p12) and
# MACOS_CERTIFICATE_PASSWORD secrets into a temporary keychain and set SIGN_IDENTITY for
# scripts/build-app.sh. Works the same for the self-signed certificate from
# scripts/make-signing-cert.sh and for a Developer ID Application certificate exported as .p12.
#
#   scripts/import-signing-cert.sh --remove   deletes the keychain again (run it once signing is done)
set -euo pipefail
TMP="${RUNNER_TEMP:-${TMPDIR:-/tmp}}"
KEYCHAIN="$TMP/openrewind-signing.keychain-db"
if [ "${1:-}" = "--remove" ]; then
  security delete-keychain "$KEYCHAIN" 2>/dev/null || true
  exit 0
fi
: "${MACOS_CERTIFICATE_P12:?set MACOS_CERTIFICATE_P12 (base64 of the .p12)}"
: "${MACOS_CERTIFICATE_PASSWORD:?set MACOS_CERTIFICATE_PASSWORD}"
KEYCHAIN_PASSWORD="$(openssl rand -hex 16)"
P12="$TMP/openrewind-signing.p12"

trap 'rm -f "$P12"' EXIT
(umask 077; printf '%s' "$MACOS_CERTIFICATE_P12" | base64 --decode > "$P12")
security delete-keychain "$KEYCHAIN" 2>/dev/null || true
security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security set-keychain-settings -lut 21600 "$KEYCHAIN"
security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
# `security` takes passwords only as arguments. That's fine on GitHub's single-use runners; don't run
# this on a shared machine. -x makes the key non-extractable: codesign can use it, nothing can copy it.
security import "$P12" -k "$KEYCHAIN" -P "$MACOS_CERTIFICATE_PASSWORD" -f pkcs12 -x -T /usr/bin/codesign
rm -f "$P12"
# A Developer ID .p12 usually holds only your certificate; codesign also needs Apple's intermediate.
# Harmless for the self-signed certificate.
for CA in DeveloperIDG2CA.cer DeveloperIDCA.cer; do
  if curl -fsSL "https://www.apple.com/certificateauthority/$CA" -o "$TMP/$CA"; then
    security import "$TMP/$CA" -k "$KEYCHAIN" >/dev/null 2>&1 || true
    rm -f "$TMP/$CA"
  fi
done
# Lets codesign use the key without a dialog.
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KEYCHAIN_PASSWORD" "$KEYCHAIN" >/dev/null
# shellcheck disable=SC2046
security list-keychains -d user -s "$KEYCHAIN" $(security list-keychains -d user | tr -d '"')

# No -v: a self-signed certificate isn't trusted by the system, and codesign doesn't need it to be.
IDENTITY="$(security find-identity -p codesigning "$KEYCHAIN" | awk '/^ *[0-9]+\)/ {print $2; exit}')"
[ -n "$IDENTITY" ] || { echo "!! no code signing identity in the .p12"; exit 1; }
echo "→ signing identity: $(security find-identity -p codesigning "$KEYCHAIN" | awk '/^ *[0-9]+\)/ {print; exit}')"
if [ -n "${GITHUB_ENV:-}" ]; then echo "SIGN_IDENTITY=$IDENTITY" >> "$GITHUB_ENV"; else echo "SIGN_IDENTITY=$IDENTITY"; fi
