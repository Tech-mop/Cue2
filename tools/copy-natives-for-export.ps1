# SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
# SPDX-License-Identifier: MIT

<#
.SYNOPSIS
  Copies platform native libraries (FFmpeg + RtMidi) into a Godot export folder.

.DESCRIPTION
  Godot C# export does not ship res://bin natives as loadable OS shared libraries.
  After exporting, run this script so MediaEngine / MidiManager can find FFmpeg (and MIDI)
  next to the app. See docs/export-packaging.md.

  Layout options:
    -FlatData (default): copy into data_Cue2_* next to managed assemblies.
    -BesideExeBin:       also copy full platform tree to {export}/bin/{platform}/.
    macOS .app:          also copies into Contents/Frameworks (preferred search path).

.PARAMETER ExportRoot
  Folder that contains the exported .exe / binary and data_Cue2_* (or .app on macOS).

.PARAMETER Platform
  One of: win64, winarm64, macos, linux64, linuxarm64.
  Default: win64 on Windows, macos on macOS, linux64 otherwise.

.PARAMETER FlatData
  Copy core FFmpeg + MIDI into each data_Cue2_* directory under ExportRoot (default: true).

.PARAMETER BesideExeBin
  Also copy the full platform bin folder to ExportRoot/bin/{Platform}
  (or Contents/Resources/bin/{Platform} inside a .app).

.PARAMETER ProjectRoot
  Cue2 repo root (default: parent of tools/).

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\copy-natives-for-export.ps1 -ExportRoot ..\Exports\0.1.0\Cue2-0.1.0-windows-x86_64 -Platform win64

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\copy-natives-for-export.ps1 -ExportRoot ..\Exports\0.1.0\Cue2-0.1.0-windows-arm64 -Platform winarm64

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\copy-natives-for-export.ps1 -ExportRoot ..\Exports\0.1.0\Cue2-0.1.0-linux-x86_64 -Platform linux64

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\copy-natives-for-export.ps1 -ExportRoot ..\Exports\0.1.0\Cue2-0.1.0-linux-arm64 -Platform linuxarm64

.EXAMPLE
  # macOS (prefer tools/copy-natives-for-export.sh for @loader_path rewrite):
  .\tools\copy-natives-for-export.ps1 -ExportRoot ..\Exports\0.1.0\Cue2-0.1.0-macos-arm64 -Platform macos
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ExportRoot,

    [ValidateSet("win64", "winarm64", "macos", "linux64", "linuxarm64")]
    [string] $Platform = "",

    [bool] $FlatData = $true,

    [switch] $BesideExeBin,

    [string] $ProjectRoot = ""
)

$ErrorActionPreference = "Stop"

if (-not $ProjectRoot) {
    $ProjectRoot = Split-Path -Parent $PSScriptRoot
}

if (-not $Platform) {
    if ($IsWindows -or $env:OS -match "Windows") { $Platform = "win64" }
    elseif ($IsMacOS) { $Platform = "macos" }
    else { $Platform = "linux64" }
}

$ExportRoot = (Resolve-Path $ExportRoot).Path
$srcBin = Join-Path (Join-Path $ProjectRoot "bin") $Platform
if (-not (Test-Path $srcBin)) {
    throw "Source natives not found: $srcBin"
}

# Core set MediaEngine loads (FFmpeg 9.x majors from AutoGen LibraryVersionMap) + RtMidi
$coreNames = switch ($Platform) {
    { $_ -in @("win64", "winarm64") } {
        @(
            "avutil-61.dll",
            "avcodec-63.dll",
            "avformat-63.dll",
            "swresample-7.dll",
            "swscale-10.dll",
            "rtmidi.dll"
        )
    }
    "macos" {
        @(
            "libavutil.61.dylib",
            "libavcodec.63.dylib",
            "libavformat.63.dylib",
            "libswresample.7.dylib",
            "libswscale.10.dylib",
            "librtmidi.dylib"
        )
    }
    default {
        @(
            "libavutil.so.61",
            "libavcodec.so.63",
            "libavformat.so.63",
            "libswresample.so.7",
            "libswscale.so.10",
            "librtmidi.so"
        )
    }
}

function Copy-CoreToDir([string] $destDir) {
    if (-not (Test-Path $destDir)) {
        New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    }
    foreach ($name in $coreNames) {
        $src = Join-Path $srcBin $name
        if (-not (Test-Path $src)) {
            Write-Warning "Missing source file: $src"
            continue
        }
        Copy-Item -Path $src -Destination (Join-Path $destDir $name) -Force
        Write-Host "  -> $(Join-Path $destDir $name)"
    }
}

function Get-AppBundles([string] $root) {
    $apps = @()
    if ($root -like "*.app" -and (Test-Path (Join-Path $root "Contents"))) {
        $apps += Get-Item $root
    }
    else {
        $apps += Get-ChildItem -Path $root -Filter "*.app" -Directory -ErrorAction SilentlyContinue
        $apps += Get-ChildItem -Path $root -Filter "*.app" -Directory -Recurse -Depth 1 -ErrorAction SilentlyContinue
    }
    return $apps | Select-Object -Unique
}

Write-Host "copy-natives-for-export: platform=$Platform"
Write-Host "  source: $srcBin"
Write-Host "  export: $ExportRoot"

$copied = $false
$appBundles = @()
if ($Platform -eq "macos") {
    $appBundles = @(Get-AppBundles $ExportRoot)
}

# --- macOS .app: Frameworks + Resources/bin + data_* ---
foreach ($app in $appBundles) {
    $contents = Join-Path $app.FullName "Contents"
    if (-not (Test-Path $contents)) { continue }

    $frameworks = Join-Path $contents "Frameworks"
    Write-Host "Copying core FFmpeg + MIDI into $($app.Name)/Contents/Frameworks (primary)"
    Copy-CoreToDir $frameworks
    $copied = $true

    $resources = Join-Path $contents "Resources"
    $resBin = Join-Path (Join-Path $resources "bin") $Platform
    Write-Host "Copying full platform folder to $resBin"
    if (-not (Test-Path $resBin)) {
        New-Item -ItemType Directory -Force -Path $resBin | Out-Null
    }
    Copy-Item -Path (Join-Path $srcBin "*") -Destination $resBin -Force
    $copied = $true

    if ($FlatData -and (Test-Path $resources)) {
        $dataDirs = Get-ChildItem -Path $resources -Directory -Filter "data_Cue2_*" -ErrorAction SilentlyContinue
        foreach ($dir in $dataDirs) {
            Write-Host "Copying core set into $($dir.FullName)"
            Copy-CoreToDir $dir.FullName
            $copied = $true
        }
    }
}

if ($FlatData) {
    $dataDirs = @()
    $dataDirs += Get-ChildItem -Path $ExportRoot -Directory -Filter "data_Cue2_*" -ErrorAction SilentlyContinue
    # Nested under non-app layouts
    $dataDirs += Get-ChildItem -Path $ExportRoot -Directory -Filter "data_Cue2_*" -Recurse -Depth 3 -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\.app/Contents/Resources' } |
        Select-Object -First 10

    foreach ($dir in ($dataDirs | Select-Object -Unique FullName)) {
        $full = if ($dir.FullName) { $dir.FullName } else { $dir }
        Write-Host "Copying core set into $full"
        Copy-CoreToDir $full
        $copied = $true
    }
}

if ($BesideExeBin -or -not $copied) {
    $destBin = Join-Path (Join-Path $ExportRoot "bin") $Platform
    Write-Host "Copying full platform folder to $destBin"
    if (-not (Test-Path $destBin)) {
        New-Item -ItemType Directory -Force -Path $destBin | Out-Null
    }
    Copy-Item -Path (Join-Path $srcBin "*") -Destination $destBin -Force
    $copied = $true
    Write-Host "  (full bin/$Platform copy complete)"
}

if (-not $copied) {
    throw "Nothing was copied. Check ExportRoot and Platform."
}

Write-Host "Done. Zip the entire export folder for distribution (do not ship only the .exe)."
Write-Host "macOS: prefer tools/copy-natives-for-export.sh for @loader_path install-name rewrite."
Write-Host "LGPL: keep FFmpeg as replaceable shared libraries; see docs/FFmpeg-Licensing.md"
