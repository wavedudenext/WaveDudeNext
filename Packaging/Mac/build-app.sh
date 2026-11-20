#!/usr/bin/env bash
# Builds "WaveDude Next.app" (and a .dmg and .zip) of the Avalonia head, WaveDudeNext.App, for macOS. Run on a Mac:
#   Packaging/Mac/build-app.sh [arm64|x64|universal]       (default: this Mac's architecture)
# Environment:
#   BUNDLE_ID      CFBundleIdentifier   (default com.wavedudenext.app)
#   SIGN_IDENTITY  "Developer ID Application: Name (TEAMID)" to sign for distribution; otherwise the app is signed ad hoc
# Needs the .NET 10 SDK and Xcode's command line tools (codesign, lipo, hdiutil, ditto).
set -euo pipefail

ARCH="${1:-$(uname -m | sed 's/x86_64/x64/')}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
PROJECT="$ROOT/WaveDudeNext.App/WaveDudeNext.App.csproj"
EXECUTABLE="WaveDudeNext.App"   # the apphost carries the assembly's name
OUT="$ROOT/dist"
BUNDLE_ID="${BUNDLE_ID:-com.wavedudenext.app}"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
APP="$OUT/WaveDude Next.app"

is_macho() { [[ "$(file -b "$1")" == *Mach-O* ]]; }
step() { echo; echo "==> $*"; }
publish() { # runtime-id output-dir
	dotnet publish "$PROJECT" -c Release -r "$1" --self-contained true -o "$2" -p:DebugType=None -p:DebugSymbols=false
}

rm -rf "$OUT" && mkdir -p "$OUT"
step "dotnet publish ($ARCH)"
case "$ARCH" in
	arm64|x64)
		publish "osx-$ARCH" "$OUT/publish"
		PUBLISHED="$OUT/publish" ;;
	universal)
		publish osx-arm64 "$OUT/publish-arm64"
		publish osx-x64 "$OUT/publish-x64"
		PUBLISHED="$OUT/publish-universal"
		cp -R "$OUT/publish-arm64" "$PUBLISHED"
		# every native binary becomes a fat file of both builds; managed files are the same in both
		while IFS= read -r -d '' f; do
			rel="${f#"$OUT/publish-arm64/"}"
			other="$OUT/publish-x64/$rel"
			if [ -f "$other" ] && is_macho "$f" && is_macho "$other"; then
				# libraries that ship already universal are the same file in both builds, and lipo refuses to combine
				# two files that both hold the same architecture
				archs="$(lipo -archs "$f")"
				if cmp -s "$f" "$other" || { [[ "$archs" == *arm64* && "$archs" == *x86_64* ]]; }; then
					continue
				fi
				lipo -create "$f" "$other" -output "$PUBLISHED/$rel"
			fi
		done < <(find "$OUT/publish-arm64" -type f -print0) ;;
	*) echo "unknown architecture '$ARCH' (arm64, x64 or universal)" >&2; exit 2 ;;
esac
[ -f "$PUBLISHED/$EXECUTABLE" ] || { echo "ERROR: $EXECUTABLE was not published; found: $(ls "$PUBLISHED" | head -20)" >&2; exit 1; }

step "assembling the app bundle"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISHED/." "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/$EXECUTABLE"
sed -e "s/@VERSION@/$VERSION/g" -e "s/@BUNDLE_ID@/$BUNDLE_ID/g" -e "s/@EXECUTABLE@/$EXECUTABLE/g" "$HERE/Info.plist.template" > "$APP/Contents/Info.plist"
# the license travels with the program (GPL-3.0-or-later)
for f in LICENSE.md README.md; do [ -f "$ROOT/$f" ] && cp "$ROOT/$f" "$APP/Contents/Resources/"; done

# the icon: a PNG into an iconset (every size macOS asks for), else the ready-made .icns
if command -v iconutil >/dev/null && command -v sips >/dev/null; then
	ICONSET="$OUT/AppIcon.iconset"; mkdir -p "$ICONSET"
	for s in 16 32 128 256 512; do
		sips -z $s $s "$HERE/AppIcon.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
		sips -z $((s*2)) $((s*2)) "$HERE/AppIcon.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
	done
	iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns" || cp "$HERE/AppIcon.icns" "$APP/Contents/Resources/"
else
	cp "$HERE/AppIcon.icns" "$APP/Contents/Resources/"
fi

step "signing"
if [ -n "${SIGN_IDENTITY:-}" ]; then
	echo "Signing with: $SIGN_IDENTITY"
	find "$APP/Contents/MacOS" -type f -print0 | while IFS= read -r -d '' f; do
		is_macho "$f" && codesign --force --options runtime --timestamp --entitlements "$HERE/entitlements.plist" --sign "$SIGN_IDENTITY" "$f"
	done
	codesign --force --options runtime --timestamp --entitlements "$HERE/entitlements.plist" --sign "$SIGN_IDENTITY" "$APP"
else
	# ad hoc: runs on this Mac and, after "right-click > Open" once, on others; not notarised
	codesign --force --deep --sign - "$APP"
fi
if codesign --verify --deep "$APP"; then echo "signature ok"; else echo "WARNING: codesign could not verify the bundle (it was still built)"; fi

step "creating the disk image"
DMG="$OUT/WaveDudeNext-$VERSION-mac-$ARCH.dmg"
STAGE="$OUT/dmg"; rm -rf "$STAGE"; mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"; ln -s /Applications "$STAGE/Applications"
for attempt in 1 2 3; do
	hdiutil create -volname "WaveDude Next" -srcfolder "$STAGE" -ov -format UDZO "$DMG" && break
	echo "hdiutil failed (attempt $attempt), retrying"; sleep 5
done
[ -f "$DMG" ] || { echo "ERROR: the disk image was not created" >&2; exit 1; }
ditto -c -k --keepParent "$APP" "$OUT/WaveDudeNext-$VERSION-mac-$ARCH.zip"
echo "Built: $APP"; echo "       $DMG"
