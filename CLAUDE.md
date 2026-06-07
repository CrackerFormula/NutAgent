# NutAgent

## WHAT

**Stack:** .NET 10, C#, Windows x64  
**Projects:** NutAgent service (Phase 1-2) + NutAgent.Tray WPF tray app (Phase 3)

```
NutAgent/
├── NutAgent.sln
├── CLAUDE.md
├── install/
│   └── install.ps1               # installs service, opens firewall port
├── NutAgent/                     # Windows Service — HID + NUT protocol + shutdown
│   ├── NutAgent.csproj           # AssemblyName: ups-agent; SelfContained win-x64
│   ├── Program.cs
│   ├── Worker.cs
│   ├── appsettings.json
│   ├── Config/
│   │   └── AgentConfig.cs
│   ├── Hid/
│   │   ├── IUpsReader.cs
│   │   ├── HidUpsReader.cs       # USB HID Power Device reader (HidSharp 2.1.0)
│   │   ├── DischargeTracker.cs   # rolling discharge rate + runtime estimate
│   │   └── UpsState.cs
│   ├── Ipc/
│   │   └── PipeServer.cs         # named pipe IPC for tray app (\\.\pipe\nutagent)
│   ├── Nut/
│   │   ├── NutVariableMap.cs
│   │   ├── NutSession.cs
│   │   ├── NutServer.cs
│   │   └── NutClient.cs
│   └── Shutdown/
│       └── ShutdownManager.cs    # charge % OR runtime minutes threshold
└── NutAgent.Tray/                # WPF tray app — status icon + settings UI
    ├── NutAgent.Tray.csproj      # AssemblyName: ups-tray; WPF + WinForms
    ├── App.xaml / App.xaml.cs    # single-instance guard, no main window
    ├── Models.cs                 # StatusResponse, TrayConfig, SetConfigResult
    ├── TrayManager.cs            # NotifyIcon lifecycle, icon states, menu
    ├── SettingsWindow.xaml/.cs   # mode + thresholds settings window
    ├── Ipc/
    │   └── PipeClient.cs         # status/getConfig/setConfig over named pipe
    └── Resources/
        ├── icon_online.ico       # green  — OL
        ├── icon_onbattery.ico    # yellow — OB
        ├── icon_lowbattery.ico   # red    — LB / critical
        └── icon_disconnected.ico # grey   — service not running
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
# Service only:
dotnet publish NutAgent/NutAgent.csproj -c Release

# Service + tray app:
dotnet publish NutAgent/NutAgent.csproj -c Release
dotnet publish NutAgent.Tray/NutAgent.Tray.csproj -c Release

# Inno Setup installer (requires Inno Setup — winget install JRSoftware.InnoSetup):
iscc install/nutagent.iss
# Output: install/Output/NutAgent-Setup.exe
```

### Install (run as Administrator)

```powershell
# Server mode with tray app (PC1, PC3, PC4):
.\install\install.bat -Mode server -InstallTray

# Server mode, service only:
.\install\install.bat -Mode server

# Client mode (PC2):
.\install\install.bat -Mode client -RemoteHost 192.168.1.10
```

The installer:
1. Copies `ups-agent.exe` + `appsettings.json` to `C:\NutAgent\`
2. Registers service via `sc.exe`, sets failure restart actions
3. Opens firewall port 3493 (server mode only)

### Windows Deploy Checklist

Follow these steps in order. Run all PowerShell commands as Administrator.

**1. Check .NET 10 SDK**
```powershell
dotnet --list-sdks
```
Need a `10.x.x` entry. If missing, download from https://dot.net and install, then reopen the terminal.

**2. Clone or pull the repo**
```powershell
# First time:
git clone https://git.coplin.ltd/CrackerFormula/NutAgent.git
cd NutAgent

# Already cloned:
git pull
```

**3. Build**
```powershell
dotnet publish NutAgent/NutAgent.csproj -c Release
dotnet publish NutAgent.Tray/NutAgent.Tray.csproj -c Release
```
Expect output ending in `publish: ups-agent.exe` and `publish: ups-tray.exe`.

**4. Install**
```powershell
# Server mode with tray app (PC has UPS connected via USB):
.\install\install.bat -Mode server -InstallTray

# Client mode (PC shares UPS with another machine):
.\install\install.bat -Mode client -RemoteHost <IP of server PC> -InstallTray
```

**5. Verify service started**
```powershell
Get-Service NutAgent
```
Status must be `Running`. If it stopped immediately, check Event Viewer:
`Windows Logs → Application → Source: NutAgent`

**6. Test NUT protocol** (from any machine on the network)
```
telnet <PC_IP> 3493
USERNAME admin
PASSWORD changeme
LIST UPS
GET VAR ups ups.status
GET VAR ups battery.charge
GET VAR ups battery.runtime
LOGOUT
```
`ups.status` should be `OL` (online). `battery.charge` should reflect actual UPS charge.  
If `battery.charge` returns `100` and `battery.runtime` returns `0`, the HID reader is not seeing the UPS — check USB connection and report back.

**7. Change the default password**

Edit `C:\NutAgent\appsettings.json` and change `"Password": "changeme"` to something real. Then restart the service:
```powershell
Restart-Service NutAgent
```

---

### Configure

Edit `C:\NutAgent\appsettings.json` — service requires restart after config changes (hot-reload not yet implemented).

| Field | Default | Notes |
|---|---|---|
| `Agent.Mode` | `Server` | `Server` or `Client` |
| `Agent.UpsName` | `ups` | NUT UPS name |
| `Agent.UpsNominalWatts` | `0` | Nameplate rated watts (W) — estimates `ups.realpower` from `ups.load` when the device doesn't report it directly. `0` = disabled |
| `Agent.UpsNominalVA` | `0` | Nameplate rated apparent power (VA) — estimates `ups.power` from `ups.load` the same way. `0` = disabled |
| `Agent.Port` | `3493` | NUT protocol port |
| `Agent.Users` | `[{admin/changeme}]` | Change password |
| `Agent.RemoteHost` | `""` | Client mode: server IP |
| `Agent.RemoteUpsName` | `ups` | Client mode: remote UPS name |
| `Agent.ShutdownBatteryThreshold` | `20` | Shutdown below this % |
| `Agent.ShutdownRuntimeMinutes` | `5` | Shutdown below this many minutes of runtime |
| `Agent.ShutdownDelaySeconds` | `60` | Grace period before shutdown |
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
← { ...config snapshot as JSON, incl. username/password (flattened from Users[0])
     and remoteUsername/remotePassword for client mode... }

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
Username:  [admin     ]         Remote UPS:  [ups        ]
Password:  [••••••••  ]         Username:    [admin      ]
                                Password:    [••••••••   ]

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
| **2** | ✅ Done | ~~runtime threshold~~ ✅; ~~validate HidUpsReader~~ ✅; ~~DischargeTracker~~ ✅; ~~PipeServer~~ ✅; ~~config hot-reload~~ ✅ |
| **3** | ✅ Done | ~~NutAgent.Tray~~ ✅ — WPF tray icon (4 states) + settings window + PipeClient |
| **4** | ✅ Done | Auto mode — `DischargeTracker` drives dynamic shutdown threshold |
| **5** | ✅ Done | Inno Setup installer — single `NutAgent-Setup.exe`, UAC elevation, wizard (mode + remote host), service + firewall + tray in one pass. See `install/nutagent.iss` and `install/INSTALLER_PLAN.md`. |

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
