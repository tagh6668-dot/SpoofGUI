---
register: product
---

# SpoofGUI Product Brief

## Purpose

SpoofGUI is a native Windows GUI fork for SNI-Spoofing. It runs a local TCP listener that injects a fake ClientHello with an out-of-window TCP sequence number, so a DPI middlebox sees an allowed hostname while the user's real traffic continues over the same socket to their proxy provider.

SpoofGUI is not a VPN. The normal workflow:

1. Launch SpoofGUI. It relaunches itself as administrator automatically (UAC prompt).
2. Open the Main page and press **start** to run the in-process C# SNI-Spoof engine bound to `127.0.0.1:40443`.
3. (Optional) Open the SNI Scanner page, scan a list of hostnames, and create a Configs profile from a Cloudflare-backed result. On Configs, keep up to 100 SNI profiles and pick the active one; **fetch from repo** bulk-imports a curated `sni.json` list.
4. Open the V2Ray page, import one or many configs at once (VLESS / VMess / Trojan / Shadowsocks / raw).
5. Pick the **connection mode** — it is global and applies to every config:
   - **Proxy** — user points clients at SOCKS `127.0.0.1:20882` / HTTP `127.0.0.1:20883` manually (ports configurable in Settings). Runs `xray.exe`.
   - **Tunnel** — runs `sing-box.exe` as a full core: a tun (wintun) inbound with `auto_route` captures OS traffic, and the profile's proxy outbound carries it. `auto_detect_interface` keeps the connection to the proxy server off the tunnel (no loop), and private / LAN ranges bypass the tunnel so LAN sharing keeps working. sing-box installs and tears down its own routes; on disconnect it is stopped gracefully.
   - **System Proxy** — runs `xray.exe` and flips the Windows Internet Settings to route the whole system through the HTTP inbound; on stop, it reverts.
6. (Optional) Press **ping** to measure the real delay of the selected config (HTTP request routed through it).
7. Press **connect**. Proxy / System Proxy start `xray.exe`; Tunnel starts `sing-box.exe`.

## Users

Primary user: Windows power users and developers who already understand SNI, IP addresses, ports, and Xray/V2Ray configs, but want a clean GUI instead of repeatedly editing config files and running command-line tools manually.

The app is designed for constrained networks where reliability and clarity matter more than decoration.

## Core Jobs

- Start and stop the SNI-Spoofing listener; show live connection count.
- Manage up to 100 SNI profiles (listen host/port, connect IP, connect port, fake SNI); one is active at a time. Fetch a curated `sni.json` from the repo to bulk-create profiles.
- Bulk-scan hostnames to find Cloudflare-backed Fake SNI targets — from pasted text, the saved Configs profiles, and/or a bundled ~640-host Cloudflare candidate list. Create a profile from a result, or auto-pick the lowest-latency result straight into the active profile.
- Import (one or many at once), edit, delete, and run VLESS / VMess / Trojan / Shadowsocks profiles through Xray (Proxy / System Proxy) or sing-box (Tunnel).
- Set one global connection mode (Proxy / Tunnel / System Proxy) applied to every config.
- Test the real delay of a config (HTTP request routed through it).
- Configure the local SOCKS / HTTP proxy ports, DNS servers (remote / direct / bootstrap + strategy, used in Tunnel mode), Fast mode (low-latency engine sockets), allow-insecure-TLS, and xray log level.
- Show real-time upload / download rate and total bytes on the V2Ray page; multi-select configs to delete many at once.
- Run from the system tray: closing the window minimizes to the tray (engines keep running); a designed flyout shows live status/stats, a profile switcher, and quick connect/disconnect for the SNI engine and V2Ray. Quit from the tray stops everything.
- Kill switch: if a connected core drops unexpectedly, block all outbound traffic until the user disconnects (cleared on disconnect/exit).
- Live Connections page: real-time TCP sessions on the SNI listener and the local proxy ports.
- Back up and restore all SNI + V2Ray profiles as a single JSON file.
- Check for updates against the GitHub releases channel and download & install the matching Setup in-app.
- Show runtime logs and make them easy to copy.
- Package the tool so end users do not need to install .NET, Xray, or Windows App Runtime.

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
