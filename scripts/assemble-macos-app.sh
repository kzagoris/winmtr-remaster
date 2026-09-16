#!/usr/bin/env bash
# Complete a desktop bundle whose full, symbol-free publish payload is already
# staged in Contents/MacOS. Called only by the macOS desktop release branch.
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "Usage: bash scripts/assemble-macos-app.sh <WinMTR.app> <version> <osx-arm64|osx-x64>" >&2
  exit 1
fi
app="$1"
version="$2"
case "$3" in
  osx-arm64) arch=arm64 ;;
  osx-x64) arch=x86_64 ;;
  *) echo "Not a supported macOS desktop RID: $3" >&2; exit 1 ;;
esac
[ "$(uname -s)" = Darwin ]
[ "$(basename "$app")" = WinMTR.app ]
root="$(cd "$(dirname "$0")/.." && pwd)"

# .NET SDK 10.0.400 (global.json): .NET 10 supports macOS 14 and later,
# on both Arm64 and x64. Revisit this floor when upgrading the SDK line.
# https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md
minimum_macos=14.0
mkdir -p "$app/Contents/Resources"
cp "$root/src/WinMtr.Desktop/Assets/winmtr-route-pulse.icns" "$app/Contents/Resources/"
printf 'APPL????' > "$app/Contents/PkgInfo"

python3 - "$app" "$version" "$minimum_macos" "$root/global.json" <<'PY'
import json
import pathlib
import plistlib
import re
import sys

app, version, minimum, sdk_file = sys.argv[1:]
sdk = json.loads(pathlib.Path(sdk_file).read_text())["sdk"]["version"]
if not sdk.startswith("10.0."):
    raise SystemExit("Revisit the minimum macOS version for the pinned SDK")
# Three numeric release components, optionally followed by SemVer prerelease
# and build labels. Never silently truncate a malformed release tag.
match = re.fullmatch(
    r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?", version)
if match is None:
    raise SystemExit(f"Invalid release version: {version}")
if match[4] and any(part.isdigit() and len(part) > 1 and part[0] == "0"
                    for part in match[4].split(".")):
    raise SystemExit(f"Invalid numeric prerelease identifier: {version}")
# CFBundleVersion permits at most four, two, and two digits respectively.
if any(len(match[index]) > limit for index, limit in ((1, 4), (2, 2), (3, 2))):
    raise SystemExit(f"Release version exceeds Apple bundle version limits: {version}")
numeric = ".".join(match.groups()[:3])
metadata = {
    "CFBundleExecutable": "winmtr",
    "CFBundleName": "WinMTR",
    "CFBundleDisplayName": "WinMTR",
    "CFBundleIdentifier": "com.kzagoris.winmtr",
    "CFBundleIconFile": "winmtr-route-pulse.icns",
    "CFBundlePackageType": "APPL",
    "CFBundleInfoDictionaryVersion": "6.0",
    "CFBundleShortVersionString": numeric,
    "CFBundleVersion": numeric,
    "NSPrincipalClass": "NSApplication",
    "NSHighResolutionCapable": True,
    "LSMinimumSystemVersion": minimum,
    "LSApplicationCategoryType": "public.app-category.utilities",
    "WinMTRFullVersion": version,
}
plist = pathlib.Path(app) / "Contents/Info.plist"
plist.write_bytes(plistlib.dumps(metadata))
# Check the serialized types and values, not merely the input version string.
actual = plistlib.loads(plist.read_bytes())
assert actual == metadata
for key in ("CFBundleShortVersionString", "CFBundleVersion"):
    assert re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", actual[key])
    assert actual[key] == numeric
assert actual["WinMTRFullVersion"] == version
assert (pathlib.Path(app) / "Contents/PkgInfo").read_bytes() == b"APPL????"
PY

plutil -lint "$app/Contents/Info.plist"
test -x "$app/Contents/MacOS/winmtr"
test "$(lipo -archs "$app/Contents/MacOS/winmtr")" = "$arch"
for native in libAvaloniaNative.dylib libSkiaSharp.dylib libHarfBuzzSharp.dylib; do
  test -s "$app/Contents/MacOS/$native"
  # The file comes first: -verify_arch reads every argument after it as an
  # architecture name, so a trailing path is rejected as an unknown flag.
  lipo "$app/Contents/MacOS/$native" -verify_arch "$arch"
done

scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT
iconutil --convert iconset --output "$scratch/decoded.iconset" \
  "$app/Contents/Resources/winmtr-route-pulse.icns"
test -s "$scratch/decoded.iconset/icon_512x512@2x.png"
for image in "$scratch/decoded.iconset/"*.png; do
  sips -s format png "$image" --out "$scratch/decoded.png" > /dev/null
  test -s "$scratch/decoded.png"
done

verify_signature() {
  codesign --verify --strict --verbose=2 "$1"
  codesign --display --verbose=4 "$1" 2>&1 | grep -Fx 'Signature=adhoc'
  # An empty entitlements blob is expected for Native AOT. Do not preserve
  # entitlements (or other signature metadata) from any input signature.
  codesign --display --entitlements :- "$1" > "$scratch/entitlements"
  test ! -s "$scratch/entitlements"
}

# Sign every nested Mach-O, including the apphost, before sealing the bundle.
# Do not use --deep to sign: it can conceal an incomplete inside-out pass.
while IFS= read -r -d '' file; do
  if file -b "$file" | grep -q 'Mach-O'; then
    codesign --force --sign - --timestamp=none "$file"
    verify_signature "$file"
  fi
done < <(find "$app/Contents" -depth -type f -print0)
codesign --force --sign - --timestamp=none "$app"
verify_signature "$app"
codesign --verify --deep --strict --verbose=2 "$app"
