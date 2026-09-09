<#
.SYNOPSIS
    Builds both release archives into dist\.

.DESCRIPTION
    Produces two assets for a GitHub Release:

      BrokenSpectrePCVRFix-<ver>.zip
          Plugin + patcher + docs. No third-party binaries. User supplies BepInEx.

      BrokenSpectrePCVRFix-<ver>-with-BepInEx.zip
          The same, plus an unmodified copy of the official BepInEx win_x64 release laid out
          ready to drop into the game folder, with all four third-party licenses included
          (LGPL-2.1 for BepInEx requires the text to travel with the binaries).

    BepInEx is NOT stored in this repository. Point -BepInExDir at an extracted copy of the
    official release; the script verifies it looks right before using it.

.PARAMETER Version
    Release version string. Must match the version in Plugin.cs.

.PARAMETER BepInExDir
    Folder containing an extracted BepInEx win_x64 release (the one with winhttp.dll at its root).
    Defaults to ..\..\BepInEx_win_x64_5.4.23.5 relative to the repo.

.PARAMETER SkipBundle
    Build only the plugin-only archive.

.EXAMPLE
    .\Make-Release.ps1
    .\Make-Release.ps1 -Version 1.3.0 -BepInExDir "C:\dl\BepInEx_win_x64_5.4.23.5"
#>
[CmdletBinding()]
param(
    [string] $Version = '1.2.0',
    [string] $BepInExDir,
    [switch] $SkipBundle
)

$ErrorActionPreference = 'Stop'

$repo    = Split-Path $PSScriptRoot -Parent
$dist    = Join-Path $repo 'dist'
$distSrc = Join-Path $repo 'dist-files'
$dll     = Join-Path $repo 'build\BrokenSpectrePCVRFix.dll'

if (-not $BepInExDir) { $BepInExDir = Join-Path (Split-Path $repo -Parent) 'BepInEx_win_x64_5.4.23.5' }

# Stage outside the repo so nothing half-built is ever committed.
$stageRoot = Join-Path ([IO.Path]::GetTempPath()) ("bsfix-release-" + [Guid]::NewGuid().ToString('N').Substring(0,8))

if (-not (Test-Path $dll)) { throw "Plugin not built. Run tools\Build.ps1 first (expected $dll)." }
New-Item -ItemType Directory -Force $dist | Out-Null

function New-Stage([string] $name) {
    $p = Join-Path $stageRoot $name
    New-Item -ItemType Directory -Force $p | Out-Null
    return $p
}

# ---------------------------------------------------------------- plugin only
$s1 = New-Stage 'plain'
Copy-Item $dll                                  $s1 -Force
Copy-Item (Join-Path $repo 'tools\Patch-OVRPlugin.ps1') $s1 -Force
Copy-Item (Join-Path $distSrc 'INSTALL.md')     $s1 -Force
Copy-Item (Join-Path $repo 'LICENSE')           $s1 -Force
Copy-Item (Join-Path $repo 'NOTICE')            $s1 -Force

$zip1 = Join-Path $dist "BrokenSpectrePCVRFix-$Version.zip"
Compress-Archive -Path "$s1\*" -DestinationPath $zip1 -CompressionLevel Optimal -Force
Write-Host ("Built {0}  ({1:N0} bytes)" -f (Split-Path $zip1 -Leaf), (Get-Item $zip1).Length) -ForegroundColor Green

# ---------------------------------------------------------------- with BepInEx
if (-not $SkipBundle) {
    $needed = @('winhttp.dll', 'doorstop_config.ini', 'BepInEx\core\BepInEx.dll', 'BepInEx\core\0Harmony.dll')
    foreach ($n in $needed) {
        if (-not (Test-Path (Join-Path $BepInExDir $n))) {
            throw "BepInExDir does not look like an extracted BepInEx win_x64 release (missing $n): $BepInExDir"
        }
    }

    $s2 = New-Stage 'bundle'
    Copy-Item (Join-Path $BepInExDir '*') $s2 -Recurse -Force
    New-Item -ItemType Directory -Force (Join-Path $s2 'BepInEx\plugins') | Out-Null

    Copy-Item $dll (Join-Path $s2 'BepInEx\plugins') -Force
    Copy-Item (Join-Path $repo 'tools\Patch-OVRPlugin.ps1')      $s2 -Force
    Copy-Item (Join-Path $distSrc 'INSTALL-with-BepInEx.md')     $s2 -Force
    Copy-Item (Join-Path $distSrc 'THIRD-PARTY.md')              $s2 -Force
    Copy-Item (Join-Path $distSrc 'THIRD-PARTY-LICENSES')        $s2 -Recurse -Force
    Copy-Item (Join-Path $repo 'LICENSE')                        $s2 -Force
    Copy-Item (Join-Path $repo 'NOTICE')                         $s2 -Force

    $zip2 = Join-Path $dist "BrokenSpectrePCVRFix-$Version-with-BepInEx.zip"
    Compress-Archive -Path "$s2\*" -DestinationPath $zip2 -CompressionLevel Optimal -Force
    Write-Host ("Built {0}  ({1:N0} bytes)" -f (Split-Path $zip2 -Leaf), (Get-Item $zip2).Length) -ForegroundColor Green
}

Write-Host ""
Write-Host "SHA-256:" -ForegroundColor Cyan
Get-ChildItem "$dist\*$Version*.zip" | ForEach-Object {
    "  {0}`n    {1}" -f $_.Name, (Get-FileHash $_.FullName -Algorithm SHA256).Hash
}
Write-Host ""
Write-Host "Staged in $stageRoot (temp; safe to delete)."
