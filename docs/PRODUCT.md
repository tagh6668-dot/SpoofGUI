---
register: product
---

# SpoofGUI Product Brief

## Purpose

SpoofGUI is a native Windows GUI fork for SNI-Spoofing. It runs a local TCP listener that injects a fake ClientHello with an out-of-window TCP sequence number, so a DPI middlebox sees an allowed hostname while the user's real traffic continues over the same socket to their proxy provider.

SpoofGUI is not a VPN. The normal workflow:

1. Launch SpoofGUI. It relaunches itself as administrator automatically (UAC prompt).
2. Open the Main page and press **start** to spawn the Python SNI-Spoof engine bound to `127.0.0.1:40443`.
3. Open the V2Ray page, import a config (VLESS / VMess / Trojan / Shadowsocks / raw).
4. Pick a mode for that profile:
   - **Proxy Mode** — user points clients at SOCKS `127.0.0.1:20882` / HTTP `127.0.0.1:20883` manually (ports configurable in Settings). Runs `xray.exe`.
   - **Tunnel Mode** — runs `sing-box.exe` as a full core: a tun (wintun) inbound with `auto_route` + `strict_route` captures all OS traffic, and the profile's proxy outbound carries it. `auto_detect_interface` keeps the connection to the proxy server off the tunnel (no loop). sing-box installs and tears down its own routes; on disconnect it is stopped gracefully.
   - **System Proxy** — runs `xray.exe` and flips the Windows Internet Settings to route the whole system through the HTTP inbound; on stop, it reverts.
5. Press **connect**. Proxy / System Proxy start `xray.exe`; Tunnel starts `sing-box.exe`.

## Users

Primary user: Windows power users and developers who already understand SNI, IP addresses, ports, and Xray/V2Ray configs, but want a clean GUI instead of repeatedly editing config files and running command-line tools manually.

The app is designed for constrained networks where reliability and clarity matter more than decoration.

## Core Jobs

- Start and stop the SNI-Spoofing listener; show live connection count.
- Edit the active SNI profile: listen host, listen port, connect IP, connect port, fake SNI.
- Import, edit, delete, and run VLESS / VMess / Trojan / Shadowsocks profiles through Xray (Proxy / System Proxy) or sing-box (Tunnel).
- Switch a profile between Proxy / Tunnel / System Proxy mode.
- Configure the local SOCKS / HTTP proxy ports.
- Show real-time upload / download rate and total bytes on the V2Ray page.
- Show runtime logs and make them easy to copy.
- Package the tool so end users do not need to install Python, .NET, Xray, or Windows App Runtime.

## Product Principles

1. **Be honest about admin.** The app needs elevation. This is not hidden.
2. **Do not pretend to be a VPN.** The UI says "Connect and use your X-Ray Client."
3. **Config is central.** Fake SNI and target IP settings are first-class UI, not buried preferences.
4. **No telemetry.** Local app state only. Update checks go to the GitHub releases channel.
5. **Logs matter.** Runtime failures should be visible and copyable.
6. **Ship self-contained.** Releases must work on a fresh Windows PC without installing runtimes.
7. **No silent state.** System proxy is only flipped by an explicit profile mode; it is reverted on disconnect.

## Anti-References

Avoid:

- Shield logos and VPN metaphors.
- Green/red consumer toggle UI.
- Animated globes.
- Cyberpunk neon visuals.
- Marketing copy.
- Hidden configuration.

## Release Channel

Updates point to:

[ZethRise/SpoofGUI](https://github.com/ZethRise/SpoofGUI)

The upstream project remains credited as:

[patterniha/SNI-Spoofing](https://github.com/patterniha/SNI-Spoofing)
