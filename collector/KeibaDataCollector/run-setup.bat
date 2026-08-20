@echo off
chcp 65001 >nul
REM Run once on first setup: opens the JV-Link / UmaConn service-key dialog.
REM WordPress credentials are not needed (setup mode never connects to WordPress).
REM
REM NOTE: keep this file ASCII-only - see the comment in run-watch.bat for why.

cd /d "%~dp0bin\Debug\net48"
KeibaDataCollector.exe setup

echo.
echo ===== Finished. Press any key to close this window. =====
pause >nul
