#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs ups-agent.exe as a Windows service.

.PARAMETER Mode
    server  — reads local UPS via USB, serves NUT protocol on port 3493
    client  — monitors a remote NUT server, triggers local shutdown

.PARAMETER RemoteHost
    (client mode) IP of the NUT server to monitor

.EXAMPLE
    # Server (PC1, PC3, PC4):
    .\install.ps1 -Mode server

    # Client (PC2, sharing UPS with PC1):
    .\install.ps1 -Mode client -RemoteHost 192.168.1.10
#>
param(
    [Parameter(Mandatory)]
    [ValidateSet("server", "client")]
    [string]$Mode,

    [string]$RemoteHost = "",
    [string]$InstallDir = "C:\NutAgent",
    [string]$ServiceName = "NutAgent"
)

$ErrorActionPreference = "Stop"
$exe = Join-Path $InstallDir "ups-agent.exe"
$cfg = Join-Path $InstallDir "appsettings.json"

# --- Copy files ---
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir | Out-Null }

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceExe = Join-Path $scriptDir "..\NutAgent\bin\Release\net10.0-windows\win-x64\publish\ups-agent.exe"
$sourceCfg = Join-Path $scriptDir "..\NutAgent\appsettings.json"

if (-not (Test-Path $sourceExe)) {
    Write-Error "ups-agent.exe not found. Run: dotnet publish -c Release first."
}

# --- Stop and remove existing service before touching the file ---
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing service..."
    Stop-Service -Name $ServiceName -Force
    & "$env:SystemRoot\System32\sc.exe" delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Copy-Item $sourceExe $InstallDir -Force
Copy-Item $sourceCfg $InstallDir -Force

# --- Patch config for mode and remote host ---
$json = Get-Content $cfg -Raw | ConvertFrom-Json
$json.Agent.Mode = (Get-Culture).TextInfo.ToTitleCase($Mode)
if ($Mode -eq "client" -and $RemoteHost) {
    $json.Agent.RemoteHost = $RemoteHost
}
$json | ConvertTo-Json -Depth 10 | Set-Content $cfg

# --- Register service ---

& "$env:SystemRoot\System32\sc.exe" create $ServiceName binPath= "`"$exe`"" start= auto DisplayName= "NutAgent UPS Agent" | Out-Null
& "$env:SystemRoot\System32\sc.exe" description $ServiceName "NUT-compatible UPS monitoring agent (NutAgent)" | Out-Null
& "$env:SystemRoot\System32\sc.exe" failure $ServiceName reset= 60 actions= restart/5000/restart/10000/restart/30000 | Out-Null

Write-Host "Starting service..."
Start-Service -Name $ServiceName
Start-Sleep -Seconds 2
$status = (Get-Service -Name $ServiceName).Status
Write-Host "Service status: $status"

if ($Mode -eq "server") {
    # Open firewall for NUT port
    $ruleName = "NutAgent NUT Server (TCP 3493)"
    Remove-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort 3493 -Action Allow | Out-Null
    Write-Host "Firewall rule added for port 3493"
}

Write-Host ""
Write-Host "NutAgent installed successfully in $Mode mode."
Write-Host "Config: $cfg"
Write-Host "Logs:   Event Viewer > Windows Logs > Application (Source: NutAgent)"
