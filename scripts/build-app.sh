#!/bin/bash
# Build OpenScreen.app and a .dmg you can put online.
#
#   scripts/build-app.sh               build + sign + dmg
#   VERSION=0.2.0 scripts/build-app.sh
#   SIGN_IDENTITY="Developer ID Application: ..." scripts/build-app.sh
#   NOTARY_PROFILE=openscreen scripts/build-app.sh   also notarize + staple (see README)
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${VERSION:-0.2.0}"
BUILD="$(git rev-list --count HEAD 2>/dev/null || echo 1)"
DIST="dist"
APP="$DIST/OpenScreen.app"
DMG="$DIST/openscreen-$VERSION.dmg"

echo "→ compiling (release, arm64)"
swift build -c release --arch arm64
BIN="$(swift build -c release --arch arm64 --show-bin-path)/openscreen"

echo "→ assembling $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/OpenScreen"
sed -e "s/__VERSION__/$VERSION/" -e "s/__BUILD__/$BUILD/" packaging/Info.plist > "$APP/Contents/Info.plist"

ICONSET="$DIST/AppIcon.iconset"
rm -rf "$ICONSET"
swift scripts/make-icon.swift "$ICONSET"
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
rm -rf "$ICONSET"

# Signing: Developer ID if present (needed for downloads), else Apple Development, else ad-hoc.
if [ -z "${SIGN_IDENTITY:-}" ]; then
  SIGN_IDENTITY="$(security find-identity -v -p codesigning | grep -o '"Developer ID Application[^"]*"' | head -1 | tr -d '"' || true)"
fi
if [ -z "$SIGN_IDENTITY" ]; then
  SIGN_IDENTITY="$(security find-identity -v -p codesigning | grep -o '"Apple Development[^"]*"' | head -1 | tr -d '"' || true)"
fi
SIGN_IDENTITY="${SIGN_IDENTITY:--}"
echo "→ signing with: $SIGN_IDENTITY"
codesign --force --options runtime --timestamp=none \
  --entitlements packaging/openscreen.entitlements \
  --sign "$SIGN_IDENTITY" "$APP"
codesign --verify --strict --verbose=1 "$APP"

echo "→ making $DMG"
STAGE="$DIST/dmg"
rm -rf "$STAGE" "$DMG"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "openscreen $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
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
