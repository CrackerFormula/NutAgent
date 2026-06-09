# NutAgent

A lightweight Windows service that turns a USB UPS into a standard [NUT](https://networkupstools.org/) (Network UPS Tools) server — so Home Assistant, Unraid, and any other NUT-compatible tool can monitor it and trigger automatic shutdowns, with zero manual driver wrangling.

## Why NutAgent?

The official Windows port of NUT requires manually swapping USB drivers with Zadig, and it tends to break every time Windows updates. NutAgent replaces all of that with a proper .NET Windows Service that speaks the *same* standard NUT protocol on port `3493` — so your existing Home Assistant NUT integration, Unraid's native NUT client, or any other NUT tool just works, no changes needed on their end.

## Features

- 🔌 **Runs as a native Windows Service** — starts on boot, restarts itself if it crashes
- 📡 **Speaks the standard NUT protocol** — drop-in compatible with Home Assistant, Unraid, and any other NUT client
- 🔋 **Reads your UPS directly over USB** (HID) — no Zadig, no extra drivers
- 🖥️ **Optional system tray app** — live battery status at a glance, plus a full settings window (no config-file editing required)
- ⚡ **Automatic shutdown on low battery** — configurable charge % and/or runtime thresholds, with a grace period
- 🧠 **"Auto" shutdown mode** — learns your UPS's actual discharge rate and times the shutdown dynamically instead of relying on a fixed threshold
- 🌐 **Share one UPS across multiple PCs** — run in Client mode to have other machines watch a server's UPS over the network and shut themselves down too
- 🩺 **Built-in diagnostic tool** — `HidDiag.exe` ships with every install; one run dumps everything needed to add support for a new UPS model straight to a file on your Desktop
- 📦 **Single-file installer** — `NutAgent-Setup.exe` handles the service, firewall rule, tray app, and diagnostic tool in one pass

## Tested UPS models

| Manufacturer | Model |
|---|---|
| Tripp Lite | (HID Power Device class) |
| APC | Back-UPS ES 650G1 |
| CyberPower | CST135XLU |

Any UPS that exposes the standard USB HID Power Device class (most consumer line-interactive units) should work even if it's not in this list — NutAgent reads whatever the device reports and falls back gracefully if some values (like wattage) aren't available.

---

## Installation

### Option A — Installer (recommended)

1. Build `NutAgent-Setup.exe` (see "Building the installer" below) or grab a copy that's already been built
2. Right-click → **Run as administrator**
3. Pick a mode in the wizard:
   - **Server** — choose this if the UPS is plugged into *this* PC via USB
   - **Client** — choose this if you want this PC to watch another PC's UPS over the network (you'll be asked for that PC's IP address)
4. That's it — the service installs, the firewall rule opens automatically (server mode), and the tray app launches at startup

The installer is **upgrade-safe**: running it again on a machine that already has NutAgent preserves your existing settings (password, thresholds, etc.).

### Option B — Build and install manually

Requires the [.NET 10 SDK](https://dot.net).

```powershell
git clone https://github.com/CrackerFormula/NutAgent.git
cd NutAgent

# Build the service (and, optionally, the tray app)
dotnet publish NutAgent/NutAgent.csproj -c Release
dotnet publish NutAgent.Tray/NutAgent.Tray.csproj -c Release

# Install (run as Administrator)
.\install\install.bat -Mode server -InstallTray
#   ...or for a machine sharing another PC's UPS:
.\install\install.bat -Mode client -RemoteHost 192.168.1.10 -InstallTray
```

### Building the installer

Requires [Inno Setup](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`). After publishing the service and tray app as above:

```powershell
& "C:\Users\Bear\AppData\Local\Programs\Inno Setup 6\ISCC.exe" install\nutagent.iss
```

The finished installer lands at `install\Output\NutAgent-Setup.exe`.

### Verify it's running

```powershell
Get-Service NutAgent
```

Status should read `Running`. If it stopped immediately, check **Event Viewer → Windows Logs → Application → Source: NutAgent** for the error.

---

## Configuring NutAgent

The easiest way to change settings is through the **tray app**: right-click the tray icon → **Settings…**. From there you can change the UPS name, NUT login, shutdown thresholds, and (for Client mode) the remote server's address and credentials — all without touching a config file or restarting the service.

If you'd rather edit the file directly, it lives at `C:\Program Files\NutAgent\appsettings.json`:

| Setting | Default | What it does |
|---|---|---|
| `Mode` | `Server` | `Server` (UPS is local) or `Client` (watching a remote server) |
| `UpsName` | `ups` | Name NUT clients use to refer to this UPS |
| `Port` | `3493` | NUT protocol port |
| `Users` | `admin` / `changeme` | **Change this password before exposing the port to your network!** |
| `RemoteHost` | _(empty)_ | Client mode: IP address of the server PC |
| `ShutdownBatteryThreshold` | `20` | Shut down when charge drops below this percent |
| `ShutdownRuntimeMinutes` | `5` | Shut down when estimated runtime drops below this many minutes |
| `ShutdownDelaySeconds` | `60` | Grace period before the shutdown actually happens |
| `UpsNominalWatts` / `UpsNominalVA` | `0` | Nameplate ratings (printed on the UPS) — lets NutAgent estimate real/apparent power for UPS models that don't report wattage directly |

> ⚠️ **Security note:** if you go with the manual install, make sure to change the default `admin` / `changeme` login before opening the firewall port to your LAN.

The service must be restarted after manual edits to `appsettings.json`:

```powershell
Restart-Service NutAgent
```

---

## Connecting Home Assistant or Unraid

Both speak NUT natively — just point them at the machine running NutAgent:

- **Home Assistant:** Settings → Devices & Services → Add Integration → **Network UPS Tools (NUT)** → enter `<PC_IP>:3493` and the username/password from your config
- **Unraid:** its built-in NUT client connects the same way

You should immediately see `battery.charge`, `ups.status`, `battery.runtime`, and friends populate.

You can also poke at it directly with `telnet`:

```
telnet <PC_IP> 3493
USERNAME admin
PASSWORD <your password>
LIST UPS
GET VAR ups ups.status
GET VAR ups battery.charge
LOGOUT
```

---

## The tray app

A small system-tray companion shows your UPS status at a glance and gives you a settings window — the service runs independently, so closing the tray doesn't stop monitoring.

| Icon color | Meaning |
|---|---|
| 🟢 Green | Online, everything normal |
| 🟡 Yellow | Running on battery |
| 🔴 Red | Low battery / shutdown imminent |
| ⚪ Grey | Service isn't responding |

Right-click the icon for a quick status summary, the **Settings…** window, or **Exit** (which only closes the tray — the service keeps running).

---

## Troubleshooting

**Service won't start / stops immediately**
Check **Event Viewer → Windows Logs → Application → Source: NutAgent** for the error.

**`battery.charge` always shows `100` and `battery.runtime` shows `0`**
NutAgent isn't finding your UPS over USB. Double-check the cable, that the UPS shows up in **Device Manager**, and that no other software (e.g. the manufacturer's monitoring app) has an exclusive lock on it.

**`ups.realpower` / `ups.power` don't show up**
Some budget UPS models simply don't report wattage over USB — this is a hardware limitation, not a NutAgent bug (the same gap exists with the official Linux NUT driver for these models). Set `UpsNominalWatts` / `UpsNominalVA` in the config to the numbers printed on your UPS's label, and NutAgent will estimate them from the load percentage instead.

**Settings changes aren't taking effect**
Most settings (thresholds, login, UPS name, remote host) apply immediately. Changing the **port** or switching between **Server**/**Client** mode requires a service restart — the tray app will tell you when that's the case.

---

## My UPS doesn't work right — how do I get it fixed?

NutAgent supports new UPS models by *learning what they actually send over USB* — so the more concretely you can show what your UPS is doing, the faster it can be fixed. Here's what to gather, roughly in order of how much it helps:

### 1. Say exactly what's wrong

Pick whichever matches what you're seeing — it changes where the problem lives:

- **Nothing useful shows up at all** — `battery.charge` is stuck at `100`, `battery.runtime` is `0`, status is always `OL` → NutAgent isn't *finding* the device
- **Some numbers are just wrong** — e.g. voltage reads `1223` instead of `122.3` → the device encodes values differently than NutAgent expects (this is exactly what happened with Tripp Lite units — they needed a one-line fix once we knew the pattern)
- **Mostly works, but one value is missing** — e.g. no `ups.realpower` → your UPS may simply not report that value at all (common on budget models — see the Troubleshooting note above)
- **Status is wrong** — e.g. it says "online" while running on battery power

### 2. Tell us what the device is

- Manufacturer and model (printed on the unit itself)
- USB Vendor/Product ID — open **Device Manager**, find the UPS, **Properties → Details → Hardware Ids**. It'll look like `VID_09AE&PID_3016`

### 3. Show what NutAgent currently reports

From any machine on your network (replace the placeholders with your own):

```
telnet <PC_IP> 3493
USERNAME admin
PASSWORD <your password>
LIST VAR <ups name>
LOGOUT
```

Copy/paste everything it prints back.

### 4. Grab the service's startup logs

Open **Event Viewer → Windows Logs → Application**, filter by **Source: NutAgent**, and copy the entries from when the service starts up — especially any lines mentioning `Startup poll`, `HID status usage`, or `ACPresent usage`. These show exactly which signals NutAgent is receiving from your UPS in those first few seconds.

### 5. Dump the USB report descriptor — *this is the one that really matters*

Everything above tells us *that* something's wrong. This step is what tells us *how to fix it* — it's a complete list of every value and signal your UPS actually sends over USB, in the exact format NutAgent needs to be taught to read.

NutAgent ships with a small tool for exactly this — **`HidDiag.exe`**, already sitting in your install folder, no extra downloads needed:

1. With your UPS plugged in (and nothing else hogging it — close any manufacturer monitoring app first), open a command prompt in `C:\Program Files\NutAgent\` and run:
   ```
   HidDiag.exe
   ```
2. Let it finish — it spends about 5 seconds capturing live data, so don't unplug the UPS mid-run
3. It automatically saves a copy of everything it printed to a file on your **Desktop** (named something like `HidDiag-2026-06-07_183956.txt`) — just attach or paste that file when you report the issue

If you can provide all five of these, fixing support for your device usually becomes a quick, mechanical change rather than a guessing game — that's exactly how Tripp Lite, APC, and CyberPower support were each added.

---

## How it works (for the curious)

NutAgent reads your UPS's status directly over USB using the standard **HID Power Device class** that virtually every consumer UPS implements — the same interface the official NUT Windows driver uses, just without needing Zadig. It then serves that data over a plain-text TCP protocol (the NUT protocol) that Home Assistant, Unraid, and other tools already know how to speak.

Two Windows machines can also be chained together: one runs in **Server** mode (UPS plugged in via USB, serving NUT on `:3493`), and another runs in **Client** mode, watching that server and shutting itself down right alongside it — handy for protecting a second PC that shares the same UPS but has no free USB port for it.

```
  UPS ──USB──▶  PC1 (Server)  ──NUT :3493──▶  PC2 (Client)
                                              shares the UPS, shuts down with PC1
```

Separate from that shutdown chain, *anything* that speaks NUT — Home Assistant, Unraid's own native NUT server, or any other NUT client — can connect to any NUT server purely to keep an eye on it. A typical multi-UPS home setup ends up with Home Assistant as a single dashboard watching every UPS in the house, NutAgent or not:

```
  PC1     (NutAgent server, UPS A)  ─┐
  PC3     (NutAgent server, UPS B)  ─┼──  NUT :3493  ──▶  Home Assistant
  Unraid  (native NUT server, UPS D)─┘                    one dashboard, every UPS
```
