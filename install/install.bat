@echo off
:: Self-elevate via UAC if not already running as Administrator.
net session >nul 2>&1
if %errorLevel% neq 0 (
    "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -Command ^
        "Start-Process cmd -ArgumentList '/c cd /d ""%~dp0.."" && ""%~f0"" %*' -Verb RunAs"
    exit /b
)

:: Already admin — run the installer.
:: Try PowerShell 7 first, fall back to Windows PowerShell 5.
cd /d "%~dp0.."
where /q pwsh.exe && (
    pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
) || (
    "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
)
pause
