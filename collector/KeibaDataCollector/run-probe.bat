@echo off
chcp 65001 >nul
REM Probe mode (investigation only): checks which dataspecs actually return data,
REM and whether tansho odds / favourite rank are supplied for local racing.
REM Writes nothing to WordPress.
REM
REM NOTE: keep this file ASCII-only - see the comment in run-watch.bat for why.

cd /d "%~dp0"
if exist secrets.local.bat call secrets.local.bat

cd /d "%~dp0bin\Debug\net48"
KeibaDataCollector.exe probe

echo.
echo ===== Finished. Press any key to close this window. =====
pause >nul
