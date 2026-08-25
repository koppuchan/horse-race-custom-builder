# dismiss-startkit-dialog.ps1
#
# Watches for JV-Link's setup dialog (Setup / Setup kit prompt) and
# automatically selects "does not have a start kit" then clicks OK, so that an
# unattended (Task Scheduler) backfill run does not hang forever.
#
# Background: JVOpen with option=3 (Setup) can trigger JV-Link's own native
# dialog asking whether you have a physical start-kit CD/DVD-ROM. Physical
# start kits were discontinued in March 2022, so the correct answer is always
# "does not have one" (second radio button), which makes JV-Link download the
# full structure itself instead of waiting for a disc.
#
# Confirmed on this project's actual VPS: this dialog is not a one-time-ever
# prompt - it can appear separately for each dataspec the backfill opens with
# Setup mode (RACE, then SLOP, then WOOD, ...), each apparently needing its own
# structure downloaded and its own prompt answered. So this script loops for
# its entire timeout window instead of exiting after the first dialog it
# handles, to cover the whole backfill run rather than just its first minute.
#
# A Task Scheduler run configured to "run whether user is logged on or not"
# executes in a non-interactive session (Session 0), where this dialog would
# render somewhere nobody can ever see or click it, and UI Automation against
# the root element there was found (on this project's actual VPS) to hang
# rather than error out cleanly - so the task is configured to run only when
# a real user is logged on instead (see scheduled-backfill.bat / the Task
# Scheduler setup notes), keeping an interactive desktop available for this
# script to actually see and click the dialog in.
#
# Encoding note: this file is deliberately kept pure ASCII (Japanese strings
# below are built from Unicode code points, not written as literal characters)
# so it cannot be corrupted by a codepage mismatch the way the project's .bat
# launchers already were once (see the ASCII-only note in run-watch.bat) -
# PowerShell's own script encoding handling has similar pitfalls on some
# Windows PowerShell 5.1 setups depending on how the file is saved.

param(
    # Must cover the whole backfill run, not just its first minute or so - see
    # the note above about the dialog reappearing per dataspec. scheduled-
    # backfill.bat also explicitly stops this script once backfill itself
    # finishes, so this is a safety-net ceiling more than an expected runtime.
    [int]$TimeoutSeconds = 21600,
    [string]$LogFile = ""
)

# Writes its own log rather than relying on the caller to redirect this
# script's console output: attaching ">> file" directly to a "start" command
# line is a known cmd.exe gotcha that can make "start" wait for the whole
# child process instead of truly detaching (which is what caused this script
# to be launched at all - see scheduled-backfill.bat). Doing file I/O here
# avoids that trap entirely rather than trying to get the redirection right.
function Write-Log([string]$Message) {
    $line = "[{0}] {1}" -f (Get-Date -Format "yyyy/MM/dd HH:mm:ss.ff"), $Message
    if ($LogFile -ne "") {
        Add-Content -Path $LogFile -Value $line
    } else {
        Write-Output $line
    }
}

# Log immediately, before anything that could fail (Add-Type, UI Automation
# calls), so a silent crash still leaves a trace of how far the script got.
# A prior run left no log output at all - not even this line would have been
# written yet at that point - which is itself the clue that the failure was
# somewhere in Add-Type or the automation calls below, not in the polling
# logic; wrapping those in try/catch (below) turns that guess into a real
# error message next time instead of another silent gap.
Write-Log "dismiss-startkit-dialog.ps1 launched (timeout=${TimeoutSeconds}s)"

try {
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Write-Log "UI Automation assemblies loaded OK."

    # "SetupTitle" = the dialog's window title, katakana for "Setup".
    $setupTitle = -join @([char]0x30BB, [char]0x30C3, [char]0x30C8, [char]0x30A2, [char]0x30C3, [char]0x30D7)
    # "NoKitPhrase" = substring of the "does not have a start kit" radio button label.
    $noKitPhrase = -join @([char]0x6301, [char]0x3063, [char]0x3066, [char]0x3044, [char]0x306A, [char]0x3044)

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $dismissCount = 0

    while ((Get-Date) -lt $deadline) {
        $titleCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $setupTitle)
        $dialog = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $titleCondition)

        if ($dialog -ne $null) {
            $radioCondition = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::RadioButton)
            $radios = $dialog.FindAll([System.Windows.Automation.TreeScope]::Descendants, $radioCondition)

            $target = $null
            foreach ($r in $radios) {
                if ($r.Current.Name -like "*$noKitPhrase*") {
                    $target = $r
                    break
                }
            }

            if ($target -ne $null) {
                $selectPattern = $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                $selectPattern.Select()
                Start-Sleep -Milliseconds 300

                $okCondition = New-Object System.Windows.Automation.PropertyCondition(
                    [System.Windows.Automation.AutomationElement]::NameProperty, "OK")
                $okButton = $dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $okCondition)

                if ($okButton -ne $null) {
                    $invokePattern = $okButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                    $invokePattern.Invoke()
                    $dismissCount++
                    Write-Log "Dismissed setup dialog #${dismissCount}: selected no-start-kit option and clicked OK. Continuing to watch (may reappear for the next dataspec)."
                } else {
                    Write-Log "Found setup dialog and radio button but could not find OK button. Will retry on the next poll."
                }
            }
        }

        Start-Sleep -Seconds 2
    }

    Write-Log "Timeout reached (dismissed ${dismissCount} dialog(s) total); stopping watcher."
    exit 0
} catch {
    $errorDetail = ($_.InvocationInfo.PositionMessage -replace '[\r\n]+', ' ')
    Write-Log "ERROR: $($_.Exception.GetType().FullName): $($_.Exception.Message)"
    Write-Log "ERROR detail: $errorDetail"
    exit 1
}
