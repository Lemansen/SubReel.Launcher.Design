param(
    [string]$ProjectRoot = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

# Нормализуем путь (без завершающего слеша) и ищем solution в подпапках.
$ProjectRoot = [System.IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')
$solutionFile = Get-ChildItem -LiteralPath $ProjectRoot -Filter 'SubReel.Launcher.Design.sln' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $solutionFile) {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show(
        "Не найден SubReel.Launcher.Design.sln внутри:`n$ProjectRoot",
        'SubReel Build Tool', 'OK', 'Error'
    ) | Out-Null
    exit 1
}
$solution = $solutionFile.FullName
$ProjectRoot = $solutionFile.DirectoryName

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$script:process = $null
$script:busy = $false
$script:logPath = $null
$script:commandPath = $null
$script:logOffset = 0L
$script:taskResult = $null
$script:taskMode = $null
$script:publishOutput = $null
$script:stagingOutput = $null
$script:archivePath = $null

$form = New-Object System.Windows.Forms.Form
$form.Text = 'SubReel • Build Center'
$form.Size = New-Object System.Drawing.Size(880, 700)
$form.MinimumSize = New-Object System.Drawing.Size(800, 650)
$form.StartPosition = 'CenterScreen'
$form.BackColor = [Drawing.Color]::FromArgb(17, 19, 29)
$form.ForeColor = [Drawing.Color]::FromArgb(235, 237, 247)
$form.Font = New-Object Drawing.Font('Segoe UI', 9)

function New-Label($text, $x, $y, $w, $h=24, $size=9, $color=$form.ForeColor) {
    $c = New-Object Windows.Forms.Label
    $c.Text = $text; $c.Location = New-Object Drawing.Point($x,$y)
    $c.Size = New-Object Drawing.Size($w,$h)
    $c.ForeColor = $color
    $c.Font = New-Object Drawing.Font('Segoe UI', $size)
    $form.Controls.Add($c); return $c
}
function New-Button($text, $x, $y, $w, $h=38, $primary=$false) {
    $b = New-Object Windows.Forms.Button
    $b.Text = $text; $b.Location = New-Object Drawing.Point($x,$y)
    $b.Size = New-Object Drawing.Size($w,$h)
    $b.FlatStyle = 'Flat'; $b.FlatAppearance.BorderSize = 0
    $b.BackColor = if ($primary) {[Drawing.Color]::FromArgb(105,82,220)} else {[Drawing.Color]::FromArgb(39,43,60)}
    $b.ForeColor = [Drawing.Color]::White
    $b.Font = New-Object Drawing.Font('Segoe UI Semibold', 9)
    $form.Controls.Add($b); return $b
}
function New-TextBox($text, $x, $y, $w) {
    $t = New-Object Windows.Forms.TextBox
    $t.Text = $text; $t.Location = New-Object Drawing.Point($x,$y)
    $t.Size = New-Object Drawing.Size($w,26)
    $t.BackColor = [Drawing.Color]::FromArgb(29,32,46)
    $t.ForeColor = [Drawing.Color]::White
    $t.BorderStyle = 'FixedSingle'
    $form.Controls.Add($t); return $t
}
function Add-Log([string]$line) {
    if ($logBox.IsDisposed) { return }
    $logBox.AppendText($line + [Environment]::NewLine)
    $logBox.SelectionStart = $logBox.TextLength
    $logBox.ScrollToCaret()
}
function Set-Status([string]$value) { $status.Text = $value }

# Process output is written to a file by cmd.exe. The WinForms timer reads it on
# the UI thread, so no PowerShell ScriptBlock is ever executed on a worker thread.
$script:pollTimer = New-Object Windows.Forms.Timer
$script:pollTimer.Interval = 300
$script:pollTimer.Add_Tick({
    if ($script:logPath -and (Test-Path -LiteralPath $script:logPath)) {
        try {
            $content = [System.IO.File]::ReadAllText($script:logPath, [System.Text.Encoding]::UTF8)
            if ($content.Length -gt $script:logOffset) {
                $chunk = $content.Substring([int]$script:logOffset)
                $script:logOffset = $content.Length
                if ($chunk) { $logBox.AppendText($chunk) }
                $logBox.SelectionStart = $logBox.TextLength
                $logBox.ScrollToCaret()
            }
        } catch { }
    }

    if ($script:process -and $script:process.HasExited) {
        $script:pollTimer.Stop()
        # Read once more to capture the final bytes written before process exit.
        if ($script:logPath -and (Test-Path -LiteralPath $script:logPath)) {
            try {
                $content = [System.IO.File]::ReadAllText($script:logPath, [System.Text.Encoding]::UTF8)
                if ($content.Length -gt $script:logOffset) {
                    $logBox.AppendText($content.Substring([int]$script:logOffset))
                    $logBox.SelectionStart = $logBox.TextLength
                    $logBox.ScrollToCaret()
                }
            } catch { }
        }
        $exitCode = $script:process.ExitCode
        $script:process.Dispose()
        $script:process = $null
        $script:busy = $false
        $progress.Style = 'Blocks'
        $checkButton.Enabled = $true
        $publishButton.Enabled = $true

        if ($exitCode -eq 0) {
            Set-Status 'Готово'
            Add-Log ("`r`n" + [string]$script:taskResult)
        } else {
            Set-Status 'Завершено с ошибкой'
            Add-Log ("`r`nОШИБКА: процесс сборки завершился с кодом $exitCode. Подробности выше.")
            if ($script:stagingOutput -and (Test-Path -LiteralPath $script:stagingOutput)) {
                Remove-Item -LiteralPath $script:stagingOutput -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        foreach ($file in @($script:commandPath, $script:logPath)) {
            if ($file -and (Test-Path -LiteralPath $file)) {
                Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
            }
        }
        $script:commandPath = $null
        $script:logPath = $null
    }
})

function Start-Task([string]$mode, [hashtable]$settings = @{}) {
    if ($script:busy) { return }

    $script:busy = $true
    $script:taskMode = $mode
    $script:publishOutput = $null
    $script:stagingOutput = $null
    $script:archivePath = $null
    $checkButton.Enabled = $false
    $publishButton.Enabled = $false
    $progress.Style = 'Marquee'
    $status.Text = 'Выполняется...'
    $logBox.Clear()
    $script:logOffset = 0L

    try {
        $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
        $psCommand = Get-Command pwsh.exe -ErrorAction SilentlyContinue
        if ($psCommand) { $powerShell = $psCommand.Source }
        else { $powerShell = (Get-Command powershell.exe -ErrorAction Stop).Source }

        $releaseScript = Join-Path $ProjectRoot 'Build-Release.ps1'
        if (-not (Test-Path -LiteralPath $releaseScript)) { throw "Не найден Build-Release.ps1: $releaseScript" }

        $tempId = [Guid]::NewGuid().ToString('N')
        $script:logPath = Join-Path ([IO.Path]::GetTempPath()) ("SubReel-Build-$tempId.log")
        $script:commandPath = Join-Path ([IO.Path]::GetTempPath()) ("SubReel-Build-$tempId.cmd")
        $commands = New-Object System.Collections.Generic.List[string]
        $commands.Add('@echo off')
        $commands.Add('chcp 65001 >nul')
        $commands.Add('cd /d "' + $ProjectRoot + '"')

        if ($mode -eq 'check') {
            $cmd = '"' + $powerShell + '" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $releaseScript + '" -Mode Check -ProjectRoot "' + $ProjectRoot + '"'
            $script:taskResult = 'Проверка завершена успешно: Build и Test прошли без ошибок.'
        } else {
            $out = [IO.Path]::GetFullPath($settings.Output)
            $safeName = $settings.Name.Trim()
            $boolSelf = ([bool]$settings.SelfContained).ToString().ToLowerInvariant()
            $boolSingle = ([bool]$settings.SingleFile).ToString().ToLowerInvariant()
            $boolInstaller = ([bool]$settings.BuildInstaller).ToString().ToLowerInvariant()
            $cmd = '"' + $powerShell + '" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $releaseScript + '" -Mode Publish -ProjectRoot "' + $ProjectRoot + '" -Output "' + $out + '" -LauncherName "' + $safeName + '" -SelfContained ' + $boolSelf + ' -SingleFile ' + $boolSingle + ' -BuildInstaller ' + $boolInstaller
            if ($settings.Icon) { $cmd += ' -Icon "' + $settings.Icon + '"' }
            $script:taskResult = "Релиз собран. Папка и ZIP созданы в: $out"
        }
        $commands.Add($cmd + ' >> "' + $script:logPath + '" 2>&1')
        $commands.Add('exit /b %errorlevel%')
        [IO.File]::WriteAllLines($script:commandPath, $commands, [Text.Encoding]::ASCII)

        $psi = New-Object Diagnostics.ProcessStartInfo
        $psi.FileName = $env:ComSpec
        $psi.Arguments = '/d /c "' + $script:commandPath + '"'
        $psi.WorkingDirectory = $ProjectRoot
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true
        $script:process = New-Object Diagnostics.Process
        $script:process.StartInfo = $psi
        if (-not $script:process.Start()) { throw 'Не удалось запустить Build-Release.ps1.' }
        Add-Log ("> Build Center: $mode")
        Add-Log ("Журнал: $script:logPath")
        Add-Log 'Сборка выполняется в отдельной временной папке; существующий релиз сохраняется до успешной проверки.'
        $script:pollTimer.Start()
    } catch {
        $script:busy = $false
        $progress.Style = 'Blocks'
        $checkButton.Enabled = $true
        $publishButton.Enabled = $true
        Set-Status 'Ошибка запуска'
        Add-Log ("ОШИБКА: " + $_.Exception.Message)
        foreach ($file in @($script:commandPath, $script:logPath)) {
            if ($file -and (Test-Path -LiteralPath $file)) { Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue }
        }
        $script:commandPath = $null
        $script:logPath = $null
    }
}

# Header
New-Label 'SUBREEL' 24 18 150 28 17 ([Drawing.Color]::FromArgb(177,157,255)) | Out-Null
New-Label 'BUILD CENTER  /  .NET 10' 145 23 300 22 9 ([Drawing.Color]::FromArgb(145,150,170)) | Out-Null
New-Label 'Проект' 24 57 80 20 8 ([Drawing.Color]::FromArgb(145,150,170)) | Out-Null
New-Label 'SubReel.Launcher.Design.sln' 24 77 500 24 10 | Out-Null

# Build check
$panel1 = New-Object Windows.Forms.Panel
$panel1.Location = New-Object Drawing.Point(24,112); $panel1.Size = New-Object Drawing.Size(812,105)
$panel1.BackColor = [Drawing.Color]::FromArgb(25,28,41); $form.Controls.Add($panel1)
$l1 = New-Object Windows.Forms.Label
$l1.Text='1. Проверка проекта'; $l1.Location=New-Object Drawing.Point(16,12); $l1.Size=New-Object Drawing.Size(300,24)
$l1.Font=New-Object Drawing.Font('Segoe UI Semibold',11); $l1.ForeColor=$form.ForeColor; $panel1.Controls.Add($l1)
$l2 = New-Object Windows.Forms.Label
$l2.Text='Сначала dotnet build, затем dotnet test. При ошибке процесс остановится.'
$l2.Location=New-Object Drawing.Point(16,43); $l2.Size=New-Object Drawing.Size(550,22); $l2.ForeColor=[Drawing.Color]::FromArgb(160,165,184); $panel1.Controls.Add($l2)
$checkButton = New-Button 'Проверить Build + Test' 600 31 190 42 $true
$panel1.Controls.Add($checkButton); $checkButton.Location=New-Object Drawing.Point(600,31)

# Publish settings
New-Label '2. Сборка приложения и ZIP-пакета' 24 232 480 26 12 | Out-Null
New-Label 'Название EXE' 24 266 150 20 8 ([Drawing.Color]::FromArgb(155,160,180)) | Out-Null
$nameBox = New-TextBox 'SubReel.Launcher' 24 288 300
New-Label 'Иконка приложения (.ico, необязательно)' 350 266 400 20 8 ([Drawing.Color]::FromArgb(155,160,180)) | Out-Null
$iconBox = New-TextBox '' 350 288 390
$browseIcon = New-Button 'Обзор' 750 286 86 29
$browseIcon.Add_Click({
    $d=New-Object Windows.Forms.OpenFileDialog
    $d.Filter='Иконка Windows (*.ico)|*.ico'
    if ($d.ShowDialog() -eq 'OK') {$iconBox.Text=$d.FileName}
})
New-Label 'Папка результата (папка + ZIP рядом)' 24 326 280 20 8 ([Drawing.Color]::FromArgb(155,160,180)) | Out-Null
$outBox = New-TextBox (Join-Path $ProjectRoot 'publish\SubReel-win-x64') 24 348 716
$browseOut = New-Button 'Обзор' 750 346 86 29
$browseOut.Add_Click({
    $d=New-Object Windows.Forms.FolderBrowserDialog
    $d.SelectedPath=$outBox.Text
    if ($d.ShowDialog() -eq 'OK') {$outBox.Text=$d.SelectedPath}
})
$selfBox = New-Object Windows.Forms.CheckBox
$selfBox.Text='Self-contained (включить .NET Runtime в публикацию)'
$selfBox.Location='24,390'; $selfBox.Size='420,24'; $selfBox.Checked=$true
$selfBox.ForeColor=$form.ForeColor; $selfBox.BackColor=$form.BackColor; $form.Controls.Add($selfBox)
$singleBox = New-Object Windows.Forms.CheckBox
$singleBox.Text='Single file (один EXE, опционально)'
$singleBox.Location='460,390'; $singleBox.Size='350,24'; $singleBox.Checked=$false
$singleBox.ForeColor=$form.ForeColor; $singleBox.BackColor=$form.BackColor; $form.Controls.Add($singleBox)
$installerBox = New-Object Windows.Forms.CheckBox
$installerBox.Text='Создать EXE-установщик (нужен Inno Setup 6)'
$installerBox.Location='24,416'; $installerBox.Size='500,24'; $installerBox.Checked=$true
$installerBox.ForeColor=$form.ForeColor; $installerBox.BackColor=$form.BackColor; $form.Controls.Add($installerBox)

$publishButton = New-Button 'Собрать релиз' 24 448 220 42 $true
$exitButton = New-Button 'Выйти' 750 448 86 42
$publishButton.Add_Click({
    $n=$nameBox.Text.Trim()
    if ($n -notmatch '^[A-Za-z0-9._-]+$') {
        [Windows.Forms.MessageBox]::Show('Название EXE: только латинские буквы, цифры, точка, дефис и подчёркивание.','Проверка','OK','Warning') | Out-Null
        return
    }
    if ($iconBox.Text -and -not (Test-Path $iconBox.Text)) {
        [Windows.Forms.MessageBox]::Show('Выбранный ICO-файл не найден.','Проверка','OK','Warning') | Out-Null
        return
    }
    Start-Task 'publish' @{
        Name=$n; Icon=$iconBox.Text; Output=$outBox.Text
        SelfContained=$selfBox.Checked; SingleFile=$singleBox.Checked; BuildInstaller=$installerBox.Checked
    }
})
$checkButton.Add_Click({ Start-Task 'check' })
$exitButton.Add_Click({ $form.Close() })

# Output
New-Label 'Журнал выполнения' 24 500 250 22 9 ([Drawing.Color]::FromArgb(155,160,180)) | Out-Null
$status = New-Object Windows.Forms.Label
$status.Text='Ожидание'; $status.Location=New-Object Drawing.Point(690,500); $status.Size=New-Object Drawing.Size(146,22)
$status.TextAlign='MiddleRight'; $status.ForeColor=[Drawing.Color]::FromArgb(177,157,255); $form.Controls.Add($status)
$logBox = New-Object Windows.Forms.TextBox
$logBox.Location='24,526'; $logBox.Size='812,115'; $logBox.Multiline=$true
$logBox.ReadOnly=$true; $logBox.ScrollBars='Vertical'
$logBox.BackColor=[Drawing.Color]::FromArgb(10,12,19)
$logBox.ForeColor=[Drawing.Color]::FromArgb(205,210,225)
$logBox.Font=New-Object Drawing.Font('Consolas',8.5)
$form.Controls.Add($logBox)
$progress = New-Object Windows.Forms.ProgressBar
$progress.Location='24,650'; $progress.Size='812,8'; $progress.Style='Blocks'
$form.Controls.Add($progress)

$form.Add_FormClosing({
    if ($script:busy) {
        $r=[Windows.Forms.MessageBox]::Show('Сборка ещё выполняется. Всё равно завершить процесс и выйти?','SubReel Build Tool','YesNo','Warning')
        if ($r -ne 'Yes') { $_.Cancel=$true; return }
        if ($script:process -and -not $script:process.HasExited) {
            try { $script:process.Kill() } catch { }
        }
    }
    if ($script:pollTimer) { $script:pollTimer.Stop(); $script:pollTimer.Dispose() }
})
[void]$form.ShowDialog()
