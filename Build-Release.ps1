[CmdletBinding()]
param(
    [ValidateSet('Check','Publish')][string]$Mode = 'Publish',
    [string]$ProjectRoot = $PSScriptRoot,
    [string]$Output = (Join-Path $PSScriptRoot 'publish\SubReel-win-x64'),
    [string]$LauncherName = 'SubReel.Launcher',
    [string]$Icon = '',
    [ValidateSet('true','false')][string]$SelfContained = 'true',
    [ValidateSet('true','false')][string]$SingleFile = 'false',
    [ValidateSet('true','false')][string]$BuildInstaller = 'true'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$SelfContainedEnabled = $SelfContained -eq 'true'
$SingleFileEnabled = $SingleFile -eq 'true'
$BuildInstallerEnabled = $BuildInstaller -eq 'true'

function Write-Step([string]$Message) { Write-Host "`n=== $Message ===" -ForegroundColor Cyan }
function Invoke-DotNet([string[]]$Arguments) {
    Write-Host ('> dotnet ' + ($Arguments -join ' '))
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet завершился с кодом $LASTEXITCODE" }
}
function Get-ProjectVersion([string]$Csproj) {
    [xml]$xml = Get-Content -LiteralPath $Csproj -Raw
    $node = $xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $node) { return '0.1.0' }
    return [string]$node
}
function Assert-File([string]$Path, [string]$Description) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Не найден обязательный файл ($Description): $Path" }
}

$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
$solution = Join-Path $ProjectRoot 'SubReel.Launcher.Design.sln'
$launcherProject = Join-Path $ProjectRoot 'SubReel.Launcher.Design\Subreel.Launcher.Design.csproj'
$updaterProject = Join-Path $ProjectRoot 'SubReel.Updater\SubReel.Updater.csproj'
$installerScript = Join-Path $ProjectRoot 'installer\SubReel.iss'
foreach ($required in @($solution,$launcherProject,$updaterProject)) { Assert-File $required 'проект' }

if ($Mode -eq 'Check') {
    Write-Step 'Проверка сборки Release'
    Invoke-DotNet @('build',$solution,'--configuration','Release')
    Write-Step 'Запуск тестов'
    Invoke-DotNet @('test',$solution,'--configuration','Release','--no-build')
    Write-Host "`nПроверка завершена успешно." -ForegroundColor Green
    exit 0
}

if ($LauncherName -notmatch '^[A-Za-z0-9._-]+$') { throw 'Имя EXE содержит недопустимые символы.' }
if ($Icon -and -not (Test-Path -LiteralPath $Icon -PathType Leaf)) { throw "ICO-файл не найден: $Icon" }

$Output = [IO.Path]::GetFullPath($Output)
$rootPath = [IO.Path]::GetPathRoot($Output)
if ($Output.TrimEnd('\') -eq $rootPath.TrimEnd('\') -or
    $Output.Equals($ProjectRoot,[StringComparison]::OrdinalIgnoreCase) -or
    $ProjectRoot.StartsWith($Output.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Папка результата должна быть отдельной от проекта и не может быть корнем диска или родительской папкой проекта.'
}

$parent = Split-Path -Parent $Output
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$leaf = Split-Path -Leaf $Output
$token = [Guid]::NewGuid().ToString('N')
$stage = Join-Path $parent ('.' + $leaf + '.staging-' + $token)
$zipPath = Join-Path $parent ($leaf + '.zip')
$zipTemp = Join-Path $parent ('.' + $leaf + '.zip.tmp-' + $token)
$backup = Join-Path $parent ('.' + $leaf + '.backup-' + $token)
$zipBackup = Join-Path $parent ('.' + $leaf + '.zip.backup-' + $token)
$hashPath = $zipPath + '.sha256'
$hashBackup = $hashPath + '.backup-' + $token
$oldHashMoved = $false
$published = $false
$oldOutputMoved = $false
$oldZipMoved = $false
$newOutputMoved = $false
$newZipMoved = $false
$manifestPath = Join-Path $parent ($leaf + '.release.json')

try {
    Write-Step 'Сборка и тесты'
    Invoke-DotNet @('build',$solution,'--configuration','Release')
    Invoke-DotNet @('test',$solution,'--configuration','Release','--no-build')

    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    $runtime = 'win-x64'
    $version = Get-ProjectVersion $launcherProject

    Write-Step 'Публикация лаунчера'
    $launcherArgs = @('publish',$launcherProject,'--configuration','Release','--runtime',$runtime,'--output',$stage)
    if ($SelfContainedEnabled) { $launcherArgs += @('--self-contained','true') } else { $launcherArgs += @('--self-contained','false') }
    $launcherArgs += @('-p:LauncherAssemblyName=' + $LauncherName,'-p:PublishSingleFile=' + $SingleFileEnabled.ToString().ToLowerInvariant(),'-p:PublishTrimmed=false','-p:DebugType=None','-p:DebugSymbols=false')
    if ($SingleFileEnabled) { $launcherArgs += '-p:IncludeNativeLibrariesForSelfExtract=true' }
    if ($Icon) { $launcherArgs += ('-p:ApplicationIcon=' + $Icon) }
    Invoke-DotNet $launcherArgs

    Write-Step 'Отдельная автономная публикация обновлятора'
    $updaterOutput = Join-Path $stage 'Updater'
    New-Item -ItemType Directory -Path $updaterOutput -Force | Out-Null
    Invoke-DotNet @('publish',$updaterProject,'--configuration','Release','--runtime',$runtime,'--self-contained','true','--output',$updaterOutput,'-p:PublishSingleFile=false','-p:PublishTrimmed=false','-p:DebugType=None','-p:DebugSymbols=false')

    $launcherExe = Join-Path $stage ($LauncherName + '.exe')
    $updaterExe = Join-Path $updaterOutput 'SubReel.Updater.exe'
    Assert-File $launcherExe 'EXE лаунчера'
    Assert-File $updaterExe 'EXE обновлятора'
    Assert-File (Join-Path $updaterOutput 'SubReel.Updater.runtimeconfig.json') 'runtimeconfig обновлятора'
    if (-not $SingleFileEnabled -and -not (Get-ChildItem -LiteralPath $stage -File -Filter '*.deps.json')) { throw 'В публикации не найден deps.json лаунчера.' }

    Write-Step 'Подготовка релизной информации'
    $readme = @"
SubReel Launcher $version — Windows x64

Запуск: $LauncherName.exe
Обновлятор: Updater\SubReel.Updater.exe
Архитектура: win-x64
Self-contained launcher: $SelfContainedEnabled
Single-file launcher: $SingleFileEnabled

Распакуйте ВСЮ папку и запускайте EXE из неё. Не переносите EXE отдельно от DLL и папки Updater.
Пользовательские данные Minecraft, профили, моды и настройки должны храниться вне папки установки.
"@
    Set-Content -LiteralPath (Join-Path $stage 'README-RELEASE.txt') -Value $readme -Encoding UTF8
    $buildInfo = [ordered]@{
        product = 'SubReel Launcher'
        version = $version
        runtime = $runtime
        architecture = 'x64'
        selfContained = $SelfContainedEnabled
        singleFile = $SingleFileEnabled
        launcherExe = ($LauncherName + '.exe')
        updaterExe = 'Updater/SubReel.Updater.exe'
        builtUtc = [DateTime]::UtcNow.ToString('o')
    }
    $buildInfo | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'release-info.json') -Encoding UTF8

    Write-Step 'Создание ZIP'
    if (Test-Path -LiteralPath $zipTemp) { Remove-Item -LiteralPath $zipTemp -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage,$zipTemp,[IO.Compression.CompressionLevel]::Optimal,$false)
    $zipHash = (Get-FileHash -LiteralPath $zipTemp -Algorithm SHA256).Hash.ToLowerInvariant()
    $zipSize = (Get-Item -LiteralPath $zipTemp).Length
    Set-Content -LiteralPath ($zipTemp + '.sha256') -Value ($zipHash + '  ' + (Split-Path -Leaf $zipPath)) -Encoding ASCII
    $manifest = [ordered]@{
        version = $version
        file = (Split-Path -Leaf $zipPath)
        sha256 = $zipHash
        sizeBytes = $zipSize
        runtime = $runtime
        launcherExe = ($LauncherName + '.exe')
        updaterPath = 'Updater/SubReel.Updater.exe'
        builtUtc = [DateTime]::UtcNow.ToString('o')
    }

    # Transactional replacement: preserve the previous release if the final move fails.
    if (Test-Path -LiteralPath $Output) { Move-Item -LiteralPath $Output -Destination $backup; $oldOutputMoved = $true }
    if (Test-Path -LiteralPath $zipPath) { Move-Item -LiteralPath $zipPath -Destination $zipBackup; $oldZipMoved = $true }
    if (Test-Path -LiteralPath $hashPath) { Move-Item -LiteralPath $hashPath -Destination $hashBackup; $oldHashMoved = $true }
    Move-Item -LiteralPath $stage -Destination $Output
    $newOutputMoved = $true
    Move-Item -LiteralPath $zipTemp -Destination $zipPath
    $newZipMoved = $true
    Move-Item -LiteralPath ($zipTemp + '.sha256') -Destination $hashPath -Force
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    $published = $true
    if ($oldOutputMoved -and (Test-Path -LiteralPath $backup)) { Remove-Item -LiteralPath $backup -Recurse -Force }
    if ($oldZipMoved -and (Test-Path -LiteralPath $zipBackup)) { Remove-Item -LiteralPath $zipBackup -Force }
    if ($oldHashMoved -and (Test-Path -LiteralPath $hashBackup)) { Remove-Item -LiteralPath $hashBackup -Force }

    Write-Host "Папка: $Output" -ForegroundColor Green
    Write-Host "ZIP: $zipPath" -ForegroundColor Green
    Write-Host "SHA-256: $zipHash" -ForegroundColor Green

    if ($BuildInstallerEnabled) {
        $iscc = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
        if (-not $iscc) {
            $candidates = @(
                (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
                (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
            ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
            if ($candidates) { $iscc = [pscustomobject]@{ Source = $candidates[0] } }
        }
        if ($iscc -and (Test-Path -LiteralPath $installerScript)) {
            Write-Step 'Сборка установщика Inno Setup'
            $installerOutput = Join-Path $parent 'installer'
            New-Item -ItemType Directory -Path $installerOutput -Force | Out-Null
            & $iscc.Source "/DAppSource=$Output" "/DOutputDir=$installerOutput" "/DAppVersion=$version" "/DLauncherExe=$LauncherName.exe" $installerScript
            if ($LASTEXITCODE -ne 0) { throw "Inno Setup завершился с кодом $LASTEXITCODE. ZIP и папка публикации уже готовы." }
            Write-Host "Установщик: $installerOutput\SubReel-Setup-win-x64.exe" -ForegroundColor Green
        } else {
            Write-Warning 'Inno Setup не найден. Папка и ZIP созданы; для EXE-установщика установи Inno Setup 6 и повтори публикацию.'
        }
    }

    Write-Host "`nРелиз успешно подготовлен." -ForegroundColor Green
}
catch {
    if (-not $published) {
        if ($newOutputMoved -and (Test-Path -LiteralPath $Output)) { Remove-Item -LiteralPath $Output -Recurse -Force -ErrorAction SilentlyContinue }
        if ($newZipMoved -and (Test-Path -LiteralPath $zipPath)) { Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue }
        if ($oldOutputMoved -and (Test-Path -LiteralPath $backup)) { Move-Item -LiteralPath $backup -Destination $Output -Force }
        if ($oldZipMoved -and (Test-Path -LiteralPath $zipBackup)) { Move-Item -LiteralPath $zipBackup -Destination $zipPath -Force }
        if (Test-Path -LiteralPath $hashPath) { Remove-Item -LiteralPath $hashPath -Force -ErrorAction SilentlyContinue }
        if ($oldHashMoved -and (Test-Path -LiteralPath $hashBackup)) { Move-Item -LiteralPath $hashBackup -Destination $hashPath -Force }
    }
    throw
}
finally {
    foreach ($temporary in @($stage,$zipTemp,($zipTemp + '.sha256'))) {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue }
    }
}
