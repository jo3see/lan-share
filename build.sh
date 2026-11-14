#!/bin/bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
output_dir="$project_dir/dist"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/lan-share.XXXXXXXX")"
trap 'rm -rf -- "$work_dir"' EXIT
bundle="$work_dir/LAN Share.app"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources" "$output_dir"
cache_dir="$project_dir/.build/module-cache"
mkdir -p "$cache_dir"
cp "$project_dir/Resources/Info.plist" "$bundle/Contents/Info.plist"

sdk="$(xcrun --sdk macosx --show-sdk-path)"
minimum_macos="$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$bundle/Contents/Info.plist")"
executable="$bundle/Contents/MacOS/LanShare"
helper_id=local.lanshare.macos.helper
helper="$work_dir/$helper_id"
sources="$project_dir/Sources/LanShare"
for architecture in arm64 x86_64; do
    xcrun swiftc -O -swift-version 5 -target "$architecture-apple-macosx$minimum_macos" \
        -sdk "$sdk" -module-cache-path "$cache_dir" \
        -o "$work_dir/LanShare-$architecture" "$sources"/*.swift
    xcrun swiftc -O -swift-version 5 -target "$architecture-apple-macosx$minimum_macos" \
        -sdk "$sdk" -module-cache-path "$cache_dir" -o "$work_dir/helper-$architecture" \
        "$project_dir/Sources/LanShareHelper/HelperMain.swift" \
        "$sources"/{HelperConnection,Hotspot,HotspotPreferences,Network,SharingPreferences,SharingSession}.swift
done
xcrun lipo -create "$work_dir/LanShare-arm64" "$work_dir/LanShare-x86_64" -output "$executable"
xcrun lipo -create "$work_dir/helper-arm64" "$work_dir/helper-x86_64" -output "$helper"
for binary in "$executable" "$helper"; do
    xcrun lipo "$binary" -verify_arch arm64
    xcrun lipo "$binary" -verify_arch x86_64
done
signing_identity="${LAN_SHARE_SIGNING_IDENTITY:--}"
signing_options=(--force --options runtime --sign "$signing_identity")
if [[ "$signing_identity" != "-" ]]; then
    signing_options+=(--timestamp)
fi
write_requirement() {
    codesign -dr - "$1" 2>&1 | sed -n 's/^#\{0,1\} *designated => //p' > "$2"
    test -s "$2"
}
codesign "${signing_options[@]}" --identifier "$helper_id" "$helper"
write_requirement "$helper" "$bundle/Contents/Resources/SharingHelper.requirement"
codesign "${signing_options[@]}" "$bundle"
codesign --verify --strict --all-architectures "$bundle" "$helper"

root="$work_dir/payload"
scripts="$work_dir/scripts"
mkdir -p "$root/Applications" "$root/Library/LaunchDaemons" "$root/Library/PrivilegedHelperTools" "$scripts"
ditto "$bundle" "$root/Applications/LAN Share.app"
install -m 755 "$helper" "$root/Library/PrivilegedHelperTools/$helper_id"
write_requirement "$bundle" "$root/Library/PrivilegedHelperTools/$helper_id.client-requirement"
install -m 644 "$project_dir/Resources/Installer/$helper_id.plist" "$root/Library/LaunchDaemons"
install -m 755 "$project_dir/Resources/Installer/postinstall" "$scripts"
cat > "$work_dir/components.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><array><dict>
    <key>RootRelativeBundlePath</key><string>Applications/LAN Share.app</string>
    <key>BundleIsRelocatable</key><false/>
    <key>BundleIsVersionChecked</key><true/>
    <key>BundleOverwriteAction</key><string>upgrade</string>
    <key>BundleHasStrictIdentifier</key><true/>
</dict></array></plist>
PLIST
package_options=(--identifier local.lanshare.macos.installer)
if [[ -n "${LAN_SHARE_INSTALLER_IDENTITY:-}" ]]; then
    if [[ "$signing_identity" == "-" ]]; then
        printf '%s\n' 'Set LAN_SHARE_SIGNING_IDENTITY to sign the app and helper before signing the installer.' >&2
        exit 1
    fi
    package_options+=(--sign "$LAN_SHARE_INSTALLER_IDENTITY")
fi
version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$bundle/Contents/Info.plist")"
COPYFILE_DISABLE=1 /usr/bin/pkgbuild --root "$root" --component-plist "$work_dir/components.plist" \
    --scripts "$scripts" --ownership recommended --install-location / \
    --version "$version" \
    "${package_options[@]}" "$work_dir/LAN Share.pkg"
ditto "$bundle" "$output_dir/LAN Share.app"
mv -f "$work_dir/LAN Share.pkg" "$output_dir/LAN Share.pkg"
printf '%s\n' "$output_dir/LAN Share.pkg"
