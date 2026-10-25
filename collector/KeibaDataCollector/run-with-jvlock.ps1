<#
.SYNOPSIS
    他システムのバッチ(.bat)を、JV-Link/UmaConn共通ロックを取ってから実行する。

.DESCRIPTION
    同じVPSで動く複数のシステムが同時にJV-Link/UmaConnを使うと、Initの失敗や極端な遅延が起きる
    (2026-10-25に実際に発生)。このリポジトリのKeibaDataCollector.exeは、コード内で同じロック
    (JvSessionLock.cs)を取るので、このラッパーは不要。コードを直せない・直していない
    他システムのタスクを、外側から包んで順番待ちにするためのもの。

    ロックファイルは JvSessionLock.cs と同じ場所・同じ方式なので、双方が協調して排他になる。
      既定: C:\ProgramData\KeibaJvLock\jvlink.lock  (環境変数 JV_LOCK_PATH で変更可)

    待ちには上限がある。上限を超えたらロックなしで実行する(取得が止まりきらないための安全弁)。

    注意:
      - バッチの実行中ずっとロックを持つ。JV-Link/UmaConnを使わないタスク(死活監視・
        ダッシュボード・バックアップ等)は包まないこと。包むと不要な順番待ちが生じる。
      - 起動しっぱなしの常駐タスク(watch等)は包まないこと。他が全て待たされる。
      - このリポジトリのKeibaDataCollector.exeを動かすタスクは包まないこと
        (exe自身が同じロックを取るため、二重に待って上限まで固まる)。

.PARAMETER Bat
    実行するバッチのフルパス。

.PARAMETER BatArgs
    バッチへ渡す引数(任意)。

.PARAMETER WaitMinutes
    ロックを待つ上限(分)。既定30。

.EXAMPLE
    # タスクスケジューラの「操作」を次のように変える(プログラム=powershell.exe)
    #   引数: -NoProfile -ExecutionPolicy Bypass -File C:\horse-race-custom-builder\collector\KeibaDataCollector\run-with-jvlock.ps1 -Bat "C:\keiba-ai-data-system\src\KeibaDataCollector\scheduled-score.bat"
    #   開始(オプション): 元の操作と同じ作業フォルダ
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Bat,
    [string[]] $BatArgs = @(),
    [int] $WaitMinutes = 30,
    [string] $LockPath = $(if ($env:JV_LOCK_PATH) { $env:JV_LOCK_PATH } else { Join-Path $env:ProgramData 'KeibaJvLock\jvlink.lock' })
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Bat)) {
    Write-Error "バッチが見つかりません: $Bat"
    exit 2
}

function Get-LockOwner {
    try {
        $fs = [System.IO.File]::Open($LockPath, 'Open', 'Read', 'ReadWrite')
        $sr = New-Object System.IO.StreamReader($fs)
        try { $text = $sr.ReadToEnd().Trim(); if ($text) { $text } else { '(不明)' } }
        finally { $sr.Dispose(); $fs.Dispose() }
    }
    catch { '(不明)' }
}

$lock = $null
try {
    $dir = Split-Path -Parent $LockPath
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $started = Get-Date
    $deadline = $started.AddMinutes($WaitMinutes)
    $noticed = $false

    while ($null -eq $lock) {
        try {
            # 書き込み付きで開く＋他者には読み取りだけ許す。これで他のプロセスは同時に開けない。
            $lock = [System.IO.File]::Open($LockPath, 'OpenOrCreate', 'ReadWrite', 'Read')
        }
        catch [System.IO.IOException] {
            if ((Get-Date) -ge $deadline) {
                Write-Warning "[jvlock] ${WaitMinutes}分待っても取れなかったため、ロックなしで実行します。保持側: $(Get-LockOwner)"
                break
            }
            if (-not $noticed) {
                Write-Output "[jvlock] 他の処理がJV-Link/UmaConnを使用中のため待機します。保持側: $(Get-LockOwner)"
                $noticed = $true
            }
            Start-Sleep -Seconds 2
        }
    }
}
catch {
    # 権限不足など、待っても直らないもの。ロックなしで続行する(取得を止めない)。
    Write-Warning "[jvlock] ロックを扱えないため、ロックなしで実行します: $($_.Exception.Message)"
    $lock = $null
}

if ($lock) {
    try {
        $waited = [int]((Get-Date) - $started).TotalSeconds
        if ($noticed) { Write-Output "[jvlock] 取得しました(待ち ${waited}秒)。" }
        $bytes = [System.Text.Encoding]::UTF8.GetBytes("run-with-jvlock $Bat pid=$PID $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
        $lock.SetLength(0)
        $lock.Write($bytes, 0, $bytes.Length)
        $lock.Flush()
    }
    catch { }
}

$exitCode = 0
try {
    $argText = ($BatArgs | ForEach-Object { $_ }) -join ' '
    & cmd.exe /c "`"$Bat`" $argText"
    $exitCode = $LASTEXITCODE
}
finally {
    if ($lock) {
        try { $lock.SetLength(0) } catch { }
        $lock.Dispose()
    }
}

exit $exitCode
