# NutAgent — Implementation Plan

## Goal

Replace the broken Windows NUT server with a proper .NET 8 Windows Service that speaks the standard NUT protocol. Optional WPF tray app provides status and settings UI. Home Assistant and Unraid use native NUT — NutAgent is Windows-only.

---

## Phase 1 — Service Scaffold ✅

Core service, NUT protocol, HID reader stub, basic shutdown.

- [x] `NutAgent.Service` project — Worker Service, win-x64 single exe
- [x] `AgentConfig` — typed config (mode, port, users, thresholds)
- [x] `UpsState` + `UpsStatus` flags + `ToNutString()`
- [x] `HidUpsReader` — HID Power Device class reader (HidSharp)
- [x] `NutServer` + `NutSession` — full NUT text protocol over TCP :3493
- [x] `NutClient` — polls remote NUT server, triggers local shutdown
- [x] `ShutdownManager` — `shutdown.exe /s /t 0 /f` with delay + cancel
- [x] `install.ps1` — service install, firewall, config patch
- [x] Bug fixes: `\r\n` → `\n`, usage casting, thread safety, culture-safe parse

---

## Phase 2 — Service Completion

Finish the service before building UI. Targets: runtime threshold, fake reader, pipe server, discharge tracking.

- [ ] **`FakeUpsReader`** — returns canned cycling data (OL → OB → LB → OL)
  - Auto-selected when no HID device found
  - Lets the NUT server be tested without a physical UPS

- [ ] **Runtime threshold** — shutdown when *either* fires:
  - Battery charge ≤ `ShutdownBatteryThreshold` (existing)
  - Estimated runtime ≤ `ShutdownRuntimeMinutes` (new)
  - Add `ShutdownRuntimeMinutes: 5` to `AgentConfig` + `appsettings.json`

- [ ] **`DischargeTracker`** — circular buffer, last 20 readings (100s at 5s poll)
  - Exposes `DischargeRatePerMinute` and `MinutesToEmpty`
  - v2 only collects data — Auto mode in Phase 4 uses it

- [ ] **`PipeServer`** — named pipe `\\.\pipe\nutagent`
  - JSON request/response (see IPC spec in CLAUDE.md)
  - Handles: `status`, `getConfig`, `setConfig`
  - Config writes hot-reload without service restart

- [ ] **Config hot-reload** — watch `appsettings.json` for changes, apply without restart
  - `IOptionsMonitor<AgentConfig>` + reload callback

- [ ] **`ShutdownMode` enum** — `Manual | Auto` added to `AgentConfig`
  - `Auto` is a no-op until Phase 4

- [ ] **`uninstall.ps1`** — stops service, removes it, removes firewall rule, removes tray from Run

---

## Phase 3 — WPF Tray App

Add `NutAgent.Shared` and `NutAgent.Tray` to the solution. Service must be complete first.

### 3a — Shared Library

- [ ] `NutAgent.Shared` project — class library, no dependencies
- [ ] `StatusResponse` — charge, runtimeSeconds, status, load, model, isConnected
- [ ] `ConfigResponse` — full config snapshot
- [ ] `ConfigUpdateRequest` — fields to change
- [ ] `ShutdownMode` enum — move here from Service, reference from both

### 3b — Tray App

- [ ] `NutAgent.Tray` project — WPF, `OutputType=WinExe`
  - NuGet: `Hardcodet.NotifyIcon.Wpf`
  - References `NutAgent.Shared`

- [ ] **`PipeClient`** — connects to `\\.\pipe\nutagent`, polls status every 5s

- [ ] **Tray icon** — four states driven by `StatusResponse`

  | State | Icon | Condition |
  |---|---|---|
  | Online | Green | `OL`, above thresholds |
  | On Battery | Yellow | `OB` |
  | Low / Imminent | Red | `LB` or below threshold |
  | Disconnected | Grey | Pipe not responding |

- [ ] **Right-click menu**
  ```
  ● Online — 85% — ~30 min
  ─────────────────────────
    Settings...
  ─────────────────────────
    Exit
  ```

- [ ] **Settings window**
  - Mode: Server / Client
  - UPS name, port (server) OR remote host, remote port, remote UPS (client)
  - Shutdown at: [__]% battery OR [__] minutes remaining
  - Shutdown delay: [__] seconds
  - Save → `setConfig` pipe call

- [ ] **`install.ps1` update** — add `-InstallTray` flag
  - Copies `NutAgent.Tray.exe` to `C:\NutAgent\`
  - Adds to `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

---

## Phase 4 — Auto Mode

Uses `DischargeTracker` data collected since Phase 2.

- [ ] `ShutdownMode.Auto` active in `ShutdownManager`
  - Uses `DischargeTracker.MinutesToEmpty - SafetyMarginMinutes` as dynamic threshold
  - Falls back to fixed threshold if fewer than 3 readings available
- [ ] `SafetyMarginMinutes` config field (default: 3)
- [ ] Settings window: expose Auto toggle + safety margin field

---

## Deployment Summary

```
PC1  →  Service (server)  →  reads UPS A via USB, NUT :3493
PC2  →  Service (client)  →  watches PC1:3493, own shutdown
PC3  →  Service (server)  →  reads UPS B via USB, NUT :3493
PC4  →  Service (server)  →  reads UPS C via USB, NUT :3493

Unraid  →  native NUT     →  reads UPS D, NUT :3493
HA      →  native NUT client → watches all 4 servers
```

Each machine: tray app optional, service mandatory. SCM restarts service on crash.
