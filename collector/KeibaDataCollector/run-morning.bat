@echo off
chcp 65001 >nul
REM Morning batch: fetches today's race cards and pushes them to WordPress.
REM
REM NOTE: keep this file ASCII-only - see the comment in run-watch.bat for why.

cd /d "%~dp0"
if not exist secrets.local.bat (
    echo [ERROR] secrets.local.bat not found.
    echo Copy secrets.local.bat.example to secrets.local.bat and fill in the values.
    pause
    exit /b 1
)
call secrets.local.bat

cd /d "%~dp0bin\Debug\net48"
KeibaDataCollector.exe morning

echo.
echo ===== Finished. Press any key to close this window. =====
pause >nul
