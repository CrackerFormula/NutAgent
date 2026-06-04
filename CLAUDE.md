# NutAgent

## WHAT

**Stack:** .NET 8, C#, Windows x64  
**Projects:** three — Service (core), Tray (WPF UI), Shared (IPC types)

```
NutAgent/
├── NutAgent.sln
├── CLAUDE.md
├── install/
│   └── install.ps1               # installs service + registers tray at login
├── NutAgent.Service/             # Windows Service: HID + NUT protocol + shutdown + pipe server
│   ├── NutAgent.Service.csproj
│   ├── Program.cs
│   ├── Worker.cs
│   ├── appsettings.json
│   ├── Config/
│   │   └── AgentConfig.cs
│   ├── Hid/
│   │   ├── IUpsReader.cs
│   │   ├── HidUpsReader.cs       # USB HID Power Device reader (HidSharp)
│   │   ├── FakeUpsReader.cs      # canned data for dev/testing
│   │   └── UpsState.cs
│   ├── Nut/
│   │   ├── NutVariableMap.cs
│   │   ├── NutSession.cs
│   │   ├── NutServer.cs
│   │   └── NutClient.cs
│   ├── Shutdown/
│   │   └── ShutdownManager.cs    # charge % OR runtime minutes threshold
│   ├── Tracking/
│   │   └── DischargeTracker.cs   # circular buffer → discharge rate (v2 Auto mode)
│   └── Ipc/
│       └── PipeServer.cs         # named pipe \\.\pipe\nutagent
├── NutAgent.Tray/                # WPF tray app — optional UI layer
│   ├── NutAgent.Tray.csproj
│   ├── App.xaml / App.xaml.cs
│   ├── Ipc/
│   │   └── PipeClient.cs         # polls pipe every 5s for status
│   ├── ViewModels/
│   │   ├── TrayViewModel.cs      # icon state + menu text
│   │   └── SettingsViewModel.cs  # two-way config bindings
│   ├── Views/
│   │   └── SettingsWindow.xaml
│   └── Assets/
│       ├── icon-online.ico       # green
│       ├── icon-battery.ico      # yellow
│       ├── icon-low.ico          # red
│       └── icon-disconnected.ico # grey
└── NutAgent.Shared/              # IPC types — referenced by both Service and Tray
    ├── NutAgent.Shared.csproj
    ├── Ipc/
    │   ├── StatusResponse.cs
    │   ├── ConfigResponse.cs
    │   └── ConfigUpdateRequest.cs
    └── Enums/
        └── ShutdownMode.cs       # Manual | Auto
```

---

## WHY

The Windows port of NUT requires manual driver replacement via Zadig and breaks on Windows updates. NutAgent replaces it with a proper Windows service that speaks the standard NUT protocol — so Home Assistant, Unraid's native NUT client, and any other NUT tool work without modification.

The optional WPF tray app provides a settings UI. The service runs independently — closing the tray does not affect monitoring. The SCM acts as the watchdog: if the service crashes it restarts automatically.

### Deployment topology

```
UPS A (USB→ PC1) → NutAgent.Service --mode server :3493
         PC2     → NutAgent.Service --mode client → watches PC1

UPS B (USB→ PC3) → NutAgent.Service --mode server :3493
UPS C (USB→ PC4) → NutAgent.Service --mode server :3493

UPS D (USB→ Unraid) → native NUT server :3493
         HA          → native NUT integration → connects to all 4 servers
```

PC2 shares UPS A with PC1 but has no USB — it monitors PC1's NUT server and shuts itself down.  
Unraid + HA share UPS D — use native NUT, not NutAgent.

---

## HOW

### Build

```powershell
dotnet publish NutAgent.Service/NutAgent.Service.csproj -c Release
dotnet publish NutAgent.Tray/NutAgent.Tray.csproj -c Release
```

### Install (run as Administrator)

```powershell
# Server mode (PC1, PC3, PC4):
.\install\install.ps1 -Mode server

# Client mode (PC2):
.\install\install.ps1 -Mode client -RemoteHost 192.168.1.10

# With tray app:
.\install\install.ps1 -Mode server -InstallTray
```

The installer:
1. Copies `NutAgent.Service.exe` + `appsettings.json` to `C:\NutAgent\`
2. Registers service via `sc.exe`, sets failure restart actions
3. Optionally copies `NutAgent.Tray.exe`, adds to `HKCU\...\Run`
4. Opens firewall port 3493 (server mode only)

### Configure

Edit `C:\NutAgent\appsettings.json` — service hot-reloads config without restart.

| Field | Default | Notes |
|---|---|---|
| `Agent.Mode` | `Server` | `Server` or `Client` |
| `Agent.UpsName` | `ups` | NUT UPS name |
| `Agent.Port` | `3493` | NUT protocol port |
| `Agent.Users` | `[{admin/changeme}]` | Change password |
| `Agent.RemoteHost` | `""` | Client mode: server IP |
| `Agent.RemoteUpsName` | `ups` | Client mode: remote UPS name |
| `Agent.ShutdownBatteryThreshold` | `20` | Shutdown below this % |
| `Agent.ShutdownRuntimeMinutes` | `5` | Shutdown below this many minutes |
| `Agent.ShutdownDelaySeconds` | `60` | Grace period before shutdown |
| `Agent.ShutdownMode` | `Manual` | `Manual` or `Auto` (v4) |
| `Agent.SafetyMarginMinutes` | `3` | Auto mode safety buffer (v4) |
| `Agent.PollIntervalSeconds` | `30` | Client poll frequency |

Shutdown triggers on **whichever comes first**: charge % threshold OR runtime minutes threshold.

### Logs

Windows Event Viewer → Windows Logs → Application → Source: `NutAgent`

### Verify with HA

Add a NUT integration in HA pointing to `<PC_IP>:3493`. Variables `battery.charge`, `ups.status`, `battery.runtime` should populate immediately.

### Manual NUT protocol test

```bash
nc 192.168.1.x 3493
USERNAME admin
PASSWORD changeme
LIST UPS
GET VAR ups ups.status
GET VAR ups battery.charge
LOGOUT
```

---

## IPC — Named Pipe

Pipe name: `\\.\pipe\nutagent`  
Transport: JSON lines, one request → one response.

```
→ {"type":"status"}
← {"charge":85,"runtimeSeconds":1800,"status":"OL","load":42,"isConnected":true,"model":"Back-UPS 1500"}

→ {"type":"getConfig"}
← { ...full AgentConfig as JSON... }

→ {"type":"setConfig","shutdownBatteryThreshold":15,"shutdownRuntimeMinutes":3}
← {"ok":true}
```

Tray polls status every 5 seconds. Config writes immediately restart the relevant service components.

---

## Tray Icon States

| Icon | Colour | Condition |
|---|---|---|
| Online | Green | `OL`, charge above thresholds |
| On Battery | Yellow | `OB` |
| Low / Shutdown Imminent | Red | `LB`, or charge ≤ threshold, or runtime ≤ threshold |
| Disconnected | Grey | Pipe not responding (service down) |

**Right-click menu:**
```
● Online — 85% — ~30 min
─────────────────────────
  Settings...
─────────────────────────
  Exit
```

**Settings window:**
```
Mode:  ● Server  ○ Client

[Server]                        [Client]
UPS Name:  [ups      ]          Remote Host: [192.168.1.x]
Port:      [3493     ]          Remote Port: [3493       ]
                                Remote UPS:  [ups        ]

─────────────────────────────────────────
Shutdown when:
  Battery below   [20] %
  Runtime below   [ 5] minutes
  (whichever comes first)

Shutdown delay:   [60] seconds
─────────────────────────────────────────
                        [Save]  [Cancel]
```

---

## DischargeTracker

Circular buffer of the last 20 charge readings (100s at 5s poll interval).

```csharp
tracker.Record(charge: 85.0, timestamp: now);
double ratePerMinute    = tracker.DischargeRatePerMinute;  // e.g. 0.5%/min
double minutesToEmpty   = tracker.MinutesToEmpty;           // e.g. 170 min
```

**v1–v3:** populated continuously, not used for decisions.  
**v4 Auto mode:** if `ShutdownMode == Auto`, ShutdownManager uses `MinutesToEmpty - SafetyMarginMinutes` as the dynamic runtime threshold instead of the fixed `ShutdownRuntimeMinutes` value.

---

## Phase Roadmap

| Phase | Status | Scope |
|---|---|---|
| **1** | ✅ Done | Service scaffold, NUT protocol, HID reader stub, basic shutdown |
| **2** | 🔲 Next | `FakeUpsReader`, runtime threshold, `DischargeTracker`, `PipeServer`, config hot-reload |
| **3** | 🔲 | `NutAgent.Shared`, `NutAgent.Tray` — WPF tray icon + settings window |
| **4** | 🔲 | Auto mode — `DischargeTracker` drives dynamic shutdown threshold |

---

## HID Reader Notes (Phase 2)

`HidUpsReader.FindUpsDevice()` scans for a device with top-level Usage Page `0x84` (Power Device). If not found, `FakeUpsReader` is used automatically so the NUT server still responds.

**Usage constants to verify against real device:**

| Variable | Page | Usage | Combined |
|---|---|---|---|
| RemainingCapacity | 0x85 | 0x66 | `0x00850066` |
| RunTimeToEmpty | 0x85 | 0x68 | `0x00850068` |
| Battery Voltage | 0x85 | 0x30 | `0x00850030` |
| Config Voltage | 0x85 | 0x40 | `0x00850040` |
| Input Voltage | 0x84 | 0x30 | `0x00840030` |
| AC Present | 0x84 | 0xD1 | `0x008400D1` |
| Charging | 0x84 | 0xD2 | `0x008400D2` |
| Discharging | 0x84 | 0xD3 | `0x008400D3` |
| Need Replacement | 0x84 | 0xDB | `0x008400DB` |

Use `hidapitester --list-detail` on a Windows machine with the UPS attached to dump the actual report descriptor and verify offsets before writing real parsing code.

---

## NUT Protocol Reference

| Command | Response |
|---|---|
| `USERNAME <u>` | `OK` or `ERR ACCESS-DENIED` |
| `PASSWORD <p>` | `OK` or `ERR ACCESS-DENIED` |
| `LOGIN <ups>` | `OK` or `ERR UNKNOWN-UPS` |
| `LOGOUT` | `OK Goodbye` |
| `LIST UPS` | `BEGIN LIST UPS … END LIST UPS` |
| `LIST VAR <ups>` | `BEGIN LIST VAR … END LIST VAR` |
| `LIST RW <ups>` | empty |
| `LIST CMD <ups>` | empty |
| `GET VAR <ups> <var>` | `VAR <ups> <var> "<value>"` |
| `GET TYPE <ups> <var>` | `TYPE <ups> <var> STRING:256` or `INTEGER` |
| `GET DESC <ups> <var>` | `DESC <ups> <var> "<description>"` |
| `GET UPSDESC <ups>` | `UPSDESC <ups> "<description>"` |
| `VER` | `NutAgent 1.0.0` |
| `NETVER` | `3` |
