[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('2023', '2024', '2025')]
    [string]$RevitVersion,

    [string]$AddinsRoot = (Join-Path $env:APPDATA 'Autodesk/Revit/Addins')
)

$ErrorActionPreference = 'Stop'
$framework = if ($RevitVersion -eq '2025') { 'net8.0-windows' } else { 'net48' }
$projectRoot = Split-Path $PSScriptRoot
$build = Join-Path $projectRoot "src/Numbering.Revit/bin/$RevitVersion/Release/$framework"
$manifestDirectory = Join-Path $AddinsRoot $RevitVersion
$destination = Join-Path $manifestDirectory 'ElementNumbering'

foreach ($name in @('Numbering.Revit.dll', 'Numbering.Core.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $build $name) -PathType Leaf)) {
        throw "Build the Release configuration for Revit $RevitVersion first. Missing $name in $build."
    }
}

New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($name in @('Numbering.Revit.dll', 'Numbering.Core.dll')) {
    Copy-Item -LiteralPath (Join-Path $build $name) -Destination $destination -Force
}

[xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ElementNumbering.addin') -Raw
$manifest.RevitAddIns.AddIn.Assembly = [System.IO.Path]::GetFullPath((Join-Path $destination 'Numbering.Revit.dll'))
$manifestPath = [System.IO.Path]::GetFullPath((Join-Path $manifestDirectory 'ElementNumbering.addin'))
$manifest.Save($manifestPath)
"Revit ${RevitVersion}: $manifestPath"
