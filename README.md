# TetherNest / LAN Share

**TetherNest Rev 0.1 for Windows is an early beta.** Native Windows ARM64 and x64 portable packages are published as a GitHub prerelease. See the [Windows guide](windows/README.md), [iPhone setup](docs/setup/iphone-setup.md), and [release notes](docs/releases/tethernest-v0.1.0-beta.1.md).

The original macOS LAN Share application and its history remain below. This checkpoint does not implement Rev 0.2 tray or automatic-connect features.

---
# LAN Share

LAN Share is a small macOS app that shares your Mac’s internet connection through another network adapter. It uses macOS Internet Sharing.

## Use it

1. Quit LAN Share if it’s running. Open `LAN Share.pkg` and authorize the installation. This installs the app and its background helper.
2. Open **LAN Share** from Applications. **Internet** shows your Mac’s current connection. If several sources are available, use the menu to choose one. Choose a different adapter under **Share to**.
3. Click **Start**. The helper runs sharing in the background. Open a website on the connected device to check that it’s online.
4. **Data transferred** combines bytes sent and received on the sharing adapter, including local network traffic on that adapter. It excludes the Mac’s traffic on the internet adapter and resets on each **Start**.
5. Click **Stop**, close the window, or quit to restore the previous Internet Sharing setup. If macOS was already sharing to the selected wired adapter from the selected source, LAN Share leaves that session running.

If your Mac joins the iPhone’s Personal Hotspot from the Wi‑Fi menu, choose **Wi‑Fi** as **Internet** and a wired adapter under **Share to**. To share the iPhone’s internet through your Mac’s Wi‑Fi, connect the iPhone by USB and choose **iPhone USB** as **Internet**. The same Wi‑Fi adapter can’t be selected at both ends. Available combinations depend on the adapters and macOS Internet Sharing.

Before sharing to Wi‑Fi for the first time, use **System Settings → General → Sharing → Internet Sharing** to save a network name and a WPA password. Leave Internet Sharing off after saving. LAN Share uses the credentials saved by macOS whenever you choose **Wi‑Fi** under **Share to**. You can change the name and password in System Settings between sessions. [Apple’s Internet Sharing instructions](https://support.apple.com/guide/mac-help/mchlp1540/mac)

The helper attempts restoration if the app crashes or is force quit. If recovery remains pending, reopen LAN Share and click **Restore settings**.

## Build locally

Run `./build.sh` with the Apple Command Line Tools and macOS 27 SDK installed. It creates `dist/LAN Share.pkg` with a universal app and helper for Apple silicon and Intel, targeting macOS 13 Ventura or later.

LAN Share uses Apple’s System Configuration APIs with the system’s Internet Sharing preference format. Before a public release, verify wired and Wi‑Fi client connectivity, hotspot authentication, the data counter, and settings restoration on the macOS versions and hardware the release will support. Set `LAN_SHARE_SIGNING_IDENTITY` to a Developer ID Application identity and `LAN_SHARE_INSTALLER_IDENTITY` to a Developer ID Installer identity when building for distribution. Submit the signed package to Apple for notarization before sharing it. [Apple’s distribution guide](https://developer.apple.com/macos/distribution/)

The rollback checks can be run with `./test.sh`. They use simulated settings so they can exercise failures without changing the Mac’s network.

## License

LAN Share is available under the [MIT License](LICENSE).
