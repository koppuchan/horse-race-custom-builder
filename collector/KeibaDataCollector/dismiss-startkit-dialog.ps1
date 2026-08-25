# dismiss-startkit-dialog.ps1
#
# Watches for JV-Link's one-time setup dialog (Setup / Setup kit prompt) and
# automatically selects "does not have a start kit" then clicks OK, so that an
# unattended (Task Scheduler) backfill run does not hang forever.
#
# Background: JVOpen with option=3 (Setup) can trigger JV-Link's own native
# dialog asking whether you have a physical start-kit CD/DVD-ROM. Physical
# start kits were discontinued in March 2022, so the correct answer is always
# "does not have one" (second radio button), which makes JV-Link download the
# full structure itself instead of waiting for a disc.
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
    [int]$TimeoutSeconds = 600,
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

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# "SetupTitle" = the dialog's window title, katakana for "Setup".
$setupTitle = -join @([char]0x30BB, [char]0x30C3, [char]0x30C8, [char]0x30A2, [char]0x30C3, [char]0x30D7)
# "NoKitPhrase" = substring of the "does not have a start kit" radio button label.
$noKitPhrase = -join @([char]0x6301, [char]0x3063, [char]0x3066, [char]0x3044, [char]0x306A, [char]0x3044)

Write-Log "dismiss-startkit-dialog watcher started (timeout=${TimeoutSeconds}s)"

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$root = [System.Windows.Automation.AutomationElement]::RootElement

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
                Write-Log "Dismissed setup dialog: selected no-start-kit option and clicked OK."
                exit 0
            } else {
                Write-Log "Found setup dialog and radio button but could not find OK button."
                exit 1
            }
        }
    }

    Start-Sleep -Seconds 2
}

Write-Log "No setup dialog appeared within timeout; nothing to do."
exit 0
