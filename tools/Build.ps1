<#
.SYNOPSIS
    Builds BrokenSpectrePCVRFix.dll (the BepInEx plugin).

.DESCRIPTION
    The plugin is compiled against the game's own managed assemblies, because Unity targets
    netstandard 2.1 and the desktop .NET Framework reference set alone will not satisfy it
    (you get CS0012 "The type 'Object' is defined in an assembly that is not referenced").
    That is why this uses -nostdlib+ and points every core reference at BrokenSpectre_Data\Managed.

    No .NET SDK is required. It uses the Roslyn csc.exe that ships with Visual Studio Build
    Tools, falling back to the .NET Framework compiler in C:\Windows\Microsoft.NET.

.PARAMETER GamePath
    Broken Spectre install folder. Auto-detected from the default Steam library if omitted.

.PARAMETER BepInExPath
    Folder containing BepInEx\core\BepInEx.dll. Defaults to the game folder (i.e. BepInEx already
    installed there).

.PARAMETER Install
    Copy the built DLL into <GamePath>\BepInEx\plugins after a successful build.

.EXAMPLE
    .\Build.ps1
    .\Build.ps1 -Install
    .\Build.ps1 -GamePath "D:\SteamLibrary\steamapps\common\Broken Spectre" -Install
#>
[CmdletBinding()]
param(
    [string] $GamePath,
    [string] $BepInExPath,
    [switch] $Install
)

$ErrorActionPreference = 'Stop'

function Find-GamePath {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\Broken Spectre",
        "$env:ProgramFiles\Steam\steamapps\common\Broken Spectre"
    )
    foreach ($c in $candidates) { if (Test-Path (Join-Path $c 'BrokenSpectre.exe')) { return $c } }
    return $null
}

function Find-Csc {
    $roots = @(
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022",
        "$env:ProgramFiles\Microsoft Visual Studio\2022",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2019"
    )
    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        $hit = Get-ChildItem -Path $r -Recurse -Filter csc.exe -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -like '*Roslyn*' } | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    $fallback = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (Test-Path $fallback) { return $fallback }
    return $null
}

if (-not $GamePath) { $GamePath = Find-GamePath }
if (-not $GamePath) { throw "Could not find Broken Spectre. Pass -GamePath ""<install folder>""." }
if (-not $BepInExPath) { $BepInExPath = $GamePath }

$managed = Join-Path $GamePath 'BrokenSpectre_Data\Managed'
$core    = Join-Path $BepInExPath 'BepInEx\core'
$srcDir  = Join-Path $PSScriptRoot '..\src\BrokenSpectreMetaGate'
$src     = Join-Path $srcDir 'Plugin.cs'
$outDir  = Join-Path $PSScriptRoot '..\build'
$out     = Join-Path $outDir 'BrokenSpectrePCVRFix.dll'

foreach ($p in @($managed, $core, $src)) {
    if (-not (Test-Path $p)) { throw "Missing required path: $p" }
}

$csc = Find-Csc
if (-not $csc) { throw "No C# compiler found. Install Visual Studio Build Tools 2022 (Desktop development with C++/.NET), or use the .NET Framework csc." }

New-Item -ItemType Directory -Force $outDir | Out-Null

$refs = @(
    "$managed\mscorlib.dll"
    "$managed\netstandard.dll"
    "$managed\System.dll"
    "$managed\System.Core.dll"
    "$managed\UnityEngine.dll"
    "$managed\UnityEngine.CoreModule.dll"
    "$managed\Oculus.VR.dll"
    "$core\BepInEx.dll"
    "$core\0Harmony.dll"
)
foreach ($r in $refs) { if (-not (Test-Path $r)) { throw "Missing reference assembly: $r" } }

Write-Host "Compiler : $csc"
Write-Host "Game     : $GamePath"
Write-Host "BepInEx  : $core"
Write-Host "Output   : $out"
Write-Host ""

$cscArgs = @('-nologo', '-target:library', '-optimize+', '-nostdlib+', "-out:$out")
$cscArgs += ($refs | ForEach-Object { "-r:$_" })
$cscArgs += $src

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed (exit $LASTEXITCODE)." }

Write-Host ""
Write-Host ("Built {0} ({1} bytes)" -f (Split-Path $out -Leaf), (Get-Item $out).Length) -ForegroundColor Green

if ($Install) {
    $plugins = Join-Path $GamePath 'BepInEx\plugins'
    if (-not (Test-Path $plugins)) { throw "BepInEx plugins folder not found at $plugins. Install BepInEx first (see README)." }
    Copy-Item $out $plugins -Force
    Write-Host "Installed to $plugins" -ForegroundColor Green
}
