#!/bin/bash
# Build OpenRewind.app and a .dmg you can put online.
#
#   scripts/build-app.sh               build + sign + dmg
#   VERSION=0.2.0 scripts/build-app.sh
#   SIGN_IDENTITY="Developer ID Application: ..." scripts/build-app.sh   (default: see Signing below)
#   NOTARY_PROFILE=openrewind scripts/build-app.sh   also notarize + staple (see README)
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${VERSION:-0.2.0}"
BUILD="$(git rev-list --count HEAD 2>/dev/null || echo 1)"
DIST="dist"
APP="$DIST/OpenRewind.app"
DMG="$DIST/openrewind-$VERSION.dmg"

echo "→ compiling (release, arm64)"
# Package.resolved pins swift-sqlcipher to one commit; never resolve anything newer at build time.
swift build -c release --arch arm64 --only-use-versions-from-resolved-file
BIN="$(swift build -c release --arch arm64 --only-use-versions-from-resolved-file --show-bin-path)/openrewind"

echo "→ assembling $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/OpenRewind"
sed -e "s/__VERSION__/$VERSION/" -e "s/__BUILD__/$BUILD/" packaging/Info.plist > "$APP/Contents/Info.plist"

# Icon PNGs come from branding/logo.png via scripts/make-icons.py.
iconutil -c icns packaging/AppIcon.iconset -o "$APP/Contents/Resources/AppIcon.icns"
# Licenses of SQLCipher, LibTomCrypt and the rest that the app includes.
cp THIRD_PARTY_NOTICES.txt "$APP/Contents/Resources/THIRD_PARTY_NOTICES.txt"

# Signing. macOS keeps Screen Recording / Accessibility / Microphone permission for the signing
# identity, so every build has to use the same one or updates lose them (see "Signing" in the README).
# Order: SIGN_IDENTITY, Developer ID, the OpenRewind self-signed certificate
# (scripts/make-signing-cert.sh), Apple Development, ad-hoc.
if [ -z "${SIGN_IDENTITY:-}" ]; then
  SIGN_IDENTITY="$(security find-identity -v -p codesigning | grep -o '"Developer ID Application[^"]*"' | head -1 | tr -d '"' || true)"
fi
if [ -z "$SIGN_IDENTITY" ]; then
  # No -v: the self-signed certificate isn't trusted by the system, and codesign doesn't need it to be.
  SIGN_IDENTITY="$(security find-identity -p codesigning | awk '/"OpenRewind Code Signing"/ {print $2; exit}' || true)"
fi
if [ -z "$SIGN_IDENTITY" ]; then
  SIGN_IDENTITY="$(security find-identity -v -p codesigning | grep -o '"Apple Development[^"]*"' | head -1 | tr -d '"' || true)"
fi
SIGN_IDENTITY="${SIGN_IDENTITY:--}"
echo "→ signing with: $SIGN_IDENTITY"
if [ "$SIGN_IDENTITY" = "-" ]; then
  echo "!! ad-hoc signature: macOS will ask for its permissions again after every rebuild"
fi
# Notarization needs a secure timestamp; the other identities don't.
TIMESTAMP="--timestamp=none"
if security find-identity -v -p codesigning | grep -F "$SIGN_IDENTITY" | grep -q "Developer ID Application"; then
  TIMESTAMP="--timestamp"
fi
codesign --force --options runtime "$TIMESTAMP" \
  --entitlements packaging/openrewind.entitlements \
  --sign "$SIGN_IDENTITY" "$APP"
codesign --verify --strict --verbose=1 "$APP"
# The designated requirement is what macOS stores with each permission. It has to name the
# certificate (not a cdhash) for permissions to carry over to the next build.
codesign --display --requirements - "$APP" 2>&1 | grep '^designated' || true

echo "→ making $DMG"
STAGE="$DIST/dmg"
rm -rf "$STAGE" "$DMG"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "openrewind $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"
if [ "$SIGN_IDENTITY" != "-" ]; then codesign --force --sign "$SIGN_IDENTITY" "$DMG"; fi

if [ -n "${NOTARY_PROFILE:-}" ]; then
  if [[ "$SIGN_IDENTITY" != Developer\ ID\ Application* ]]; then
    echo "!! notarizing needs a 'Developer ID Application' certificate; skipping"
  else
    echo "→ notarizing (this takes a few minutes)"
    xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$DMG"
  fi
fi

echo "✓ $APP"
echo "✓ $DMG ($(du -h "$DMG" | cut -f1))"
