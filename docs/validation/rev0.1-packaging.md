# Release packaging validation

Date: 2026-11-24.

- Self-contained .NET 10 Windows builds for win-arm64 and win-x64 completed with zero warnings/errors.
- PE machine headers: ARM64 0xAA64; x64 0x8664. Both include coreclr.dll and Windows Desktop runtime assemblies.
- 61 simulated/nonmutating logic checks passed per architecture. x64 execution was Windows on Arm emulation, not physical Intel/AMD hardware.
- No sharing, Bluetooth reset or Wi-Fi switch is performed by the release smoke checks; opening the widget starts its normal ping monitoring.
- Runtime and Windows SDK projection dependencies are distributed by the .NET SDK publish pipeline. Application source remains MIT; bundled runtime components retain their upstream notices.

Both self-contained application executables opened a TetherNest window and closed normally with exit code zero on the ARM64 ZenBook; x64 ran under emulation and needed more than the initial two-second startup wait. These smoke checks did not click Connect or Start sharing. Bundled .NET runtime version: 10.0.13.
