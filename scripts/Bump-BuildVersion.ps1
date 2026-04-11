param(
    [string]$VersionPropsPath = (Join-Path $PSScriptRoot "..\Version.props")
)

$resolvedPath = (Resolve-Path -LiteralPath $VersionPropsPath).Path
[xml]$project = Get-Content -LiteralPath $resolvedPath

$propertyGroup = $project.Project.PropertyGroup
if (-not $propertyGroup) {
    throw "PropertyGroup not found in $resolvedPath"
}

$major = [int]$propertyGroup.VersionMajor
$minor = [int]$propertyGroup.VersionMinor
$build = ([int]$propertyGroup.VersionBuild) + 1

$propertyGroup.VersionBuild = [string]$build
$project.Save($resolvedPath)

Write-Output ("Version updated to {0}.{1}.{2}" -f $major, $minor, $build)
