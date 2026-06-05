@echo off
:: Wrapper so install.ps1 runs without requiring a changed execution policy.
:: Must be run as Administrator.
:: Try PowerShell 7 first, fall back to Windows PowerShell 5.
where /q pwsh.exe && (
    pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
) || (
    "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
)
