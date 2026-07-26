[CmdletBinding()]
param(
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repoRoot 'ORhom.csproj'
$publishDir = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\publish\win-x64'))
$installDir = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\ORhom'))
$dataDir = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'ORhom'))
$legacyInstallDirs = @(
    [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\OpenAIFlow')),
    [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\OliSpeechToText'))
)
$legacyDataDirs = @(
    [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'OpenAIFlow')),
    [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'OliSpeechToText'))
)
$settingsPath = Join-Path $dataDir 'settings.json'
$installedExe = Join-Path $installDir 'ORhom.exe'
$installedNotices = Join-Path $installDir 'THIRD-PARTY-NOTICES.md'
$desktop = [Environment]::GetFolderPath('Desktop')
$startMenuPrograms = [Environment]::GetFolderPath('Programs')

if ([string]::IsNullOrWhiteSpace($desktop)) {
    throw 'Der Windows-Desktopordner konnte nicht ermittelt werden.'
}
if ([string]::IsNullOrWhiteSpace($startMenuPrograms)) {
    throw 'Der Windows-Startmenüordner konnte nicht ermittelt werden.'
}

$legacyStandaloneExecutables = @(
    [IO.Path]::GetFullPath((Join-Path $desktop 'SpeechToText.exe'))
)
$legacyExecutableNames = @(
    'OpenAIFlow.exe',
    'OliSpeechToText.exe',
    'SpeechToText.exe',
    'ChatGptDictationBridge.exe'
)
$legacyProductMetadata = @(
    'OpenAIFlow',
    'OpenAI Flow Dictation',
    'OliSpeechToText',
    'SpeechToText',
    'ChatGptDictationBridge'
)

function Test-IsPathWithinDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$CandidatePath,

        [Parameter(Mandatory)]
        [string]$DirectoryPath
    )

    $candidateFullPath = [IO.Path]::GetFullPath($CandidatePath)
    $directoryFullPath = [IO.Path]::GetFullPath($DirectoryPath)
    $directoryPrefix = $directoryFullPath.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

    return $candidateFullPath.Equals(
        $directoryFullPath,
        [StringComparison]::OrdinalIgnoreCase) -or
        $candidateFullPath.StartsWith(
            $directoryPrefix,
            [StringComparison]::OrdinalIgnoreCase)
}

function Test-IsLegacyExecutablePath {
    param(
        [AllowNull()]
        [string]$CandidatePath
    )

    if ([string]::IsNullOrWhiteSpace($CandidatePath)) {
        return $false
    }

    try {
        $candidateFullPath = [IO.Path]::GetFullPath($CandidatePath)
    }
    catch {
        return $false
    }

    $isLegacyStandalonePath = [bool]($legacyStandaloneExecutables | Where-Object {
        $_.Equals($candidateFullPath, [StringComparison]::OrdinalIgnoreCase)
    })
    if ($isLegacyStandalonePath) {
        if (-not (Test-Path -LiteralPath $candidateFullPath -PathType Leaf)) {
            return $true
        }

        try {
            $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($candidateFullPath)
            return [bool](@(
                $versionInfo.ProductName,
                $versionInfo.FileDescription,
                $versionInfo.InternalName,
                $versionInfo.OriginalFilename
            ) | Where-Object {
                $metadata = $_
                $legacyProductMetadata | Where-Object {
                    $legacyMetadataValue = $_
                    $metadata -and (
                        $metadata.Equals($legacyMetadataValue, [StringComparison]::OrdinalIgnoreCase) -or
                        $metadata.Equals("$legacyMetadataValue.exe", [StringComparison]::OrdinalIgnoreCase) -or
                        $metadata.Equals("$legacyMetadataValue.dll", [StringComparison]::OrdinalIgnoreCase))
                }
            })
        }
        catch {
            return $false
        }
    }

    if ($legacyExecutableNames -notcontains [IO.Path]::GetFileName($candidateFullPath)) {
        return $false
    }

    return [bool]($legacyInstallDirs | Where-Object {
        Test-IsPathWithinDirectory -CandidatePath $candidateFullPath -DirectoryPath $_
    })
}

if (-not $publishDir.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Der Publish-Ordner liegt unerwartet außerhalb des Projekts: $publishDir"
}

$managedProcessNames = @(
    'ORhom',
    'OpenAIFlow',
    'OliSpeechToText',
    'SpeechToText',
    'ChatGptDictationBridge'
)
$managedRoots = @($repoRoot, $installDir) + $legacyInstallDirs
$managedProcesses = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    if ($managedProcessNames -notcontains $_.ProcessName) {
        return $false
    }

    try {
        $path = $_.Path
        return $path -and (
            [bool]($managedRoots | Where-Object {
                Test-IsPathWithinDirectory -CandidatePath $path -DirectoryPath $_
            }) -or
            (Test-IsLegacyExecutablePath -CandidatePath $path))
    }
    catch {
        return $false
    }
})

if ($managedProcesses.Count -gt 0) {
    $processIds = ($managedProcesses | ForEach-Object { $_.Id }) -join ', '
    throw "ORhom läuft noch (PID: $processIds). Bitte zuerst über das Tray-Menü 'Beenden' wählen und die Installation erneut starten. Ein erzwungener Abbruch könnte eine Aufnahme oder die Audiolautstärke in einem inkonsistenten Zustand hinterlassen."
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

& dotnet restore $projectPath -r win-x64 -m:1 --locked-mode
if ($LASTEXITCODE -ne 0) {
    throw "Die ORhom-Abhängigkeiten konnten nicht reproduzierbar wiederhergestellt werden (Exitcode $LASTEXITCODE)."
}

& dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    --nologo `
    -p:TreatWarningsAsErrors=true `
    -p:PublishProfile=WindowsSelfContained `
    -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "Der ORhom-Publish ist fehlgeschlagen (Exitcode $LASTEXITCODE)."
}

$publishedExe = Join-Path $publishDir 'ORhom.exe'
$publishedNotices = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md'
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "Die veröffentlichte ORhom.exe wurde nicht gefunden."
}
if (-not (Test-Path -LiteralPath $publishedNotices)) {
    throw "Die Drittanbieterhinweise wurden im Repository nicht gefunden."
}
$expectedPublishedFiles = @('ORhom.exe')
$publishPrefix = $publishDir.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$actualPublishedFiles = @(
    Get-ChildItem -LiteralPath $publishDir -File -Recurse |
        ForEach-Object { $_.FullName.Substring($publishPrefix.Length) }
)
$publishDifference = @(
    Compare-Object -ReferenceObject $expectedPublishedFiles -DifferenceObject $actualPublishedFiles
)
if ($publishDifference.Count -ne 0) {
    throw "Unerwarteter Publish-Inhalt: $($actualPublishedFiles -join ', ')"
}
if ((Get-Item -LiteralPath $publishedExe).Length -le 0 -or
    (Get-Item -LiteralPath $publishedNotices).Length -le 0) {
    throw 'Die veröffentlichte ORhom.exe oder die Drittanbieterhinweise sind leer.'
}
$publishedVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExe)
if ($publishedVersionInfo.ProductName -ne 'ORhom' -or
    $publishedVersionInfo.FileDescription -ne 'ORhom') {
    throw 'Die veröffentlichte EXE trägt nicht vollständig den Produktnamen ORhom.'
}

if (-not (Test-Path -LiteralPath $dataDir)) {
    foreach ($legacyDataDir in $legacyDataDirs) {
        if (Test-Path -LiteralPath $legacyDataDir) {
            Move-Item -LiteralPath $legacyDataDir -Destination $dataDir
            break
        }
    }
}
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
if (-not (Test-Path -LiteralPath $settingsPath)) {
    $legacyCandidates = @(
        $legacyDataDirs | ForEach-Object { Join-Path $_ 'settings.json' }
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

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
$copyCompleted = $false
for ($attempt = 1; $attempt -le 20; $attempt++) {
    $temporaryInstalledExe = Join-Path $installDir ".ORhom.$([Guid]::NewGuid().ToString('N')).tmp"
    $backupInstalledExe = Join-Path $installDir ".ORhom.$([Guid]::NewGuid().ToString('N')).bak"
    try {
        Copy-Item -LiteralPath $publishedExe -Destination $temporaryInstalledExe
        if (Test-Path -LiteralPath $installedExe) {
            [IO.File]::Replace($temporaryInstalledExe, $installedExe, $backupInstalledExe, $true)
        }
        else {
            [IO.File]::Move($temporaryInstalledExe, $installedExe)
        }

        $copyCompleted = $true
        break
    }
    catch [IO.IOException] {
        Remove-Item -LiteralPath $temporaryInstalledExe -Force -ErrorAction SilentlyContinue
        if ($attempt -eq 20) {
            throw
        }
        Start-Sleep -Milliseconds 250
    }
    finally {
        Remove-Item -LiteralPath $temporaryInstalledExe -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $backupInstalledExe -Force -ErrorAction SilentlyContinue
    }
}
if (-not $copyCompleted) {
    throw 'Die installierte ORhom.exe konnte nicht aktualisiert werden.'
}
Copy-Item -LiteralPath $publishedNotices -Destination $installedNotices -Force

$shell = New-Object -ComObject WScript.Shell
$desktopShortcutPath = Join-Path $desktop 'ORhom.lnk'
$startMenuShortcutPath = Join-Path $startMenuPrograms 'ORhom.lnk'
foreach ($shortcutPath in @($desktopShortcutPath, $startMenuShortcutPath)) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $installedExe
    $shortcut.WorkingDirectory = $installDir
    $shortcut.IconLocation = "$installedExe,0"
    $shortcut.Description = 'ORhom starten'
    $shortcut.Save()
}

$taskbarPins = if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
    $null
}
else {
    Join-Path $env:APPDATA 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
}
$legacyShortcutNames = @(
    'OpenAI Flow Dictation.lnk',
    'OliSpeechToText.lnk',
    'SpeechToText.lnk'
)
$legacyShortcutDirectories = @($desktop, $startMenuPrograms)
if ($taskbarPins) {
    $legacyShortcutDirectories += $taskbarPins
}

$taskbarRepinRequired = $false
foreach ($shortcutDirectory in $legacyShortcutDirectories) {
    foreach ($legacyShortcutName in $legacyShortcutNames) {
        $legacyShortcutPath = Join-Path $shortcutDirectory $legacyShortcutName
        if (-not (Test-Path -LiteralPath $legacyShortcutPath -PathType Leaf)) {
            continue
        }

        try {
            $legacyShortcutTarget = $shell.CreateShortcut($legacyShortcutPath).TargetPath
        }
        catch {
            continue
        }

        if (-not (Test-IsLegacyExecutablePath -CandidatePath $legacyShortcutTarget)) {
            continue
        }

        if ($taskbarPins -and
            (Test-IsPathWithinDirectory -CandidatePath $legacyShortcutPath -DirectoryPath $taskbarPins)) {
            $taskbarRepinRequired = $true
        }

        Remove-Item -LiteralPath $legacyShortcutPath -Force
    }
}

$legacyInstalledExecutables = @(
    foreach ($legacyInstallDir in $legacyInstallDirs) {
        foreach ($legacyExecutableName in $legacyExecutableNames) {
            Join-Path $legacyInstallDir $legacyExecutableName
        }
    }
) + $legacyStandaloneExecutables
foreach ($legacyInstalledExecutable in $legacyInstalledExecutables) {
    if (-not (Test-Path -LiteralPath $legacyInstalledExecutable -PathType Leaf)) {
        continue
    }

    if (-not (Test-IsLegacyExecutablePath -CandidatePath $legacyInstalledExecutable)) {
        Write-Warning "Eine nicht eindeutig ORhom zuordenbare Datei bleibt unangetastet: $legacyInstalledExecutable"
        continue
    }

    Remove-Item -LiteralPath $legacyInstalledExecutable -Force
}

foreach ($legacyInstallDir in $legacyInstallDirs) {
    if ((Test-Path -LiteralPath $legacyInstallDir -PathType Container) -and
        @(Get-ChildItem -LiteralPath $legacyInstallDir -Force).Count -eq 0) {
        Remove-Item -LiteralPath $legacyInstallDir -Force
    }
}

if ($taskbarRepinRequired) {
    Write-Warning 'Die alte Taskleisten-Anheftung wurde entfernt. Bitte ORhom nach dem Start erneut an die Taskleiste anheften.'
}

if (-not $NoLaunch) {
    Start-Process -FilePath $installedExe -WorkingDirectory $installDir
}

[PSCustomObject]@{
    Executable = $installedExe
    ThirdPartyNotices = $installedNotices
    DesktopShortcut = $desktopShortcutPath
    StartMenuShortcut = $startMenuShortcutPath
    TaskbarRepinRequired = $taskbarRepinRequired
    Settings = $settingsPath
    Logs = (Join-Path $dataDir 'logs\app.log')
}
