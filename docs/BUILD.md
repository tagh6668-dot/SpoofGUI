# Building SpoofGUI

This document describes the 1.0.4 release pipeline.

## What Gets Built

The release process produces four user-facing artifacts — one Setup installer and one Portable zip per architecture:

```text
dist\SpoofGUI-Setup-amd64.exe
dist\SpoofGUI-Setup-x86.exe
dist\SpoofGUI-Portable-amd64.zip
dist\SpoofGUI-Portable-x86.zip
```

> ARM64 is intentionally not shipped: the SNI spoof relies on the WinDivert kernel driver, which has no ARM64 build and cannot be loaded by an ARM64 Windows kernel. ARM64 users can run the amd64 build under x64 emulation for the userspace V2Ray modes, but the spoof feature will not work there.

### Portable layout (clean root)

The portable zip keeps its root clean — a single launcher plus the payload tucked away:

```text
SpoofGUI-Portable-amd64.zip
  SpoofGUI.exe        (tiny native launcher; this is what you run)
  app\                (everything else)
    SpoofGUI.exe      (the actual self-contained WinUI app)
    *.dll ...         (.NET runtime + Windows App SDK)
    Xray\xray.exe
    engine\WinDivert.dll, WinDivert64.sys, sing-box.exe, wintun.dll
```

The root `SpoofGUI.exe` is a `requireAdministrator` launcher (`launcher\launcher.cpp`). It elevates once, then starts `app\SpoofGUI.exe`, so the real app sees an admin token without a second relaunch. The Setup installer uses the same layout under the install directory.

Each payload contains:

- The WinUI 3 frontend (self-contained, includes Windows App SDK + .NET runtime). The SNI-Spoof engine is **pure C#**, compiled into this app and run in-process — there is no separate engine executable.
- The WinDivert user DLL (`WinDivert.dll`) + driver (`WinDivert64.sys`) for the target architecture, committed under `app\SpoofGUI\Engine\`.
- `engine\sing-box.exe` ([SagerNet/sing-box](https://github.com/SagerNet/sing-box)) + `engine\wintun.dll` — used by Tunnel Mode.
- `Xray\xray.exe` ([XTLS/Xray-core](https://github.com/XTLS/Xray-core)) — used by Proxy / System Proxy modes.

`xray.exe`, `sing-box.exe`, and `wintun.dll` are not committed; the build fetches the architecture-correct binaries.

Target PCs should not need Python, .NET, or Windows App Runtime installed separately.

## Build Prerequisites

Required on the build machine only:

- .NET 10 SDK. (No Python — the SNI engine is pure C# and compiles with the app.)
- Visual Studio C++ build tools (for the native launcher — `cl.exe` / `rc.exe`, located via `vswhere`).
- Inno Setup 6 with `ISCC.exe` at the default install path.
- Internet access to fetch the cores and restore packages.

## One-Command Release

From the repository root:

```bat
build-release.bat
```

This thin wrapper calls `scripts\build-release.ps1`, which by default builds both `x86` and `amd64`. To build a single architecture:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Arch amd64
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Arch x86
```

Per architecture, the orchestrator:

1. Aborts if `SpoofGUI.exe` is running.
2. Verifies the committed WinDivert files (`WinDivert.dll` + `WinDivert64.sys`) are present under `app\SpoofGUI\Engine`. The SNI engine itself is pure C# and is compiled by the publish step below.
3. Fetches the architecture-correct `xray.exe`, `sing-box.exe`, and `wintun.dll`.
4. Publishes the WinUI app self-contained for the target RID (`win-x64` / `win-x86`) to `dist\publish-<arch>`.
5. Compiles the native launcher for the target architecture and assembles the clean root layout under `dist\stage\<arch>`.
6. Zips the staged layout into `dist\SpoofGUI-Portable-<arch>.zip` and packs it into `dist\SpoofGUI-Setup-<arch>.exe` with Inno Setup.

> Building both architectures in one run overwrites the binaries under `app\SpoofGUI\Xray` with each architecture in turn; the default order ends on `amd64` so the working tree matches the committed binaries.

## Manual Frontend Publish

```powershell
dotnet publish app\SpoofGUI\SpoofGUI.csproj `
  -c Release `
  -p:Platform=x64 `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:PublishTrimmed=false `
  -o dist\publish-amd64
```

`PublishSingleFile=false` is intentional. WinUI 3 unpackaged apps need XAML, PRI, native runtime, and Windows App SDK files laid out beside the executable.

## Inno Setup Packages

The installer script lives in `../installer/`.

| Script | Output | Notes |
| --- | --- | --- |
| `SpoofGUI.iss` | `SpoofGUI-Setup-<arch>.exe` | Installs the clean root layout to Program Files, creates shortcuts to the launcher, dark-mode installer, requests admin. Architecture and output name come from `SPOOFGUI_ARCH` / `SPOOFGUI_STAGE_DIR`. |

The portable build is a plain `.zip` (no installer) produced by `Compress-Archive`.

## CI

`.github/workflows/release.yml` runs a matrix over `amd64` and `x86` on `windows-latest`, builds each architecture with `build-release.bat -Arch <arch>`, and a final job collects all four artifacts into a draft GitHub Release.

## Runtime Notes

- The launcher elevates first; `Program.cs` still relaunches the inner app elevated if it is ever started directly without admin. Without elevation, WinDivert cannot open its kernel driver.
- The first launch creates `%LOCALAPPDATA%\SpoofGUI\spoofgui.db` (SQLite — profiles, V2Ray configs, settings).
- The default SNI listener is `127.0.0.1:40443`.
- Default proxy inbounds when V2Ray is connected: SOCKS `127.0.0.1:20882`, HTTP `127.0.0.1:20883` (both configurable on the Settings page).
- "System Proxy" mode rewrites `HKCU\...\Internet Settings\ProxyServer` and calls `InternetSetOption(INTERNET_OPTION_PER_CONNECTION_OPTION)` so WinINet apps pick up the change immediately. It is reverted on disconnect.
- Closing the window minimizes to the system tray (Win32 `Shell_NotifyIcon` + a borderless WinUI flyout); engines keep running. Full teardown happens only via tray **Quit** or a real window close.
- Kill switch (Settings, off by default): while a connected core is armed and it drops, a `netsh advfirewall` block-all-outbound rule is added and removed on disconnect/exit.
- In-app update downloads `SpoofGUI-Setup-<arch>.exe` from the latest GitHub release to `%TEMP%` and launches it.
- The update channel points to [ZethRise/SpoofGUI](https://github.com/ZethRise/SpoofGUI).

## Backend Summary

The shipping app runs the SNI engine in-process and spawns one proxy core depending on the active mode:

| Component | Source | Role |
| --- | --- | --- |
| SNI-Spoof engine (in-process) | `app/SpoofGUI/Engine/SniSpoofEngine.cs` (C#) | Pure-C# listener + relay + WinDivert `wrong_seq` capture/inject state machine on port 40443; no separate process or config file. Ported from patterniha/SNI-Spoofing; resilient relay (a dropped connection never kills the engine) + Fast Mode low-latency sockets adapted from atarevals/SNI-Spoofing. |
| `xray.exe` | fetched ([XTLS/Xray-core](https://github.com/XTLS/Xray-core)) | Proxy / System Proxy modes. Started by `XrayCoreService` after generating `%LOCALAPPDATA%\SpoofGUI\xray-client.json` (SOCKS/HTTP inbounds). |
| `sing-box.exe` | fetched ([SagerNet/sing-box](https://github.com/SagerNet/sing-box)) | Tunnel Mode only. Started by `SingBoxTunnelService` as a full core: tun inbound (`auto_route` + `strict_route`) + the profile's proxy outbound. `auto_detect_interface` keeps the server dial off the tunnel. |
