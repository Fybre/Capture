#!/bin/bash
# Publishes Capture, assembles it into a real .app bundle, code-signs it, packages it into a .dmg, and
# (if Apple notarization credentials are present) notarizes and staples it. Produces
# packaging/macos/out/Capture-<version>.dmg.
#
# Signing identity: set CAPTURE_SIGNING_IDENTITY to a "Developer ID Application" certificate name/hash
# for a real signature. Falls back to ad-hoc signing ("-") for local dev — good enough to smoke-test
# that the bundle assembles and launches, but Gatekeeper will still block it for anyone else; only a
# real identity plus notarization (below) produces something distributable.
#
# Notarization (all four must be set, or notarization/stapling is skipped — useful for a quick local
# build without needing real Apple credentials on hand):
#   APPLE_API_KEY_ID       App Store Connect API key ID
#   APPLE_API_ISSUER_ID    App Store Connect API issuer ID
#   APPLE_API_KEY_P8_PATH  Path to the downloaded .p8 private key file
#   APPLE_TEAM_ID          Apple Developer Team ID (informational; notarytool derives the rest from the key)
set -euo pipefail

# Without this, `cp -R` on APFS can carry over a decmpfs/resource-fork-adjacent attribute from the
# .NET SDK's apphost template that doesn't show up under `xattr -l` but still makes codesign fail with
# "resource fork, Finder information, or similar detritus not allowed" on the main executable —
# confirmed by reproducing it and fixing it with exactly this env var. Standard macOS fix: it tells
# cp/ditto/tar to skip resource forks/xattrs/ACLs entirely during the copy.
export COPYFILE_DISABLE=1

if [[ "$(uname)" != "Darwin" ]]; then
  echo "build-dmg.sh: not on macOS, aborting." >&2
  exit 1
fi

VERSION="${1:?Usage: build-dmg.sh <version>}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
IDENTITY="${CAPTURE_SIGNING_IDENTITY:--}"
ENTITLEMENTS="$SCRIPT_DIR/entitlements.plist"

# Deliberately NOT a subdirectory of the repo: on a machine where the repo lives under an
# iCloud-Drive-synced folder (e.g. ~/Documents), the file-provider daemon asynchronously touches
# freshly-written files there in real time, which intermittently makes codesign fail with "resource
# fork, Finder information, or similar detritus not allowed" — reproduced directly (confirmed via
# `brctl status` showing active sync activity inside this exact build path) and confirmed fixed by
# building outside any synced tree. CI runners have no iCloud sync, so this is purely a local-machine
# concern, but mktemp -d sidesteps it unconditionally rather than assuming where the repo lives.
WORK_DIR="$(mktemp -d /tmp/capture-dmg-build.XXXXXX)"
trap 'rm -rf "$WORK_DIR"' EXIT
OUT_DIR="$SCRIPT_DIR/out"
PUBLISH_DIR="$WORK_DIR/publish"
APP_DIR="$WORK_DIR/Capture.app"
DMG_STAGING="$WORK_DIR/dmg-staging"

rm -rf "$OUT_DIR"
mkdir -p "$PUBLISH_DIR" "$OUT_DIR"

echo "==> Publishing self-contained osx-arm64 build"
# PublishSingleFile matters here beyond convenience: codesign refuses to seal an app bundle that has
# loose PE-format managed .dll files sitting directly in Contents/MacOS ("code object is not signed at
# all, in subcomponent: X.dll" — codesign requires everything there to be genuinely signable code).
# Bundling the managed assemblies into the one Mach-O apphost removes that whole class of file.
dotnet publish "$REPO_ROOT/src/Capture.App/Capture.App.csproj" \
  --runtime osx-arm64 \
  --self-contained true \
  --configuration Release \
  -p:Version="$VERSION" \
  -p:PublishSingleFile=true \
  -p:DebugType=none \
  --output "$PUBLISH_DIR"

# LLamaSharp's native package ships every RID's binaries in one NuGet package and dotnet publish
# doesn't prune the ones that don't match — leaves win-x64/win-arm64/linux-*/osx-x64 dead weight
# (including more loose PE .dlls, the other source of the codesign issue above) in an osx-arm64-only
# bundle. Safe to delete outright: nothing on macOS loads another RID's native assets.
echo "==> Pruning non-macOS native runtime assets"
find "$PUBLISH_DIR/runtimes" -mindepth 1 -maxdepth 1 -type d ! -name osx-arm64 -exec rm -rf {} +

# BuildCaptureScanHelperMac (Capture.App.csproj) only hooks AfterTargets="Build", not "Publish", so
# `dotnet publish` alone would ship without the scan helper — re-run it explicitly against the publish
# output rather than editing that target (used by every local dev Build). Pass through this script's
# own resolved identity so the helper gets a real Developer ID signature too, not the ad-hoc default —
# it's excluded from the later individual-signing loop (re-signing it there would invalidate this
# bundle-level signature), so this is the only place it gets signed at all.
echo "==> Building CaptureScanHelperMac into the publish output"
CAPTURE_SCAN_HELPER_SIGNING_IDENTITY="$IDENTITY" "$REPO_ROOT/native/CaptureScanHelperMac/build.sh" "$PUBLISH_DIR"

echo "==> Assembling Capture.app"
mkdir -p "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources"
cp -R "$PUBLISH_DIR/." "$APP_DIR/Contents/MacOS/"
cp "$REPO_ROOT/src/Capture.App/Assets/Brand/Capture.icns" "$APP_DIR/Contents/Resources/Capture.icns"

cat > "$APP_DIR/Contents/Info.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>Capture.App</string>
    <key>CFBundleIdentifier</key>
    <string>com.fybre.capture</string>
    <key>CFBundleName</key>
    <string>Capture</string>
    <key>CFBundleDisplayName</key>
    <string>Capture</string>
    <key>CFBundleIconFile</key>
    <string>Capture</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleVersion</key>
    <string>$VERSION</string>
    <key>LSMinimumSystemVersion</key>
    <string>13.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
PLIST

echo "==> Relocating large data payloads to Contents/Resources"
# codesign's (non-deep) bundle-level seal requires every loose file directly under Contents/MacOS to
# be validly signable code — confirmed the hard way: it rejects Presidio's ~300MB PyInstaller data tree
# even for plain non-code files it finds in there (e.g. a stray .cu CUDA source file included as
# package data), and Tesseract's tessdata is data, not code, either. Contents/Resources is exempt from
# this. Move both payloads there and leave a symlink behind at the original path so
# PresidioSidecarLauncher's/TesseractCliOcrEngine's existing Path.Combine(AppContext.BaseDirectory, ...)
# lookups still resolve correctly at runtime (symlinks are transparent to file I/O) — no app source
# changes needed. Verified this specific pattern (symlink in Contents/MacOS pointing into
# Contents/Resources) signs and seals cleanly.
for payload in presidio-sidecar-data tessdata; do
  if [[ -d "$APP_DIR/Contents/MacOS/$payload" ]]; then
    mv "$APP_DIR/Contents/MacOS/$payload" "$APP_DIR/Contents/Resources/$payload"
    ln -s "../Resources/$payload" "$APP_DIR/Contents/MacOS/$payload"
  fi
done

echo "==> Repairing macOS framework layouts"
xattr -cr "$APP_DIR"
# NuGet's .nupkg packaging (a plain zip) doesn't preserve symlinks, so a proper macOS framework's
# Versions/Current -> <version> symlink, and its top-level Python/Resources/etc -> Versions/Current/...
# symlinks, arrive as real duplicated files/dirs instead (confirmed via `cmp` — identical content, just
# not a symlink). That flattened layout is exactly what makes codesign report "bundle format is
# ambiguous (could be app or framework)" on presidio-sidecar-data's embedded Python.framework. Rebuild
# the canonical symlink structure before signing rather than trying to sign the broken layout as-is.
while IFS= read -r -d '' framework; do
  versions_dir="$framework/Versions"
  [[ -d "$versions_dir" ]] || continue
  actual_version="$(find "$versions_dir" -mindepth 1 -maxdepth 1 -type d ! -name Current -print -quit)"
  [[ -n "$actual_version" ]] || continue
  version_name="$(basename "$actual_version")"

  current_link="$versions_dir/Current"
  if [[ ! -L "$current_link" ]]; then
    rm -rf "$current_link"
    ln -s "$version_name" "$current_link"
  fi

  while IFS= read -r -d '' entry; do
    name="$(basename "$entry")"
    [[ "$name" == "Versions" ]] && continue
    if [[ ! -L "$entry" ]]; then
      rm -rf "$entry"
      ln -s "Versions/Current/$name" "$entry"
    fi
  done < <(find "$framework" -mindepth 1 -maxdepth 1 -print0)
done < <(find "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources" -type d -name "*.framework" -print0)

echo "==> Code-signing nested binaries"
# `codesign --deep` is deliberately avoided here — it walks every directory looking for things that
# look bundle-like and chokes on the Presidio sidecar's PyInstaller-generated *.dist-info folders
# ("bundle format unrecognized"), which aren't real bundles at all. Sign real content explicitly
# instead, in dependency order: framework bundles as units (e.g. presidio-sidecar-data's embedded
# Python.framework — signing its raw Mach-O binary directly is ambiguous; codesign wants the
# .framework directory itself), then every other Mach-O dylib/executable individually, skipping
# CaptureScanHelperMac.app (already signed as its own bundle above — re-signing its contents
# individually would invalidate that signature) and skipping inside any *.framework (covered by the
# framework-level signature just applied). Finally sign the outer .app once as a plain (non-deep)
# bundle signature.
while IFS= read -r -d '' framework; do
  codesign --force -s "$IDENTITY" --entitlements "$ENTITLEMENTS" --options runtime --timestamp "$framework"
done < <(find "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources" -type d -name "*.framework" -print0)

while IFS= read -r -d '' f; do
  if file -b "$f" | grep -q "Mach-O"; then
    codesign --force -s "$IDENTITY" --entitlements "$ENTITLEMENTS" --options runtime --timestamp "$f"
  fi
done < <(find "$APP_DIR/Contents/MacOS" "$APP_DIR/Contents/Resources" -type f ! -path "*/CaptureScanHelperMac.app/*" ! -path "*.framework/*" -print0)

echo "==> Code-signing Capture.app ($IDENTITY)"
# CoreCLR's JIT needs to mmap executable memory at runtime — without allow-jit/allow-unsigned-
# executable-memory in the entitlements, hardened runtime blocks it and the app fails to start with
# "Failed to create CoreCLR, HRESULT: 0x80070008" (reproduced and confirmed this entitlements.plist
# fixes it — see packaging/macos/entitlements.plist).
codesign --force -s "$IDENTITY" --entitlements "$ENTITLEMENTS" --options runtime --timestamp "$APP_DIR"

# Deliberately not version-suffixed: the README links to
# github.com/Fybre/Capture/releases/latest/download/Capture.dmg, which only resolves correctly when
# every release's asset has this exact same filename — the release itself (tag/title) still carries
# the version, visible on the GitHub release page.
DMG_PATH="$OUT_DIR/Capture.dmg"
echo "==> Building $DMG_PATH"
mkdir -p "$DMG_STAGING"
cp -R "$APP_DIR" "$DMG_STAGING/Capture.app"
ln -s /Applications "$DMG_STAGING/Applications"
hdiutil create -volname Capture -srcfolder "$DMG_STAGING" -ov -format UDZO "$DMG_PATH"

echo "==> Code-signing $DMG_PATH"
codesign --force -s "$IDENTITY" --timestamp "$DMG_PATH"

if [[ -n "${APPLE_API_KEY_ID:-}" && -n "${APPLE_API_ISSUER_ID:-}" && -n "${APPLE_API_KEY_P8_PATH:-}" ]]; then
  echo "==> Notarizing $DMG_PATH"
  notarize_output="$(xcrun notarytool submit "$DMG_PATH" \
    --key "$APPLE_API_KEY_P8_PATH" \
    --key-id "$APPLE_API_KEY_ID" \
    --issuer "$APPLE_API_ISSUER_ID" \
    --wait 2>&1)"
  echo "$notarize_output"
  if ! grep -q "status: Accepted" <<< "$notarize_output"; then
    # `--wait` only reports Accepted/Invalid, not *why* — fetch the actual rejection reasons so a CI
    # failure is self-diagnosing instead of needing someone with local Apple credentials to re-fetch it.
    submission_id="$(grep -m1 '^  id:' <<< "$notarize_output" | awk '{print $2}')"
    echo "==> Notarization rejected — fetching detailed log for $submission_id"
    xcrun notarytool log "$submission_id" \
      --key "$APPLE_API_KEY_P8_PATH" \
      --key-id "$APPLE_API_KEY_ID" \
      --issuer "$APPLE_API_ISSUER_ID"
    exit 1
  fi
  echo "==> Stapling notarization ticket"
  xcrun stapler staple "$DMG_PATH"
else
  echo "==> Skipping notarization (APPLE_API_KEY_ID/APPLE_API_ISSUER_ID/APPLE_API_KEY_P8_PATH not all set)"
fi

echo "==> Done: $DMG_PATH"
