# NutAgent

## WHAT

**Stack:** .NET 8 Worker Service, C#, HidSharp (NuGet), Windows Service host  
**Target:** Windows x64 only (win-x64 self-contained single exe)  
**Output:** `ups-agent.exe` — one binary, two modes

```
NutAgent/
├── NutAgent.sln
├── CLAUDE.md
├── install/
│   └── install.ps1          # installs/registers Windows service
└── NutAgent/
    ├── NutAgent.csproj
    ├── Program.cs            # host builder, DI, Windows service hook
    ├── Worker.cs             # BackgroundService — wires mode branches
    ├── appsettings.json      # all config (mode, port, users, thresholds)
    ├── Config/
    │   └── AgentConfig.cs    # typed config model
    ├── Hid/
    │   ├── IUpsReader.cs     # interface: Read() + IsConnected
    │   ├── HidUpsReader.cs   # HID Power Device class reader (HidSharp)
    │   └── UpsState.cs       # data model + UpsStatus flags + ToNutString()
    ├── Nut/
    │   ├── NutVariableMap.cs # maps UpsState → NUT variable names/values
    │   ├── NutSession.cs     # one TCP client session, NUT text protocol
    │   ├── NutServer.cs      # TcpListener, spawns NutSession per connection
    │   └── NutClient.cs      # polls remote NUT server, triggers shutdown
    └── Shutdown/
        └── ShutdownManager.cs # schedules/cancels graceful Windows shutdown
```

---

## WHY

The Windows port of NUT (Network UPS Tools) requires manual driver replacement via Zadig, has no real installer, and breaks on Windows updates. This project replaces the NUT server component with a proper Windows service that speaks the standard NUT protocol — so Home Assistant, Unraid's native NUT client, and any other NUT tool work without modification.

### Deployment topology

```
UPS A (USB→ PC1) → ups-agent --mode server :3493
         PC2     → ups-agent --mode client → watches PC1

UPS B (USB→ PC3) → ups-agent --mode server :3493
UPS C (USB→ PC4) → ups-agent --mode server :3493

UPS D (USB→ Unraid) → native NUT server :3493
         HA          → native NUT integration → connects to all 4 servers
```

PC2 doesn't have a USB connection to its UPS — it runs in client mode, monitoring PC1's NUT server and handling its own shutdown.

---

## HOW

### Build

```powershell
# On the target Windows machine or cross-compiled from Mac:
dotnet publish NutAgent/NutAgent.csproj -c Release
# Output: NutAgent/bin/Release/net8.0-windows/win-x64/publish/ups-agent.exe
```

### Install (run as Administrator on each Windows PC)

```powershell
# Server mode (PC1, PC3, PC4):
.\install\install.ps1 -Mode server

# Client mode (PC2):
.\install\install.ps1 -Mode client -RemoteHost 192.168.1.10
```

The installer:
1. Copies `ups-agent.exe` and `appsettings.json` to `C:\NutAgent\`
2. Patches `appsettings.json` for the correct mode/remote host
3. Registers and starts the Windows service
4. Opens firewall port 3493 (server mode only)

### Configure

Edit `C:\NutAgent\appsettings.json` then restart the service (`Restart-Service NutAgent`).

Key fields:
| Field | Default | Notes |
|---|---|---|
| `Agent.Mode` | `Server` | `Server` or `Client` |
| `Agent.UpsName` | `ups` | Name reported to NUT clients |
| `Agent.Port` | `3493` | NUT protocol port |
| `Agent.Users` | `[{admin/changeme}]` | Auth — change password |
| `Agent.RemoteHost` | `""` | Client mode: IP of NUT server |
| `Agent.RemoteUpsName` | `ups` | Client mode: UPS name on remote |
| `Agent.ShutdownBatteryThreshold` | `20` | Shutdown below this % |
| `Agent.ShutdownDelaySeconds` | `60` | Grace period before shutdown |
| `Agent.PollIntervalSeconds` | `30` | Client poll frequency |

### Logs

Windows Event Viewer → Windows Logs → Application → Source: `NutAgent UPS Agent`

### Verify with HA

In HA, add a NUT integration pointing to `<PC_IP>:3493` with the configured credentials. Variables like `battery.charge`, `ups.status`, and `battery.runtime` should populate immediately.

### Manual NUT protocol test (from any machine with netcat)

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

## Implementation Plan

### Phase 1 — Core (current)
- [x] Project scaffold: solution, csproj, directory layout
- [x] `AgentConfig` typed config
- [x] `UpsState` model + `UpsStatus` flags
- [x] `NutVariableMap` — state → NUT variable names
- [x] `NutSession` — full NUT text protocol (LIST UPS, LIST VAR, GET VAR, GET TYPE, GET DESC, auth)
- [x] `NutServer` — TCP listener, spawns session per connection
- [x] `NutClient` — polls remote, triggers shutdown
- [x] `ShutdownManager` — `shutdown.exe /s /t 0 /f` with delay + cancel
- [x] `Worker` — wires server or client branch
- [x] `Program.cs` — host builder + `UseWindowsService()`
- [x] `install.ps1` — service install, firewall, config patch

### Phase 2 — HID reading (next)
The HID UPS reader scaffold is in place. The core loop is correct. What needs real testing:

**Finding the device:**  
`HidUpsReader.FindUpsDevice()` scans for a device whose top-level usage page is `0x84` (Power Device). Most compliant UPS devices declare this. If a specific UPS isn't found, add its VendorId/ProductId as a fallback filter.

**Reading values:**  
The `HidDeviceInputReceiver` approach in `ApplyDataValue()` handles standard HID input reports. However, some UPS values (nominal voltage, manufacturer string) are in **feature reports** — the `ReadNominalValues()` method needs fleshing out per-device once tested.

**Status bitmask:**  
The current implementation derives `OnBattery` from `ACPresent=false`. Some UPS devices report a combined `PresentStatus` bitmask usage (`0x85D0`). Add handling for this if individual bit usages aren't working.

**Testing HID without a UPS:**  
Add a `FakeUpsReader : IUpsReader` that returns canned data. Wire it in when no HID device is found, so the NUT server still responds during development.

**HID usage constants to verify:**  
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

### Phase 3 — Polish
- [ ] `FakeUpsReader` for dev/testing without a physical UPS
- [ ] `appsettings.Development.json` with fake reader wired
- [ ] Unit tests for `NutSession` protocol parsing (no TCP needed — use `MemoryStream`)
- [ ] Uninstall script (`uninstall.ps1`)
- [ ] Config validation on startup (warn if password is still `changeme`)

---

## NUT Protocol Reference

NutAgent implements the subset needed by Home Assistant and upsmon:

| Command | Response |
|---|---|
| `USERNAME <u>` | `OK` or `ERR ACCESS-DENIED` |
| `PASSWORD <p>` | `OK` or `ERR ACCESS-DENIED` |
| `LOGIN <ups>` | `OK` or `ERR UNKNOWN-UPS` |
| `LOGOUT` | `OK Goodbye` |
| `LIST UPS` | `BEGIN LIST UPS … END LIST UPS` |
| `LIST VAR <ups>` | `BEGIN LIST VAR … END LIST VAR` |
| `LIST RW <ups>` | empty (no writable vars) |
| `LIST CMD <ups>` | empty (no commands) |
| `GET VAR <ups> <var>` | `VAR <ups> <var> "<value>"` |
| `GET TYPE <ups> <var>` | `TYPE <ups> <var> STRING:256` or `INTEGER` |
| `GET DESC <ups> <var>` | `DESC <ups> <var> "<description>"` |
| `GET UPSDESC <ups>` | `UPSDESC <ups> "<description>"` |
| `VER` | `NutAgent 1.0.0` |
| `NETVER` | `3` |
