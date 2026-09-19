<#
.SYNOPSIS
    Applies (or reverts) the one-byte patch to Broken Spectre's OVRPlugin.dll that allows
    Meta's native plugin to initialise on a non-Meta OpenXR runtime.

.DESCRIPTION
    Meta's OVRPlugin refuses to start on any runtime whose name is not Oculus/Meta:

        [OVRPlugin][INFO]  CompositorOpenXR::PreInitialize(... preinitializeFlags=0x1)
        [OVRPlugin][INFO]  Support Non-Oculus runtime: NO
        [OVRPlugin][ERROR] Non-Oculus OpenXR runtime is not supported. (CompositorOpenXR.cpp:3433)
        [OVRPlugin][ERROR] Unable to create compositor: -1006

    That "NO" is bit 3 of preinitializeFlags, and Meta ships a code path for "YES". The caller
    hard-codes the flags as 1:

        180099E97:  lea  r8d,[rdx+1]      ; 44 8D 42 01   preinitializeFlags = 1
        180099E9B:  call 1800C2CD0        ; CompositorOpenXR::PreInitialize

    This script changes that single displacement byte from 0x01 to 0x09, setting bit 3, so the
    log reads "Support Non-Oculus runtime: YES" and initialisation continues normally.

    NOTHING ELSE IN THE FILE IS MODIFIED. The original is backed up next to the DLL, and the
    file is verified by SHA-256 before and after so a wrong or updated build is never touched.

.PARAMETER GamePath
    Broken Spectre install folder. Auto-detected from the default Steam library if omitted.

.PARAMETER Revert
    Restore the original bytes (0x09 -> 0x01).

.EXAMPLE
    .\Patch-OVRPlugin.ps1
    .\Patch-OVRPlugin.ps1 -GamePath "D:\SteamLibrary\steamapps\common\Broken Spectre"
    .\Patch-OVRPlugin.ps1 -Revert
#>
[CmdletBinding()]
param(
    [string] $GamePath,
    [switch] $Revert
)

$ErrorActionPreference = 'Stop'

# Offset of the disp8 in 'lea r8d,[rdx+1]' at VA 0x180099E97 (.text: file 0x400 -> RVA 0x1000).
$PatchOffset   = 0x9929A
$OriginalByte  = 0x01
$PatchedByte   = 0x09
$ExpectedShape = @(0x44, 0x8D, 0x42)   # lea r8d,[rdx+disp8]

$KnownOriginalSha = '5D41B11374E5068DC5CD5C6FD8D2F6BBD847D069FBC31BBB0A30039671A937DD'
$KnownPatchedSha  = '58E8FBEBBDF7724AEF115E922FD994F055E2EFEE062FD4E2CE4045585426A1EE'
$KnownVersion     = '1.94.0'

function Find-GamePath {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\Broken Spectre",
        "$env:ProgramFiles\Steam\steamapps\common\Broken Spectre"
    )
    foreach ($c in $candidates) { if (Test-Path (Join-Path $c 'BrokenSpectre.exe')) { return $c } }
    return $null
}

if (-not $GamePath) { $GamePath = Find-GamePath }
if (-not $GamePath) {
    throw "Could not find Broken Spectre. Pass -GamePath ""<install folder>"" (the folder containing BrokenSpectre.exe)."
}

$dll = Join-Path $GamePath 'BrokenSpectre_Data\Plugins\x86_64\OVRPlugin.dll'
if (-not (Test-Path $dll)) { throw "OVRPlugin.dll not found at: $dll" }

Write-Host "Target : $dll"
$version = (Get-Item $dll).VersionInfo.FileVersion
$sha     = (Get-FileHash $dll -Algorithm SHA256).Hash
Write-Host "Version: $version"
Write-Host "SHA-256: $sha"

if ($version -and $version -notlike "$KnownVersion*") {
    Write-Warning "OVRPlugin version is $version; this patch was verified against $KnownVersion."
}

# Read the four bytes of the instruction and decide what state we are in.
$fs  = [IO.File]::Open($dll, 'Open', 'ReadWrite')
try {
    $buf = New-Object byte[] 4
    $null = $fs.Seek($PatchOffset - 3, 'Begin')
    $null = $fs.Read($buf, 0, 4)

    $shapeOk = ($buf[0] -eq $ExpectedShape[0]) -and ($buf[1] -eq $ExpectedShape[1]) -and ($buf[2] -eq $ExpectedShape[2])
    $current = $buf[3]
    $hex = ($buf | ForEach-Object { $_.ToString('X2') }) -join ' '
    Write-Host "Bytes  : $hex  (expect 44 8D 42 01 unpatched, 44 8D 42 09 patched)"

    if (-not $shapeOk) {
        throw "Instruction shape does not match (found $hex). This is not the expected build - refusing to write."
    }

    $want = if ($Revert) { $OriginalByte } else { $PatchedByte }
    $from = if ($Revert) { $PatchedByte }  else { $OriginalByte }

    if ($current -eq $want) {
        Write-Host ("Already in the requested state (0x{0:X2}). Nothing to do." -f $current) -ForegroundColor Green
        return
    }
    if ($current -ne $from) {
        throw ("Unexpected byte 0x{0:X2} at offset 0x{1:X}; expected 0x{2:X2}. Refusing to write." -f $current, $PatchOffset, $from)
    }

    if (-not $Revert) {
        $backup = "$dll.orig"
        if (-not (Test-Path $backup)) {
            Copy-Item $dll $backup
            Write-Host "Backup : $backup"
        } else {
            Write-Host "Backup : already exists, keeping it ($backup)"
        }
    }

    $null = $fs.Seek($PatchOffset, 'Begin')
    $fs.WriteByte($want)
    Write-Host ("Wrote  : 0x{0:X2} -> 0x{1:X2} at offset 0x{2:X}" -f $from, $want, $PatchOffset)
}
finally { $fs.Close() }

$newSha = (Get-FileHash $dll -Algorithm SHA256).Hash
Write-Host "New SHA: $newSha"
if (-not $Revert -and $newSha -eq $KnownPatchedSha) {
    Write-Host "Verified against known-good patched hash." -ForegroundColor Green
} elseif ($Revert -and $newSha -eq $KnownOriginalSha) {
    Write-Host "Verified against known-good original hash." -ForegroundColor Green
} else {
    Write-Warning "Hash does not match the reference build. The byte patch was applied correctly, but this is a different OVRPlugin build than the one tested."
}

Write-Host ""
Write-Host ("Done. " + $(if ($Revert) { "Reverted." } else { "Patched." })) -ForegroundColor Cyan
Write-Host "A Steam game update will overwrite this file - re-run the script if the game stops working after an update."
