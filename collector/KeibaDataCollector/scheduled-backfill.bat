@echo off
chcp 65001 >nul
REM ============================================================================
REM Task Scheduler entry point for the incremental historical refresh (weekly).
REM
REM Tops up the local SQLite (data\historical.sqlite3) that the 6 factors are
REM computed from, with whatever is new since the last run.
REM
REM IMPORTANT - this runs "backfill incremental" (JVOpen option=Normal), NOT the
REM full-history backfill (option=Setup) that run-backfill.bat does by default:
REM
REM   Setup mode means "perform a setup" to JV-Link, so it pops up its own
REM   native dialog asking whether you have a start-kit CD/DVD-ROM. That is not
REM   a one-time prompt - it reappears for every dataspec (RACE, then SLOP, then
REM   WOOD, ...), and it was confirmed on this VPS that an unattended run simply
REM   blocks forever on it. It cannot be reliably clicked by automation either.
REM   So Setup mode is strictly for manual, attended runs.
REM
REM   Normal mode never shows that dialog, and returns roughly the last year of
REM   data (see README), which is far more than a weekly refresh needs. That
REM   makes it the right mode for unattended scheduling.
REM
REM The one-time full history load is therefore a manual step: run
REM run-backfill.bat once by hand and answer the dialogs. After that, this
REM weekly incremental task keeps the data current with no interaction.
REM
REM Avoid overlapping with scheduled-score.bat runs (a scoring run that lands
REM mid-refresh may hit a locked database and fail for that one run, but the
REM next scheduled-score.bat run a few hours later will succeed normally).
REM
REM Optional argument: jv (central racing only) / uma (local racing only).
REM Default (no argument): both. Set this via the "Add arguments" field of the
REM Task Scheduler action if you want to split central and local racing into
REM separate schedules instead of running both together.
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
REM "incremental" is what keeps this unattended-safe - see the header comment.
"%EXE%" backfill incremental %1 >> "%~dp0%LOGFILE%" 2>&1
set EXITCODE=%ERRORLEVEL%
popd

echo [%DATE% %TIME%] backfill batch end (exit=%EXITCODE%) >> "%LOGFILE%"
exit /b %EXITCODE%
