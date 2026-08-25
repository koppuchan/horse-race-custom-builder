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
# During a Task Scheduler run configured to "run whether user is logged on or
# not", this dialog renders in a non-interactive session (Session 0) that
# nobody can see or click, even while watching the machine over RDP - the
# backfill process just blocks forever waiting for an answer nobody can give.
# UI Automation still works against windows in that same non-interactive
# session (the isolation only blocks *other* sessions from reaching in), so
# this script - launched from the same scheduled task, in the same session -
# can find the dialog and answer it even though a human cannot.
#
# Encoding note: this file is deliberately kept pure ASCII (Japanese strings
# below are built from Unicode code points, not written as literal characters)
# so it cannot be corrupted by a codepage mismatch the way the project's .bat
# launchers already were once (see the ASCII-only note in run-watch.bat) -
# PowerShell's own script encoding handling has similar pitfalls on some
# Windows PowerShell 5.1 setups depending on how the file is saved.

param(
    [int]$TimeoutSeconds = 600
)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# "SetupTitle" = the dialog's window title, katakana for "Setup".
$setupTitle = -join @([char]0x30BB, [char]0x30C3, [char]0x30C8, [char]0x30A2, [char]0x30C3, [char]0x30D7)
# "NoKitPhrase" = substring of the "does not have a start kit" radio button label.
$noKitPhrase = -join @([char]0x6301, [char]0x3063, [char]0x3066, [char]0x3044, [char]0x306A, [char]0x3044)

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
                Write-Output "Dismissed setup dialog: selected no-start-kit option and clicked OK."
                exit 0
            } else {
                Write-Output "Found setup dialog and radio button but could not find OK button."
                exit 1
            }
        }
    }

    Start-Sleep -Seconds 2
}

Write-Output "No setup dialog appeared within timeout; nothing to do."
exit 0
