# Self-elevating launcher for install.ps1.
# Called by install-server.bat and install-client.bat.
param(
    [Parameter(Mandatory)] [string] $Mode,
    [string] $RemoteHost = "",
    [switch] $InstallTray
)

# Re-launch as admin if needed.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin)
{
    $ps5 = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $launchArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Mode $Mode -InstallTray:$($InstallTray.IsPresent)"
    # Quote (and strip embedded quotes from) RemoteHost before splicing it into the
    # argument string — Start-Process re-parses this as a single command line, so an
    # unquoted value containing spaces or quotes would be mis-split into extra arguments.
    if ($RemoteHost) { $launchArgs += ' -RemoteHost "' + $RemoteHost.Replace('"', '') + '"' }
    Start-Process $ps5 -ArgumentList $launchArgs -Verb RunAs -Wait
    exit
}

# Already admin — run installer.
$installScript = Join-Path $PSScriptRoot "install.ps1"
$projectRoot   = Split-Path $PSScriptRoot -Parent
Set-Location $projectRoot

if ($RemoteHost) {
    & $installScript -Mode $Mode -RemoteHost $RemoteHost -InstallTray:$InstallTray
} else {
    & $installScript -Mode $Mode -InstallTray:$InstallTray
}

if ($InstallTray) {
    Write-Host ""
    Write-Host ">>> Launch the tray app now: C:\NutAgent\ups-tray.exe" -ForegroundColor Cyan
    Write-Host "    It will auto-start on every login from here on." -ForegroundColor Cyan
}

Write-Host ""
Read-Host "Press Enter to close"
