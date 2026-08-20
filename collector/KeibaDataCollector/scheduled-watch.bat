@echo off
chcp 65001 >nul
REM ============================================================================
REM Task Scheduler entry point for watch mode (results / payouts).
REM
REM watch mode exits on its own once every race of the day has reached the
REM confirmed stage, so the task simply finishes when the day's racing is over -
REM there is no need for a stop trigger. Configure the Task Scheduler setting
REM "If the task is already running, do not start a new instance" so an overlap
REM cannot open the same COM object twice.
REM
REM Differences from run-watch.bat (interactive): no "pause" - a scheduled task
REM that waits for a key press never finishes - and output is appended to a
REM dated log file under logs\.
REM
REM NOTE: keep this file ASCII-only - see the comment in run-watch.bat for why.
REM ============================================================================

cd /d "%~dp0"

if not exist logs mkdir logs

REM %DATE% is locale-dependent and wmic is absent on newer Windows builds,
REM so ask PowerShell for an unambiguous yyyyMMdd.
for /f %%d in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd"') do set LOGDATE=%%d
set LOGFILE=logs\watch-%LOGDATE%.log

echo ---------------------------------------------------------------- >> "%LOGFILE%"
echo [%DATE% %TIME%] watch start >> "%LOGFILE%"

if not exist "%~dp0secrets.local.bat" (
    echo [ERROR] secrets.local.bat not found. >> "%LOGFILE%"
    exit /b 1
)
REM Use an explicit path, and send call's own output to the log so an encoding
REM problem in secrets.local.bat is recorded rather than lost.
call "%~dp0secrets.local.bat" >> "%LOGFILE%" 2>&1

REM If secrets.local.bat is malformed (UTF-8 Japanese comments or LF-only line
REM endings), cmd misparses it and the set lines never run - which would then
REM fail much later with a confusing WordPress/JV-Link auth error. Fail fast.
if not defined JvLinkSoftwareId (
    echo [ERROR] JvLinkSoftwareId is not set. secrets.local.bat did not apply. >> "%LOGFILE%"
    echo         Keep it ASCII-only with CRLF line endings - see secrets.local.bat.example. >> "%LOGFILE%"
    exit /b 1
)

REM Invoke the exe by its full path rather than relying on the current
REM directory being searched: that search is disabled when the environment sets
REM NoDefaultCurrentDirectoryInExePath=1, and a scheduled task does not
REM necessarily inherit the same environment as an interactive shell.
set EXE=%~dp0bin\Debug\net48\KeibaDataCollector.exe
if not exist "%EXE%" (
    echo [ERROR] Not built yet: %EXE% >> "%LOGFILE%"
    echo         Run: dotnet build -c Debug >> "%LOGFILE%"
    exit /b 1
)

REM Keep the working directory next to the exe; some COM components resolve
REM their own relative paths against it.
pushd "%~dp0bin\Debug\net48"
"%EXE%" watch >> "%~dp0%LOGFILE%" 2>&1
set EXITCODE=%ERRORLEVEL%
popd

echo [%DATE% %TIME%] watch end (exit=%EXITCODE%) >> "%LOGFILE%"
exit /b %EXITCODE%
