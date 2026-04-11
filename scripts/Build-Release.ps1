param(
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Configuration = "Release",
    [switch]$BumpBuild,
    [Nullable[int]]$Major = $null,
    [Nullable[int]]$Minor = $null,
    [switch]$OverwriteExisting,
    [switch]$SkipZip
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot "BatteryDotOverlay.csproj"
$versionPropsPath = Join-Path $projectRoot "Version.props"
$releasesRoot = Join-Path $projectRoot "releases"

function Get-VersionData {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    [xml]$xml = Get-Content -LiteralPath $Path
    $group = $xml.Project.PropertyGroup
    if (-not $group) {
        throw "PropertyGroup not found in $Path"
    }

    return [pscustomobject]@{
        Xml = $xml
        Group = $group
        Major = [int]$group.VersionMajor
        Minor = [int]$group.VersionMinor
        Build = [int]$group.VersionBuild
    }
}

function Save-VersionData {
    param(
        [Parameter(Mandatory = $true)]
        $VersionData,
        [Parameter(Mandatory = $true)]
        [int]$Major,
        [Parameter(Mandatory = $true)]
        [int]$Minor,
        [Parameter(Mandatory = $true)]
        [int]$Build
    )

    $VersionData.Group.VersionMajor = [string]$Major
    $VersionData.Group.VersionMinor = [string]$Minor
    $VersionData.Group.VersionBuild = [string]$Build
    $VersionData.Xml.Save($versionPropsPath)
}

$versionData = Get-VersionData -Path $versionPropsPath

$changingBaseVersion = $Major -ne $null -or $Minor -ne $null
if ($changingBaseVersion) {
    if ($Major -eq $null -or $Minor -eq $null) {
        throw "Both -Major and -Minor must be provided together."
    }

    Save-VersionData -VersionData $versionData -Major $Major.Value -Minor $Minor.Value -Build 1
    $versionData = Get-VersionData -Path $versionPropsPath
}
elseif ($BumpBuild) {
    Save-VersionData -VersionData $versionData -Major $versionData.Major -Minor $versionData.Minor -Build ($versionData.Build + 1)
    $versionData = Get-VersionData -Path $versionPropsPath
}

$version = "{0}.{1}.{2}" -f $versionData.Major, $versionData.Minor, $versionData.Build
$packageName = "BatteryDotOverlay-{0}-{1}" -f $version, $RuntimeIdentifier
$publishDir = Join-Path $releasesRoot $packageName
$zipPath = Join-Path $releasesRoot ($packageName + ".zip")

if (Test-Path -LiteralPath $publishDir) {
    if (-not $OverwriteExisting) {
        throw "Release $packageName already exists. Use -BumpBuild, set a new -Major/-Minor, or pass -OverwriteExisting explicitly."
    }

    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

if (Test-Path -LiteralPath $zipPath) {
    if (-not $OverwriteExisting) {
        throw "Archive $packageName.zip already exists. Use -BumpBuild, set a new -Major/-Minor, or pass -OverwriteExisting explicitly."
    }

    Remove-Item -LiteralPath $zipPath -Force
}

New-Item -ItemType Directory -Path $releasesRoot -Force | Out-Null

dotnet publish $projectPath `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir

$releaseInfo = @(
    "Battery Dot Overlay release package",
    "",
    ("Version: {0}" -f $version),
    ("Runtime: {0}" -f $RuntimeIdentifier),
    ("Configuration: {0}" -f $Configuration),
    ("Built at: {0}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss")),
    "",
    "Package contents",
    "- BatteryDotOverlay.exe",
    "- config\indicator.settings.json",
    "- START-HERE.txt",
    "- START-HERE-RU.txt",
    "- CONFIGURE-OVERLAY.txt",
    "- CONFIGURE-OVERLAY-RU.txt",
    "",
    "Launch",
    "1. Ensure SteamVR is running.",
    "2. If using a PICO headset, keep PICO Connect running so headset battery fallback stays available.",
    "3. Start BatteryDotOverlay.exe.",
    "",
    "Note",
    "The application package is self-contained and does not require a separate .NET installation."
)

Set-Content -LiteralPath (Join-Path $publishDir "RELEASE-INFO.txt") -Value $releaseInfo -Encoding UTF8

if (-not $SkipZip) {
    Compress-Archive -Path $publishDir -DestinationPath $zipPath -CompressionLevel Optimal
}

Write-Output ("Release directory: {0}" -f $publishDir)
if (-not $SkipZip) {
    Write-Output ("Release archive: {0}" -f $zipPath)
}
Write-Output ("Release version: {0}" -f $version)
