# Building SpoofGUI

This document describes the current 1.0.1 release pipeline.

## What Gets Built

The release process produces two user-facing EXE files:

```text
dist\SpoofGUI-Portable.exe
dist\SpoofGUI-Setup.exe
```

Both contain:

- The WinUI 3 frontend (self-contained, includes Windows App SDK + .NET runtime).
- The Python SNI-Spoof engine: `engine\SpoofGUI.SniSpoofEngine.exe` (PyInstaller --onefile bundle of the upstream tool).
- `engine\WinDivert.dll`, `engine\WinDivert64.dll`, and `engine\WinDivert64.sys` for the Python SNI-Spoofing engine.
- `engine\sing-box.exe` ([SagerNet/sing-box](https://github.com/SagerNet/sing-box)) + `engine\wintun.dll` — used by Tunnel Mode.
- `Xray\xray.exe` ([XTLS/Xray-core](https://github.com/XTLS/Xray-core)) — used by Proxy / System Proxy modes.
- A copy of the SNI-Spoofing Python source under `source\SNI-Spoofing\` (for licensing transparency).

`xray.exe` and `sing-box.exe` are not committed to the repo; the build fetches them on first run (see step 5).

Target PCs should not need Python, .NET, or Windows App Runtime installed separately. The app still requires administrator elevation at runtime (`PrivilegesRequired=admin` in the installer). Because the self-contained WinUI build does not reliably honor the embedded `requireAdministrator` manifest, elevation is enforced in `Program.cs`: a custom `Main` checks for admin before any WinUI bootstrap and relaunches itself elevated if needed.

## Build Prerequisites

Required on the build machine only:

- Python 3.10+ with `pip` and PyInstaller (the script installs `app\SpoofGUI\EngineSource\requirements.txt` automatically).
- .NET 10 SDK.
- Inno Setup 6.7 or newer, with `ISCC.exe` at the default install path.
- Internet access the first time .NET / pip packages are restored.

## One-Command Release

From the repository root:

```bat
build-release.bat
```

The script performs these steps:

1. Aborts if `SpoofGUI.exe` is already running (Windows would lock the output EXE).
2. Verifies `python`, `dotnet`, and Inno Setup are reachable.
3. Cleans `dist\`.
4. Builds the **Python SNI-Spoof engine** via PyInstaller → `app\SpoofGUI\Engine\SpoofGUI.SniSpoofEngine.exe`.
5. Fetches the cores if missing: `xray.exe` (latest [XTLS/Xray-core](https://github.com/XTLS/Xray-core) `Xray-windows-64.zip`) into `app\SpoofGUI\Xray\`, and `sing-box.exe` (pinned [SagerNet/sing-box](https://github.com/SagerNet/sing-box) release) into `app\SpoofGUI\Engine\`. Copies `wintun.dll` next to sing-box.
6. Publishes the WinUI app self-contained to `dist\publish`.
7. Copies the SNI-Spoofing Python source into `dist\publish\source\SNI-Spoofing` for transparency.
8. Compiles the portable extractor and setup installer with Inno Setup.

## Manual Backend Builds

### Python engine

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-python-engine.ps1
```

Output: `app\SpoofGUI\Engine\SpoofGUI.SniSpoofEngine.exe`

## Manual Frontend Publish

```powershell
dotnet publish app\SpoofGUI\SpoofGUI.csproj `
  -c Release `
  -p:Platform=x64 `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:PublishTrimmed=false `
  -o dist\publish
```

`PublishSingleFile=false` is intentional. WinUI 3 unpackaged apps need XAML, PRI, native runtime, and Windows App SDK files laid out beside the executable.

## Inno Setup Packages

The installer scripts live in `../installer/`.

| Script | Output | Notes |
| --- | --- | --- |
| `SpoofGUI.iss` | `SpoofGUI-Setup.exe` | Installs to Program Files, creates shortcuts, dark-mode installer, requests admin. |
| `SpoofGUI.Portable.iss` | `SpoofGUI-Portable.exe` | Self-extracting portable folder, dark-mode extractor, requests admin. |

Both scripts launch SpoofGUI with the elevated setup token, avoiding Windows error code `740`.

## Runtime Notes

- `Program.cs` relaunches the app elevated if it is not already admin. Without elevation, WinDivert cannot install / open its kernel driver.
- The first launch creates `%LOCALAPPDATA%\SpoofGUI\spoofgui.db` (SQLite — profiles, V2Ray configs, settings).
- The default SNI listener is `127.0.0.1:40443`.
- Default proxy inbounds when V2Ray is connected: SOCKS `127.0.0.1:20882`, HTTP `127.0.0.1:20883` (both configurable on the Settings page).
- "System Proxy" mode rewrites `HKCU\...\Internet Settings\ProxyServer` and calls `InternetSetOption(INTERNET_OPTION_PER_CONNECTION_OPTION)` so WinINet apps (Edge, Chrome non-system, .NET, etc.) pick up the change immediately. It is reverted on disconnect.
- The update channel points to [ZethRise/SpoofGUI](https://github.com/ZethRise/SpoofGUI).

## Backend Summary

The shipping app spawns the SNI engine plus one proxy core depending on the active mode:

| Process | Source | Role |
| --- | --- | --- |
| `SpoofGUI.SniSpoofEngine.exe` | `app/SpoofGUI/EngineSource/` (vendored Python) | Reads `engine\config.json`, runs the WinDivert + fake-ClientHello listener on port 40443. |
| `xray.exe` | fetched ([XTLS/Xray-core](https://github.com/XTLS/Xray-core)) | Proxy / System Proxy modes. Started by `XrayCoreService` after generating `%LOCALAPPDATA%\SpoofGUI\xray-client.json` (SOCKS/HTTP inbounds). |
| `sing-box.exe` | fetched ([SagerNet/sing-box](https://github.com/SagerNet/sing-box)) | Tunnel Mode only. Started by `SingBoxTunnelService` as a full core: tun inbound (`auto_route` + `strict_route`) + the profile's proxy outbound. `auto_detect_interface` keeps the server dial off the tunnel. Replaces the old tun2socks path. |
