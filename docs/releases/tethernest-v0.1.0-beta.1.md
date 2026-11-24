# TetherNest Rev 0.1 — early beta

Frozen first Windows prerelease: **0.1.0-beta.1**, tag **tethernest-v0.1.0-beta.1**. The existing Mac LAN Share history and source are preserved. Local development version labels were normalized to Rev 0.1 for this first public TetherNest checkpoint.

## Downloads

- **TetherNest-Rev0.1-win-arm64.zip** — Windows 11 ARM64 / Snapdragon.
- **TetherNest-Rev0.1-win-x64.zip** — Windows 11 x64 / Intel and AMD; tested under Windows on Arm emulation, not physical x64 hardware.
- **SHA256SUMS.txt** — checksums for both ZIPs.

Both ZIPs are self-contained and unsigned. Extract all files and open TetherNest.exe; no separate .NET installation is required.

## Included

- Manual Connect to iPhone: Bluetooth off/on, phone automation trigger, saved hotspot join, internet verification.
- One-time QR-based iPhone shortcut setup; the imported Bluetooth trigger must be explicitly enabled.
- Separate Windows Internet Connection Sharing from existing Wi-Fi to physical Ethernet.
- Independent Cloudflare ping, session cable traffic, rounded widget and Keep on top.
- Cancel handling, previous-Wi-Fi restoration attempts, durable Bluetooth intent, protected sharing recovery and external-change protection.

## Validation

- Self-contained ARM64 and x64 builds succeeded with zero warnings/errors.
- **61/61 automated logic checks passed in each architecture**: ARM64 natively, x64 under emulation. Sanitized logs are in docs/validation.
- Development hardware: Windows 11 ARM64 ZenBook, iPhone 17 Pro, USB Ethernet adapter and a Linux Ethernet client. User-operated tests passed for ordinary Wi-Fi sharing, iPhone-hotspot sharing, clean QR import after enabling its trigger, locked-phone Bluetooth automation and the full Connect to iPhone button.
- Earlier read-only native checks verified WLAN profiles, Bluetooth radio enumeration and interface-bound HTTPS verification. Widget layout/ping was inspected on ARM64.
- Published binary architecture and bundled runtime are checked during packaging. Packaged-app launch smoke results are recorded in docs/validation/rev0.1-packaging.md.

## Known limitations

This is an early beta, not general hardware certification. Physical x64 radio/ICS behavior, older iPhones/iOS, prolonged idle keepalive, and comprehensive crash/sleep/reboot recovery require more testing. The phone's exact iOS build was not recorded. Windows Bluetooth permissions and phone reconnect behavior vary.

The current phone trigger cycles all enabled Bluetooth radios and briefly disconnects peripherals. The supplied Any Device automation can also fire when other Bluetooth accessories connect. Importing a shortcut does not enable its trigger. Replace produced duplicate entries during testing; avoid multiple enabled copies.

Overlapping VPN and local subnets can break DNS; no automatic VPN changes are made. The ping is not proof of Ethernet-client internet or guaranteed hotspot keepalive. HTTPS verification uses Google's gstatic connectivity endpoint; firewall/endpoint blocking may produce a failed check.

Tray operation, launch-at-sign-in, automatic connection on opening, trusted-network fallback, Wi-Fi output, signed installers and automatic updates are **not included**. Rev 0.2 has not been started.

## Setup

See [Windows guide](../../windows/README.md) and [iPhone setup with QR](../setup/iphone-setup.md). Use the same Windows administrator account for sharing elevation. Keep recovery records if restoration cannot complete; do not delete them to bypass a conflict.
