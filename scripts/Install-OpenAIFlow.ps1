[CmdletBinding()]
param(
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repoRoot 'ChatGptDictationBridge.csproj'
$publishDir = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\publish\win-x64'))
$installDir = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\OpenAIFlow'))
$dataDir = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'OpenAIFlow'))
$settingsPath = Join-Path $dataDir 'settings.json'
$installedExe = Join-Path $installDir 'OpenAIFlow.exe'

if (-not $publishDir.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Der Publish-Ordner liegt unerwartet außerhalb des Projekts: $publishDir"
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

& dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishProfile=WindowsSelfContained `
    -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "Der OpenAI-Flow-Publish ist fehlgeschlagen (Exitcode $LASTEXITCODE)."
}

$publishedExe = Join-Path $publishDir 'OpenAIFlow.exe'
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "Die veröffentlichte OpenAIFlow.exe wurde nicht gefunden."
}

New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
if (-not (Test-Path -LiteralPath $settingsPath)) {
    $legacyCandidates = @(
        (Join-Path $repoRoot 'bin\Release\net8.0-windows\settings.json'),
        (Join-Path $repoRoot 'settings.json')
    ) | Where-Object { Test-Path -LiteralPath $_ }

    $legacySettings = $legacyCandidates | Where-Object {
        try {
            (Get-Content -LiteralPath $_ -Raw | ConvertFrom-Json).setupCompleted -eq $true
        }
        catch {
            $false
        }
    } | Select-Object -First 1

    if (-not $legacySettings) {
        $legacySettings = $legacyCandidates | Select-Object -First 1
    }

    if ($legacySettings) {
        Copy-Item -LiteralPath $legacySettings -Destination $settingsPath
    }
}

$managedProcessNames = @('OpenAIFlow', 'ChatGptDictationBridge')
$managedProcesses = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    if ($managedProcessNames -notcontains $_.ProcessName) {
        return $false
    }

    try {
        $path = $_.Path
        return $path -and (
            $path.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase))
    }
    catch {
        return $false
    }
})

if ($managedProcesses.Count -gt 0) {
    $managedProcesses | Stop-Process -Force
    foreach ($process in $managedProcesses) {
        if (-not $process.WaitForExit(10000)) {
            throw "Die laufende App (PID $($process.Id)) konnte nicht rechtzeitig beendet werden."
        }
    }
}

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
$copyCompleted = $false
for ($attempt = 1; $attempt -le 20; $attempt++) {
    try {
        Copy-Item -LiteralPath $publishedExe -Destination $installedExe -Force
        $copyCompleted = $true
        break
    }
    catch [IO.IOException] {
        if ($attempt -eq 20) {
            throw
        }
        Start-Sleep -Milliseconds 250
    }
}
if (-not $copyCompleted) {
    throw 'Die installierte OpenAIFlow.exe konnte nicht aktualisiert werden.'
}

$desktop = [Environment]::GetFolderPath('Desktop')
if ([string]::IsNullOrWhiteSpace($desktop)) {
    throw 'Der Windows-Desktopordner konnte nicht ermittelt werden.'
}

$shortcutPath = Join-Path $desktop 'OpenAI Flow Dictation.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installedExe
$shortcut.WorkingDirectory = $installDir
$shortcut.IconLocation = "$installedExe,0"
$shortcut.Description = 'OpenAI Flow Dictation starten'
$shortcut.Save()

if (-not $NoLaunch) {
    Start-Process -FilePath $installedExe -WorkingDirectory $installDir
}

[PSCustomObject]@{
    Executable = $installedExe
    Shortcut = $shortcutPath
    Settings = $settingsPath
    Logs = (Join-Path $dataDir 'logs\app.log')
}
