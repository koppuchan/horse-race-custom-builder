@echo off
chcp 65001 >nul
REM Watch mode: polls for confirmed race results/payouts and pushes them to WordPress.
REM Start this during race hours and leave it running. Press Ctrl+C to stop.
REM
REM NOTE: keep this file ASCII-only. cmd.exe parses .bat files using the OEM
REM codepage (CP932 on Japanese Windows) regardless of the chcp line above, so
REM UTF-8 Japanese text here gets misread, and a stray byte that happens to
REM decode as '&' splits the line - which is what produced the repeated
REM "'...' is not recognized as an internal or external command" errors.
REM The application's own console output is proper Japanese; only this
REM launcher script is kept ASCII.

cd /d "%~dp0"
if not exist secrets.local.bat (
    echo [ERROR] secrets.local.bat not found.
    echo Copy secrets.local.bat.example to secrets.local.bat and fill in the values.
    pause
    exit /b 1
)
call secrets.local.bat

cd /d "%~dp0bin\Debug\net48"
KeibaDataCollector.exe watch

echo.
echo ===== Finished. Press any key to close this window. =====
pause >nul
