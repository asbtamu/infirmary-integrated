@echo off
rem Right-click this file -> Run as administrator (from the USB drive on the relay PC).
rem Runs install-mirror-relay.ps1 from this folder; mirror_relay.py must sit beside it.
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Not running as administrator.
    echo Right-click Install-Relay.cmd and choose "Run as administrator".
    pause
    exit /b 1
)
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File ".\install-mirror-relay.ps1"
echo.
pause
