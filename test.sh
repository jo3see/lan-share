#!/bin/bash
set -euo pipefail

cd -- "$(dirname -- "$0")"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/lan-share-tests.XXXXXXXX")"
trap 'rm -rf -- "$work_dir"' EXIT
mkdir -p .build/module-cache
xcrun swiftc -swift-version 5 -module-cache-path .build/module-cache \
    Sources/LanShare/Hotspot.swift Sources/LanShare/Network.swift Sources/LanShare/SharingSession.swift \
    Tests/SharingSessionChecks.swift -o "$work_dir/SharingSessionChecks"
"$work_dir/SharingSessionChecks"
