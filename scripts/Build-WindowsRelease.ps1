[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$InnoCompilerPath,

    [Parameter(Mandatory)]
    [string]$VcRedistPath,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repoRoot 'ORhom.sln'
$projectPath = Join-Path $repoRoot 'ORhom.csproj'
$manifestPath = Join-Path $repoRoot 'app.manifest'
$noticesSourcePath = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md'
$installerScriptPath = Join-Path $repoRoot 'installer\ORhom.iss'
$buildRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\release-build\$Version"))
$publishDir = Join-Path $buildRoot 'publish'
$releaseDir = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\release'))
}
else {
    [IO.Path]::GetFullPath($OutputDirectory)
}

function Assert-CommandSucceeded {
    param(
        [Parameter(Mandatory)]
        [string]$Description
    )

    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Assert-PathWithinDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$CandidatePath,

        [Parameter(Mandatory)]
        [string]$DirectoryPath
    )

    $candidate = [IO.Path]::GetFullPath($CandidatePath)
    $directory = [IO.Path]::GetFullPath($DirectoryPath)
    $prefix = $directory.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside '$directory': $candidate"
    }
}

$innoCompiler = [IO.Path]::GetFullPath($InnoCompilerPath)
$vcRedist = [IO.Path]::GetFullPath($VcRedistPath)
if (-not (Test-Path -LiteralPath $innoCompiler -PathType Leaf)) {
    throw "Inno Setup compiler not found: $innoCompiler"
}
if (-not (Test-Path -LiteralPath $vcRedist -PathType Leaf)) {
    throw "Microsoft Visual C++ redistributable not found: $vcRedist"
}

$vcRedistSignature = Get-AuthenticodeSignature -LiteralPath $vcRedist
if ($vcRedistSignature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
    $vcRedistSignature.SignerCertificate.Subject -notmatch '(^|,\s*)CN=Microsoft Corporation(,|$)') {
    throw "The Visual C++ redistributable does not have a valid Microsoft signature."
}

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$projectVersion = [string]$project.Project.PropertyGroup.Version
$informationalVersion = [string]$project.Project.PropertyGroup.InformationalVersion
if ($projectVersion -ne $Version -or $informationalVersion -ne $Version) {
    throw "Release version '$Version' does not match ORhom.csproj ('$projectVersion' / '$informationalVersion')."
}

[xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
$manifestVersion = [string]$manifest.assembly.assemblyIdentity.version
if ($manifestVersion -ne "$Version.0") {
    throw "Release version '$Version' does not match app.manifest ('$manifestVersion')."
}

Assert-PathWithinDirectory -CandidatePath $buildRoot -DirectoryPath (Join-Path $repoRoot 'artifacts')
if (Test-Path -LiteralPath $buildRoot) {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

if (Test-Path -LiteralPath $releaseDir) {
    $existingReleaseFiles = @(Get-ChildItem -LiteralPath $releaseDir -Force)
    if ($existingReleaseFiles.Count -ne 0) {
        throw "Release output directory must be empty: $releaseDir"
    }
}
else {
    New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
}

& dotnet restore $solutionPath -m:1 --locked-mode
Assert-CommandSucceeded 'dotnet restore'

& dotnet format $solutionPath --verify-no-changes --no-restore
Assert-CommandSucceeded 'dotnet format'

& dotnet test $solutionPath -c Release --no-restore --nologo -m:1 -p:TreatWarningsAsErrors=true
Assert-CommandSucceeded 'dotnet test'

& dotnet restore $projectPath -r win-x64 -m:1 --locked-mode
Assert-CommandSucceeded 'runtime-specific dotnet restore'

& dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    --nologo `
    -p:TreatWarningsAsErrors=true `
    -p:PublishProfile=WindowsSelfContained `
    -o $publishDir
Assert-CommandSucceeded 'dotnet publish'

$expectedPublishFiles = @('ORhom.exe')
$publishPrefix = $publishDir.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$actualPublishFiles = @(
    Get-ChildItem -LiteralPath $publishDir -File -Recurse |
        ForEach-Object { $_.FullName.Substring($publishPrefix.Length) }
)
$publishDifference = @(
    Compare-Object -ReferenceObject $expectedPublishFiles -DifferenceObject $actualPublishFiles
)
if ($publishDifference.Count -ne 0) {
    throw "Unexpected publish contents: $($actualPublishFiles -join ', ')"
}

$publishedExe = Get-Item -LiteralPath (Join-Path $publishDir 'ORhom.exe')
$publishedNotices = Get-Item -LiteralPath $noticesSourcePath
if ($publishedExe.Length -le 0 -or $publishedNotices.Length -le 0) {
    throw 'The published executable or third-party notices file is empty.'
}

$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExe.FullName)
if ($versionInfo.ProductName -ne 'ORhom' -or
    $versionInfo.FileDescription -ne 'ORhom' -or
    -not $versionInfo.ProductVersion.StartsWith($Version, [StringComparison]::Ordinal)) {
    throw 'The published executable metadata is invalid.'
}

$portableExeName = "ORhom-$Version-win-x64-portable.exe"
$portableExePath = Join-Path $releaseDir $portableExeName
$releaseNoticesPath = Join-Path $releaseDir 'THIRD-PARTY-NOTICES.md'
Copy-Item -LiteralPath $publishedExe.FullName -Destination $portableExePath
Copy-Item -LiteralPath $publishedNotices.FullName -Destination $releaseNoticesPath

& $innoCompiler `
    "/DAppVersion=$Version" `
    "/DSourceDir=$publishDir" `
    "/DOutputDir=$releaseDir" `
    "/DNoticesPath=$noticesSourcePath" `
    "/DVcRedistPath=$vcRedist" `
    $installerScriptPath
Assert-CommandSucceeded 'Inno Setup compiler'

$installerName = "ORhom-Setup-$Version-win-x64.exe"
$installerPath = Join-Path $releaseDir $installerName
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf) -or
    (Get-Item -LiteralPath $installerPath).Length -le 0) {
    throw "Expected installer was not created: $installerPath"
}

$releaseAssets = @(
    Get-Item -LiteralPath $installerPath
    Get-Item -LiteralPath $portableExePath
    Get-Item -LiteralPath $releaseNoticesPath
)
$checksumLines = foreach ($asset in $releaseAssets) {
    $hash = (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$($asset.Name)"
}
$checksumPath = Join-Path $releaseDir 'SHA256SUMS.txt'
[IO.File]::WriteAllLines(
    $checksumPath,
    $checksumLines,
    [Text.UTF8Encoding]::new($false))

[PSCustomObject]@{
    Version = $Version
    Installer = $installerPath
    PortableExecutable = $portableExePath
    ThirdPartyNotices = $releaseNoticesPath
    Checksums = $checksumPath
}
