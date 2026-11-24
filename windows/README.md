# TetherNest for Windows — Rev 0.1 (early beta)

TetherNest connects a Windows laptop to a configured iPhone hotspot and can separately share an existing Wi-Fi connection through Ethernet. This is the frozen first beta, internally versioned 0.1.0-beta.1. Earlier local development labels 0.2.x/0.3.x were iteration numbers, not public TetherNest revisions.

## Download and open

Download the ARM64 ZIP for Snapdragon/Windows on Arm, or x64 ZIP for Intel/AMD Windows 11, from the repository's TetherNest prerelease. Extract the entire ZIP, then open **TetherNest.exe**. Both packages include their .NET runtime; keep all extracted files together. These are unsigned portable builds, not installers.

The window starts ping monitoring only. It does not change Wi-Fi, restart Bluetooth, or enable sharing at launch. System tray operation, sign-in launch and automatic connection management are not included in Rev 0.1.

## Connect an iPhone

1. Follow [iPhone setup](../docs/setup/iphone-setup.md). This requires one-time pairing, a saved hotspot profile, and enabling the imported phone automation.
2. Open **iPhone setup** in the widget and select your phone's saved Wi-Fi profile. Confirm the setup checkbox and save.
3. Click **Connect to iPhone**. Enabled Bluetooth radios restart off/on, briefly disconnecting all Bluetooth peripherals. This triggers the configured iPhone shortcut. TetherNest waits, joins the selected hotspot, then verifies internet access.
4. Success shows **Connected to iPhone**. A healthy existing phone connection avoids restarting Bluetooth.

**Cancel connection** restores Bluetooth and attempts to return to the previous Wi-Fi profile if TetherNest had begun switching. It does not replace a different Wi-Fi selection made externally. The button does not start Ethernet sharing. After a crash during the radio-off interval, use **Restore Bluetooth** on reopening, or turn Bluetooth on in Windows Settings. Do not delete a pending recovery file.

## Share through Ethernet

Connect a separate physical Ethernet adapter and cable to the client device. The adapter must initially use automatic IP and DNS with no gateway. Select the existing **Internet from** connection and cable adapter, then click **Start sharing** and approve Windows elevation if prompted. Verify browsing on the client; the laptop's ping alone does not prove client connectivity.

**Stop sharing** removes owned sharing and restores automatic cable settings. An exactly matching pre-existing sharing session is observed and left running. Conflicting sharing or externally changed settings are protected and may require manual review. Closing the widget attempts to stop its owned sharing. Recovery journals survive a helper crash; open the app and use **Restore settings**.

## Status and privacy

- Ping targets Cloudflare 1.1.1.1 approximately every three seconds; **Pause ping** stops it. It follows the laptop's route and can be affected by a VPN. It is not a guaranteed iPhone keepalive.
- Connection verification uses HTTPS to `https://www.gstatic.com/generate_204`, bound to the selected Wi-Fi interface. A blocked endpoint can cause verification to fail even if some internet services work.
- Cable traffic is received plus sent bytes on that adapter, including local traffic, not a cellular billing estimate.
- TetherNest uses saved Windows Wi-Fi profiles; it does not read or store their passwords.
- Per-user profile selection and Bluetooth recovery live under `%LOCALAPPDATA%\TetherNest`. Protected sharing recovery lives under `%ProgramData%\TetherNest`.

## Limitations and validation

Read the [Rev 0.1 release notes](../docs/releases/tethernest-v0.1.0-beta.1.md). VPN/local subnet overlap caused DNS failure during testing; disconnecting the conflicting VPN resolved it. Automatic VPN management is not implemented. Phone reconnect, Windows radio permission and a working phone automation are prerequisites. Older phones/iOS versions, physical Intel/AMD hardware and extended crash/sleep/reboot scenarios are not certified.

## Build from source

Requires .NET 10 SDK. From `windows`:

```powershell
.\build.ps1 -RuntimeIdentifier win-arm64 -SelfContained -OutputDirectory .\releases\arm64
.\build.ps1 -RuntimeIdentifier win-x64 -SelfContained -OutputDirectory .\releases\x64
```

Run the corresponding test executable under `tests/TetherNest.Tests/bin/Release/net10.0-windows10.0.19041.0/<RID>/`. No arguments runs simulated logic tests. `--phone-inspect` additionally performs read-only WLAN/radio checks and an HTTPS request; it does not cycle Bluetooth or join a network. An optional first argument ending in `.png` renders the live widget and tests idle controls, starting its normal ping monitoring.

The macOS LAN Share source and history remain unchanged. There is no new Mac binary in this Windows prerelease.
