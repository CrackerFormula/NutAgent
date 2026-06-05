# NutAgent Inno Setup Installer Plan

## Goal

Replace the current `.bat` + PowerShell script flow with a single `NutAgent-Setup.exe`.
User flow: double-click → UAC prompt → two wizard questions → done. No source tree needed.

## Why Inno Setup

- `PrivilegesRequired=admin` gives proper UAC elevation at launch — no multi-process elevation dance like the current `launch.ps1` approach
- Built-in uninstaller registered in Add/Remove Programs
- LZMA compression produces ~120MB vs ~300MB for an embedded-.NET-resources approach
- `.iss` script is ~80 lines, version-controlled, no external runtime on target machines
- 7-Zip SFX rejected: UAC timing bug — SFX exits before elevated child process finishes
- WiX/MSIX rejected: overkill; MSIX sandboxing blocks service installs and firewall rules
- .NET installer project rejected: you own the uninstaller and all install mechanics Inno gives for free

## New File: `install/nutagent.iss`

### `[Setup]`

```pascal
#define AppVersion "1.0.0"

[Setup]
AppName=NutAgent
AppVersion={#AppVersion}
DefaultDirName={autopf64}\NutAgent
PrivilegesRequired=admin
OutputDir=Output
OutputBaseFilename=NutAgent-Setup
Compression=lzma2
SolidCompression=yes
```

### `[Files]`

```pascal
[Files]
Source: "..\NutAgent\bin\Release\net10.0-windows\win-x64\publish\ups-agent.exe"; DestDir: "{app}"
Source: "..\NutAgent.Tray\bin\Release\net10.0-windows\win-x64\publish\ups-tray.exe"; DestDir: "{app}"
Source: "..\NutAgent\appsettings.json"; DestDir: "{app}"; AfterInstall: PatchConfig
```

### `[Code]` — Custom Wizard Pages

Two pages inserted after the license/welcome page:

**Page 1 — Mode selection**
- Radio buttons: `Server` / `Client`

**Page 2 — Remote host** (shown only when Client is selected)
- Text field: remote NUT server IP
- Validate non-empty on Next

**Config patching** (`AfterInstall` on appsettings.json copy):
- Read `{app}\appsettings.json`
- Replace `Agent.Mode` value with selected mode
- Replace `Agent.RemoteHost` value with entered IP (client mode only)
- Write back in place

### `[Run]` — Post-install (runs as admin)

```pascal
[Run]
; Register and start the service
Filename: "{sys}\sc.exe"; Parameters: "create NutAgent binPath= ""{app}\ups-agent.exe"" start= auto DisplayName= ""NutAgent UPS Agent"""
Filename: "{sys}\sc.exe"; Parameters: "description NutAgent ""NUT-compatible UPS monitoring agent"""
Filename: "{sys}\sc.exe"; Parameters: "failure NutAgent reset= 60 actions= restart/5000/restart/10000/restart/30000"
Filename: "{sys}\sc.exe"; Parameters: "start NutAgent"

; Firewall rule — server mode only (Check: IsServerMode)
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""NutAgent NUT Server (TCP 3493)"" dir=in action=allow protocol=TCP localport=3493"; Check: IsServerMode

; Tray startup registry entry
; Written via [Registry] section (see below)
```

### `[Registry]`

```pascal
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "NutAgentTray"; ValueData: """{app}\ups-tray.exe"""
```

### `[UninstallRun]` — Clean uninstall

```pascal
[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop NutAgent"; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete NutAgent"; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""NutAgent NUT Server (TCP 3493)"""

[UninstallDelete]
; Files created at runtime (config edits, logs) — remove on uninstall
Type: files; Name: "{app}\appsettings.json"
```

Registry key written in `[Registry]` is automatically removed by Inno on uninstall.

## Build Steps (Windows only)

```powershell
# Install Inno Setup once
winget install JRSoftware.InnoSetup

# Build both projects
dotnet publish NutAgent/NutAgent.csproj -c Release
dotnet publish NutAgent.Tray/NutAgent.Tray.csproj -c Release

# Compile installer
iscc install/nutagent.iss

# Output
# install/Output/NutAgent-Setup.exe
```

## End-User Wizard Flow

```
Welcome
  → Next

Mode
  ● Server   ○ Client
  → Next

Remote Host  (Client only)
  NUT server IP: [192.168.1.x]
  → Next

Installing...
  Copying files
  Registering service
  Adding firewall rule  (server only)
  Adding tray to startup

Finish
  ☑ Launch tray app now
```

## What Stays

| File | Keep? | Notes |
|---|---|---|
| `install/install.ps1` | Yes | Still useful for scripted/CI deploys |
| `install/install-server.bat` | Yes | Dev fallback |
| `install/install-client.bat` | Yes | Dev fallback |
| `install/launch.ps1` | Yes | Used by the .bat files |
| `install/install.bat` | Yes | Generic entry point |
| `install/nutagent.iss` | Add | The new file |

## CLAUDE.md Updates Needed

After the `.iss` file is built and working:
- Add `iscc install/nutagent.iss` to the HOW / Build section
- Add `install/Output/NutAgent-Setup.exe` note to the deploy checklist
