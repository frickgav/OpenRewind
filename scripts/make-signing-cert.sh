#!/bin/bash
# Make the self-signed certificate that signs every OpenRewind Mac build.
#
# macOS keeps Screen Recording, Accessibility and Microphone permission for "the app signed by this
# certificate". Ad-hoc builds get a new identity each time, so every update lost them. Make the
# certificate once, keep it, and sign every build with it (see "Signing" in the README).
#
#   scripts/make-signing-cert.sh                  writes ./signing/
#   OUT=/some/dir scripts/make-signing-cert.sh "OpenRewind Code Signing"
#
# Writes openrewind-signing.p12, openrewind-signing.p12.b64 (paste into the MACOS_CERTIFICATE_P12
# secret) and password.txt (the MACOS_CERTIFICATE_PASSWORD secret). Keep them private: anything
# signed with this key gets the permissions you gave OpenRewind.
set -euo pipefail

NAME="${1:-OpenRewind Code Signing}"
OUT="${OUT:-signing}"
umask 077
mkdir -p "$OUT"
PASSWORD="${PASSWORD:-$(openssl rand -hex 24)}"
export PASSWORD

CONF="$OUT/cert.cnf"
# The unencrypted key.pem must not outlive this script, even if a step below fails.
trap 'rm -f "$OUT/key.pem" "$CONF"' EXIT
cat > "$CONF" <<CNF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $NAME
[ext]
basicConstraints = critical, CA:false
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
subjectKeyIdentifier = hash
CNF

# 20 years, so it never has to change (a new certificate means granting the permissions once more).
openssl req -x509 -newkey rsa:2048 -sha256 -days 7300 -nodes -config "$CONF" \
  -keyout "$OUT/key.pem" -out "$OUT/cert.pem" 2>/dev/null

# macOS `security import` reads only the older PKCS#12 encryption; OpenSSL 3 needs -legacy for it.
LEGACY=""
if openssl pkcs12 -help 2>&1 | grep -q -- -legacy; then LEGACY="-legacy"; fi
openssl pkcs12 -export $LEGACY -name "$NAME" -inkey "$OUT/key.pem" -in "$OUT/cert.pem" \
  -out "$OUT/openrewind-signing.p12" -passout env:PASSWORD
base64 < "$OUT/openrewind-signing.p12" | tr -d '\n' > "$OUT/openrewind-signing.p12.b64"
printf '%s' "$PASSWORD" > "$OUT/password.txt"

echo "✓ $OUT/openrewind-signing.p12  ($NAME, SHA-1 $(openssl x509 -in "$OUT/cert.pem" -noout -fingerprint -sha1 | cut -d= -f2 | tr -d :))"
echo "  GitHub → Settings → Environments → release → Environment secrets (not repository secrets):"
echo "    MACOS_CERTIFICATE_P12       = contents of $OUT/openrewind-signing.p12.b64"
echo "    MACOS_CERTIFICATE_PASSWORD  = contents of $OUT/password.txt"
echo "  To build signed on this Mac: open $OUT/openrewind-signing.p12 and enter the password."
