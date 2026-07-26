[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$targetName = 'ORhom'
$targetRemote = 'https://github.com/Oliveronworking/ORhom.git'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoParent = [IO.Directory]::GetParent($repoRoot)

if ($null -eq $repoParent) {
    throw "Der uebergeordnete Ordner von '$repoRoot' konnte nicht ermittelt werden."
}

$destination = [IO.Path]::GetFullPath((Join-Path $repoParent.FullName $targetName))
$repoPrefix = $repoRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$currentDirectory = [IO.Path]::GetFullPath((Get-Location).Path)

if ($currentDirectory.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $currentDirectory.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Starte das Skript aus '$($repoParent.FullName)', nicht aus dem Repository."
}

if (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.git') -PathType Container) -or
    -not (Test-Path -LiteralPath (Join-Path $repoRoot 'ORhom.sln') -PathType Leaf)) {
    throw "'$repoRoot' ist nicht der erwartete ORhom-Workspace."
}

if (-not $repoRoot.Equals($destination, [StringComparison]::OrdinalIgnoreCase) -and
    (Test-Path -LiteralPath $destination)) {
    throw "Der Zielordner '$destination' existiert bereits."
}

& git -C $repoRoot remote set-url origin $targetRemote
if ($LASTEXITCODE -ne 0) {
    throw "Die lokale origin-URL konnte nicht auf '$targetRemote' gesetzt werden."
}

$configuredRemote = (& git -C $repoRoot remote get-url origin).Trim()
if ($LASTEXITCODE -ne 0 -or
    -not $configuredRemote.Equals($targetRemote, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Die lokale origin-URL wurde nicht korrekt aktualisiert."
}

$generatedRelativePaths = @(
    'bin',
    'obj',
    'artifacts',
    'ORhom.Tests\bin',
    'ORhom.Tests\obj'
)
foreach ($relativePath in $generatedRelativePaths) {
    $generatedPath = [IO.Path]::GetFullPath((Join-Path $repoRoot $relativePath))
    if (-not $generatedPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsicherer Build-Ausgabepfad: '$generatedPath'."
    }

    if (Test-Path -LiteralPath $generatedPath) {
        Remove-Item -LiteralPath $generatedPath -Recurse -Force
    }
}

$fetchHeadPath = [IO.Path]::GetFullPath((Join-Path $repoRoot '.git\FETCH_HEAD'))
if (Test-Path -LiteralPath $fetchHeadPath -PathType Leaf) {
    Remove-Item -LiteralPath $fetchHeadPath -Force
}

if (-not $repoRoot.Equals($destination, [StringComparison]::OrdinalIgnoreCase)) {
    Rename-Item -LiteralPath $repoRoot -NewName $targetName
}

if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
    throw "Der Workspace wurde nicht unter '$destination' gefunden."
}

& dotnet restore (Join-Path $destination 'ORhom.sln') --locked-mode --nologo -m:1
if ($LASTEXITCODE -ne 0) {
    throw 'Der Restore im umbenannten Workspace ist fehlgeschlagen.'
}

& dotnet build (Join-Path $destination 'ORhom.sln') -c Release --no-restore --nologo -m:1
if ($LASTEXITCODE -ne 0) {
    throw 'Der Release-Build im umbenannten Workspace ist fehlgeschlagen.'
}

$publishDirectory = Join-Path $destination 'artifacts\publish\win-x64'
& dotnet restore (Join-Path $destination 'ORhom.csproj') -r win-x64 --locked-mode --nologo -m:1
if ($LASTEXITCODE -ne 0) {
    throw 'Der win-x64-Restore im umbenannten Workspace ist fehlgeschlagen.'
}

& dotnet publish (Join-Path $destination 'ORhom.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    --nologo `
    -m:1 `
    -p:PublishProfile=WindowsSelfContained `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw 'Der Einzeldatei-Publish im umbenannten Workspace ist fehlgeschlagen.'
}

$publishedFiles = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse)
if ($publishedFiles.Count -ne 1 -or
    -not $publishedFiles[0].Name.Equals('ORhom.exe', [StringComparison]::Ordinal)) {
    throw 'Der Publish besteht nicht exakt aus ORhom.exe.'
}

[PSCustomObject]@{
    Workspace = $destination
    Remote = $configuredRemote
    Executable = $publishedFiles[0].FullName
}
