@echo off
chcp 65001 >nul
REM Prints record counts and date ranges from the local historical.sqlite3 - no JV-Link/
REM UmaConn involved, so this runs instantly. Use after run-backfill.bat to confirm what
REM actually landed in the database (source of truth over eyeballing scrollback).

cd /d "%~dp0bin\Debug\net48"
KeibaDataCollector.exe dbstats

echo.
echo ===== Finished. Press any key to close this window. =====
pause >nul
