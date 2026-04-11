param(
    [Parameter(Mandatory = $true)]
    [ValidateRange(0, 65535)]
    [int]$Major,

    [Parameter(Mandatory = $true)]
    [ValidateRange(0, 65535)]
    [int]$Minor,

    [string]$VersionPropsPath = (Join-Path $PSScriptRoot "..\Version.props")
)

$resolvedPath = (Resolve-Path -LiteralPath $VersionPropsPath).Path
[xml]$project = Get-Content -LiteralPath $resolvedPath

$propertyGroup = $project.Project.PropertyGroup
if (-not $propertyGroup) {
    throw "PropertyGroup not found in $resolvedPath"
}

$propertyGroup.VersionMajor = [string]$Major
$propertyGroup.VersionMinor = [string]$Minor
$propertyGroup.VersionBuild = "1"
$project.Save($resolvedPath)

Write-Output ("Version updated to {0}.{1}.1" -f $Major, $Minor)
