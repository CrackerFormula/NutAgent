@echo off
:: Wrapper so install.ps1 runs without requiring a changed execution policy.
:: Must be run as Administrator.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
