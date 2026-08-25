@echo off
chcp 65001 >nul
REM ============================================================================
REM Task Scheduler entry point for the historical backfill (weekly).
REM
REM Refills the local SQLite (data\historical.sqlite3) that the 6 factors are
REM computed from. BackfillFromTime is always "3 years ago from today", so
REM re-running this periodically keeps that rolling window current. This is a
REM long-running batch (can take hours - see run-backfill.bat) so schedule it
REM for a quiet time of day/week, and avoid overlapping with scheduled-score.bat
REM runs (a scoring run that lands mid-backfill may hit a locked database and
REM fail for that one run, but the next scheduled-score.bat run a few hours
REM later will succeed normally once backfill has finished).
REM
REM Optional argument, same as run-backfill.bat: jv (central racing only) /
REM uma (local racing only). Default (no argument): both. Set this via the
REM "Add arguments" field of the Task Scheduler action if you want to split
REM central and local racing into separate schedules instead of running both
REM together.
REM
REM Differences from run-backfill.bat, which is for interactive use:
REM   - No "pause" at the end. A scheduled task that waits for a key press
REM     never finishes, so the task would stay Running forever and later
REM     triggers would be skipped.
REM   - Appends stdout/stderr to a dated log file under logs\, since a
REM     scheduled task has no console to read.
REM   - Propagates the exit code so Task Scheduler's Last Run Result is
REM     meaningful (0 = success).
REM
REM NOTE: keep this file ASCII-only - see the comment in run-watch.bat for why.
REM ============================================================================

cd /d "%~dp0"

if not exist logs mkdir logs

REM Date-stamped log name. %DATE% is locale-dependent and wmic is absent on
REM newer Windows builds, so ask PowerShell for an unambiguous yyyyMMdd.
for /f %%d in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd"') do set LOGDATE=%%d
set LOGFILE=logs\backfill-%LOGDATE%.log

echo ---------------------------------------------------------------- >> "%LOGFILE%"
echo [%DATE% %TIME%] backfill batch start (arg=%1) >> "%LOGFILE%"

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

REM Launch a background watcher that auto-answers JV-Link's start-kit setup
REM dialog if it appears - see dismiss-startkit-dialog.ps1 for details on why
REM this exists (this task must run in an interactive logged-on session, not
REM "regardless of logon", for the watcher to actually be able to see it) and
REM why it now loops for the whole run instead of exiting after one dialog
REM (confirmed on the VPS: the dialog can reappear per dataspec - RACE, SLOP,
REM WOOD, ... - not just once). Runs detached (start /min) so it does not
REM block this script; it is explicitly stopped below once backfill finishes,
REM its own long timeout is only a safety net in case that stop step is
REM somehow skipped (e.g. this script itself gets killed).
REM
REM Deliberately NOT using "start ... >> file" here: attaching redirection
REM directly to a start command is a known cmd.exe gotcha that can make start
REM wait for the whole child process instead of truly detaching (confirmed on
REM this project's actual VPS - the batch never even reached the exe launch
REM below). The watcher writes its own log via -LogFile instead.
start "" /min powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0dismiss-startkit-dialog.ps1" -LogFile "%~dp0%LOGFILE%"

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
"%EXE%" backfill %1 >> "%~dp0%LOGFILE%" 2>&1
set EXITCODE=%ERRORLEVEL%
popd

REM Backfill itself is done, so the watcher has nothing left to guard - stop
REM it now instead of leaving it running for the rest of its safety-net
REM timeout. Matched by command line content (not just process name) so this
REM cannot accidentally kill an unrelated powershell.exe on the machine.
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*dismiss-startkit-dialog.ps1*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" >> "%LOGFILE%" 2>&1

echo [%DATE% %TIME%] backfill batch end (exit=%EXITCODE%) >> "%LOGFILE%"
exit /b %EXITCODE%
