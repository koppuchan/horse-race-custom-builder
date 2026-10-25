<#
.SYNOPSIS
    このVPSの定期タスクの起動時刻を、一斉起動しないように組み直す。

.DESCRIPTION
    スクリーンショット(2026-10-05時点)では、毎時0分に5〜7本、9:00には7本のタスクが同時に
    起動していた。時刻の分散と、HRC側の実行回数・実行時間上限の見直しを行う。

    既定は確認だけ(何も変更しない)。-Apply を付けたときだけ変更する。

      HRC Score    : 6:00起点の3時間おき(1日8回。0〜6時は出走表が無く無駄)
                     → 07:26 / 10:26 / 13:26 / 16:26 の1日4回、上限 2時間30分
                     (3時間未満なので次の回に食い込まない。1時間で強制終了されていたため延長)
      HRC Backfill : 月曜 4:30 → 月曜 3:30、上限 3時間30分(7:00に他の定期処理が始まる前に終える)
                     (これも強制終了されていた。JV-Linkのダウンロード中に強制終了すると
                      キャッシュが壊れて-402になり、全システムに影響するため、余裕を持たせる)

    -IncludeOtherSystems を付けると、他システムの「時刻が重要でない定期タスク」の起動分も
    ずらす(下の$planの Group='Other')。時刻に意味がある可能性のあるもの(Predict・TrendLive・
    TrendMorning・TrendFinal・Verify・Morning・Watch・Backup・BackfillIncremental)は変えない。
    他システムの時刻に意図がある場合は、このスクリプトを実行する前に確認すること。

    実行中のタスクは変更しない(スキップして表示する)。タスク定義を置き換えると実行中の
    インスタンスが終了してしまうため(README「日中に登録し直すときの注意」参照)。

    時刻は「同時起動を避ける目安」。実際の所要時間は日によって大きく変わるため、重なりを
    確実に防ぐのは時刻ではなくJV-Link共通ロック(JvSessionLock.cs / run-with-jvlock.ps1)。

.EXAMPLE
    # 確認だけ
    powershell -ExecutionPolicy Bypass -File .\rebalance-schedule.ps1

.EXAMPLE
    # HRC側だけ変更
    powershell -ExecutionPolicy Bypass -File .\rebalance-schedule.ps1 -Apply

.EXAMPLE
    # 他システムの定期タスクの起動分もずらす
    powershell -ExecutionPolicy Bypass -File .\rebalance-schedule.ps1 -Apply -IncludeOtherSystems
#>
[CmdletBinding()]
param(
    [switch] $Apply,
    [switch] $IncludeOtherSystems
)

$ErrorActionPreference = 'Stop'

# 名前 / 起動時刻 / 繰り返し(分・時間。無ければ1日1回) / 週1回なら曜日 / 実行時間上限(分。無ければ変更しない)
$plan = @(
    @{ Name = 'HRC Score';    Group = 'HRC'; Times = @('07:26', '10:26', '13:26', '16:26'); LimitMin = 150 },
    @{ Name = 'HRC Backfill'; Group = 'HRC'; Times = @('03:30'); Weekly = 'Monday'; LimitMin = 210 },

    # 時刻に意味が無さそうな定期処理(監視・表示・生成系)の起動分だけをずらす。
    # :00/:30に集中していたPredict・TrendLiveとぶつからない分に置く。
    @{ Name = 'KeibaAiDataSystem-Content';     Group = 'Other'; Times = @('07:43'); EveryMin = 20; ForHours = 16 },
    @{ Name = 'KeibaAiDataSystem-HealthCheck'; Group = 'Other'; Times = @('08:37'); EveryMin = 15; ForHours = 14 },
    @{ Name = 'KeibaAiDataSystem-Dashboard';   Group = 'Other'; Times = @('07:11'); EveryMin = 30; ForHours = 15 },
    @{ Name = 'KeibaAiDataSystem-Score';       Group = 'Other'; Times = @('07:34'); EveryMin = 20; ForHours = 14 },
    @{ Name = 'HorseRacingPrediction';         Group = 'Other'; Times = @('09:06') }
)

function New-PlanTriggers($p) {
    if ($p.Weekly) {
        return @(New-ScheduledTaskTrigger -Weekly -DaysOfWeek $p.Weekly -At $p.Times[0])
    }
    $list = @()
    foreach ($t in $p.Times) {
        $trigger = New-ScheduledTaskTrigger -Daily -At $t
        if ($p.EveryMin) {
            $rep = (New-ScheduledTaskTrigger -Once -At $t `
                -RepetitionInterval (New-TimeSpan -Minutes $p.EveryMin) `
                -RepetitionDuration (New-TimeSpan -Hours $p.ForHours)).Repetition
            $trigger.Repetition = $rep
        }
        $list += $trigger
    }
    return $list
}

function Format-Triggers($triggers) {
    if (-not $triggers) { return '(なし)' }
    $parts = foreach ($t in $triggers) {
        $start = ([datetime]$t.StartBoundary).ToString('HH:mm')
        $rep = if ($t.Repetition -and $t.Repetition.Interval) { " 繰り返し $($t.Repetition.Interval)/$($t.Repetition.Duration)" } else { '' }
        "$start$rep"
    }
    return ($parts -join ' , ')
}

$targets = $plan | Where-Object { $_.Group -eq 'HRC' -or $IncludeOtherSystems }
$changed = 0

foreach ($p in $targets) {
    $task = Get-ScheduledTask -TaskName $p.Name -ErrorAction SilentlyContinue
    if (-not $task) {
        Write-Output "[見つかりません] $($p.Name) (名前が違う場合は`$planを直してください)"
        continue
    }

    $newTriggers = @(New-PlanTriggers $p)
    $limitText = ''
    if ($p.LimitMin) {
        $limitText = " / 実行時間上限 $($task.Settings.ExecutionTimeLimit) → $([System.Xml.XmlConvert]::ToString([TimeSpan]::FromMinutes($p.LimitMin)))"
    }

    Write-Output "[$($p.Name)]"
    Write-Output "   現在: $(Format-Triggers $task.Triggers)"
    Write-Output "   変更: $(Format-Triggers $newTriggers)$limitText"

    if ($task.State -eq 'Running') {
        Write-Output "   → 実行中のため変更しません。終了後にもう一度実行してください。"
        continue
    }
    if (-not $Apply) { continue }

    if ($p.LimitMin) {
        $settings = $task.Settings
        $settings.ExecutionTimeLimit = [System.Xml.XmlConvert]::ToString([TimeSpan]::FromMinutes($p.LimitMin))
        Set-ScheduledTask -TaskName $p.Name -Trigger $newTriggers -Settings $settings | Out-Null
    }
    else {
        Set-ScheduledTask -TaskName $p.Name -Trigger $newTriggers | Out-Null
    }
    Write-Output "   → 変更しました。"
    $changed++
}

Write-Output ""
if ($Apply) {
    Write-Output "完了: $changed 件を変更しました。確認: Get-ScheduledTask | Get-ScheduledTaskInfo | Select TaskName,NextRunTime"
}
else {
    Write-Output "確認のみです(何も変更していません)。変更するには -Apply を付けてください。"
}
