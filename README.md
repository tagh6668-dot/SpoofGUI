# SpoofGUI

SpoofGUI is a native Windows GUI fork for [patterniha/SNI-Spoofing](https://github.com/patterniha/SNI-Spoofing), maintained at [ZethRise/SpoofGUI](https://github.com/ZethRise/SpoofGUI).

It is not a VPN. SpoofGUI runs the local SNI-Spoofing listener as administrator and bundles Xray (Proxy / System Proxy) and sing-box (Tunnel) for outbound. Point your client at:

```text
127.0.0.1:40443    (SNI-Spoofing listener)
127.0.0.1:20882    (Xray SOCKS5, when V2Ray is connected)
127.0.0.1:20883    (Xray HTTP,    when V2Ray is connected)
```

SOCKS / HTTP ports default to 20882 / 20883 and are configurable on the Settings page.

<img width="1264" height="802" alt="image" src="https://github.com/user-attachments/assets/4266a7de-bc91-484a-9efc-5b2e6ca03e78" /> 



## Current Status

- App version: `1.0.1`
- Frontend: C# / .NET 10 / WinUI 3
- Backends:
  - **Python SNI-Spoofing engine** (`engine\SpoofGUI.SniSpoofEngine.exe`) — bundled PyInstaller build of the vendored upstream tool; performs the WinDivert + fake ClientHello injection
  - **C# Xray manager** — generates config and starts `xray.exe` for Proxy / System Proxy modes
  - **C# sing-box manager** — runs `sing-box.exe` as a full core (tun inbound + proxy outbound) for Tunnel mode
- Core binaries (`xray.exe`, `sing-box.exe`) are not committed; the release build fetches them automatically
- Runtime package: self-contained Windows build, no Python / Xray / sing-box / .NET install needed on target PCs
- Elevation: requested automatically on launch (the app relaunches itself as administrator); packet manipulation needs admin rights

## Features

- Start / stop SNI-Spoof listener with live connection count
- Edit active profile: listen host, listen port, connect IP, connect port, fake SNI
- V2Ray page:
  - Import `vless://`, `vmess://`, `trojan://`, `ss://`, or raw configs
  - Three modes per profile:
    - **Proxy Mode** — local SOCKS5 / HTTP only; point apps at the ports manually
    - **Tunnel Mode** — `sing-box` full core routes all OS traffic via a wintun adapter; `auto_route` keeps the server connection off the tunnel (no loop). Replaces the old tun2socks path that wiped network adapters
    - **System Proxy** — flips Windows Internet Settings to route HTTP/HTTPS apps through xray's HTTP inbound; reverted on disconnect
  - Real-time uptime, download / upload rate, total bytes
- Configurable SOCKS / HTTP proxy ports
- Logs page with copy + clear (full engine / core output captured)
- Dark / light themes, GitHub-based update check

## Layout

| Path | Purpose |
| --- | --- |
| [app/SpoofGUI/](app/SpoofGUI/) | WinUI app: UI, database, engine supervisor, system-proxy helper |
| [app/SpoofGUI/EngineSource/](app/SpoofGUI/EngineSource/) | Vendored Python SNI-Spoofing source (tracked directly), built into the bundled engine exe |
| [installer/](installer/) | Inno Setup scripts |
| [scripts/](scripts/) | Build helper scripts (PyInstaller wrapper) |
| [docs/](docs/) | Build, product, and design documentation |

## Build Release

Close any running `SpoofGUI.exe`, then run:

```bat
build-release.bat
```

Outputs:

```text
dist\SpoofGUI-Portable.exe
dist\SpoofGUI-Setup.exe
```

The script needs `python`, `dotnet`, Inno Setup 6, and internet access (it fetches `xray.exe` + `sing-box.exe` on first run). See [docs/BUILD.md](docs/BUILD.md) for full notes.

## Documentation

- [Build guide](docs/BUILD.md)
- [Design tokens](docs/DESIGN.md)
- [Product brief](docs/PRODUCT.md)

## License

GPL-3.0. SpoofGUI inherits licensing from upstream SNI-Spoofing.
