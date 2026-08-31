@echo off
chcp 65001 >nul
REM Computes the 6 factors for today's runners from the backfilled local SQLite
REM (data\historical.sqlite3) and pushes them to WordPress as hrc_factors.
REM Run run-backfill.bat at least once before this, or every horse will score null.

cd /d "%~dp0"
if exist secrets.local.bat call secrets.local.bat

cd /d "%~dp0bin\Debug\net48"
REM %1 is an optional yyyy-MM-dd date (e.g. run-score.bat 2026-08-30) to
REM re-score a past day instead of today.
KeibaDataCollector.exe score %1

echo.
echo ===== Finished. Press any key to close this window. =====
pause >nul
