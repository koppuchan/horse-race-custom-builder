@echo off
chcp 65001 >nul
REM 6-factor historical backfill. VERY slow (potentially hours to days) - run "run-probe.bat"
REM first and confirm SLOP/WOOD/BLOD show up before running this for real.
REM Writes nothing to WordPress; only fills the local SQLite file (data\historical.sqlite3).
REM Optional argument: jv (central racing only) / uma (local racing only). Default: both.
REM
REM NOTE: keep this file ASCII-only - see the comment in run-watch.bat for why.

cd /d "%~dp0"
if exist secrets.local.bat call secrets.local.bat

cd /d "%~dp0bin\Debug\net48"
KeibaDataCollector.exe backfill %1

echo.
echo ===== Finished. Press any key to close this window. =====
pause >nul
