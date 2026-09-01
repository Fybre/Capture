#!/bin/bash
# Builds CaptureScanHelperMac as a signed .app bundle and copies it to the output directory passed as
# $1. Invoked from Capture.App.csproj as a macOS-only build step (see the AfterBuild target there).
#
# Signing identity: set CAPTURE_SCAN_HELPER_SIGNING_IDENTITY to a Developer ID Application certificate
# name/hash for a real signature (required for distribution — Gatekeeper/notarization). Falls back to
# ad-hoc signing ("-") for local dev builds without one; ad-hoc signing has been confirmed sufficient
# for ICDeviceBrowser to actually report devices, as long as the entitlements below are present and
# the device-type mask bug (see main.swift) is avoided — a real identity is only required for shipping.
set -euo pipefail

if [[ "$(uname)" != "Darwin" ]]; then
  echo "CaptureScanHelperMac: not on macOS, skipping." >&2
  exit 0
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT_DIR="${1:?Usage: build.sh <output-directory>}"
IDENTITY="${CAPTURE_SCAN_HELPER_SIGNING_IDENTITY:--}"

BUILD_DIR="$SCRIPT_DIR/.build"
rm -rf "$BUILD_DIR"
mkdir -p "$BUILD_DIR/CaptureScanHelperMac.app/Contents/MacOS"

swiftc "$SCRIPT_DIR/main.swift" -O -o "$BUILD_DIR/CaptureScanHelperMac.app/Contents/MacOS/CaptureScanHelperMac" \
  -framework ImageCaptureCore -framework AppKit

cat > "$BUILD_DIR/CaptureScanHelperMac.app/Contents/Info.plist" << 'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>CaptureScanHelperMac</string>
    <key>CFBundleIdentifier</key>
    <string>com.fybre.capture.scanhelper</string>
    <key>CFBundleName</key>
    <string>CaptureScanHelperMac</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>LSUIElement</key>
    <true/>
</dict>
</plist>
PLIST

xattr -cr "$BUILD_DIR/CaptureScanHelperMac.app"
# --timestamp=none is fine (and faster) for ad-hoc local-dev signing, but a real Developer ID signature
# needs a secure timestamp or Apple's notary service rejects it outright ("The signature does not
# include a secure timestamp") — reproduced via a real CI notarization failure.
TIMESTAMP_FLAG="--timestamp=none"
[[ "$IDENTITY" != "-" ]] && TIMESTAMP_FLAG="--timestamp"
codesign --force -s "$IDENTITY" --entitlements "$SCRIPT_DIR/entitlements.plist" --options runtime "$TIMESTAMP_FLAG" \
  "$BUILD_DIR/CaptureScanHelperMac.app"

mkdir -p "$OUT_DIR"
rm -rf "$OUT_DIR/CaptureScanHelperMac.app"
cp -R "$BUILD_DIR/CaptureScanHelperMac.app" "$OUT_DIR/CaptureScanHelperMac.app"
echo "CaptureScanHelperMac: built and signed ($IDENTITY) -> $OUT_DIR/CaptureScanHelperMac.app"
