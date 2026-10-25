# SPDX-FileCopyrightText: 2026 Samuel Moxham
# SPDX-License-Identifier: MIT

<#
.SYNOPSIS
Runs Cue2's automated tests on Windows (Windows PowerShell 5.1 or later): the same suites as
.github/workflows/tests.yml.

.DESCRIPTION
Plain:  C# tests in tests\Cue2.Tests (no engine), with dotnet test.
Engine: tests in tests\engine, run inside headless Godot 4.7.1 through GdUnit4Net. Godot's user
        data (APPDATA) goes to a fresh folder under tests\artifacts, never to the real one.
        Media tests (tests\engine\Media) need the FFmpeg DLLs in bin\win64 (git LFS); without
        them they are skipped and the reason is printed.
The real hardware smoke run is separate: tests\run-hardware-smoke.ps1.
Results (.trx) go to tests\artifacts\, GO timings to tests\artifacts\engine\timing.csv (the
previous run's file is kept as timing.previous.csv). Exits with a non-zero code when anything fails.

.PARAMETER Suite
All (default), Plain or Engine.

.PARAMETER Godot
Path to the Godot 4.7.1 .NET (mono) editor console binary. Defaults to $env:CUE2_GODOT.
Needed for the engine suite only.

.PARAMETER Filter
Optional dotnet test filter, for example "FullyQualifiedName~Timecode".

.PARAMETER Hardware
Used by tests\run-hardware-smoke.ps1: engine tests in a real window with the real audio
device, results in tests\artifacts\hardware. Not part of the default run or CI.

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1 -Godot C:\Godot\Godot_v4.7.1-stable_mono_win64_console.exe
#>
param(
    [ValidateSet('All', 'Plain', 'Engine')]
    [string]$Suite = 'All',
    [string]$Godot = $env:CUE2_GODOT,
    [string]$Filter = '',
    [switch]$Hardware
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $repo 'tests\artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

function Invoke-PlainTests {
    Write-Host 'run-tests: plain C# tests'
    $testArgs = @(
        'test', (Join-Path $repo 'tests\Cue2.Tests.sln'),
        '-c', 'Debug', '-nologo',
        '--blame-crash',
        '--results-directory', (Join-Path $artifacts 'plain'),
        '--logger', 'trx;LogFileName=plain.trx',
        '--logger', 'console;verbosity=normal'
    )
    if ($Filter -ne '') { $testArgs += @('--filter', $Filter) }
    dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Test-RealBinary([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    return ($bytes.Length -gt 2 -and $bytes[0] -eq 0x4D -and $bytes[1] -eq 0x5A)
}

function Invoke-EngineTests {
    if ([string]::IsNullOrEmpty($Godot) -or -not (Test-Path -LiteralPath $Godot)) {
        Write-Error 'run-tests: the engine suite needs the Godot 4.7.1 .NET editor console binary: pass -Godot PATH or set CUE2_GODOT'
    }

    if ($Hardware) { $engineDir = Join-Path $artifacts 'hardware' } else { $engineDir = Join-Path $artifacts 'engine' }
    $dataRoot = Join-Path $engineDir 'userdata'
    if (Test-Path -LiteralPath $dataRoot) { Remove-Item -LiteralPath $dataRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
    # Keep the last run's GO timings next to the new ones, for comparing.
    $timing = Join-Path $engineDir 'timing.csv'
    if (Test-Path -LiteralPath $timing) { Move-Item -LiteralPath $timing -Destination (Join-Path $engineDir 'timing.previous.csv') -Force }

    $engineFilter = $Filter
    $ffmpegReal = Test-RealBinary (Join-Path $repo 'bin\win64\avcodec-63.dll')
    if ($Hardware -and -not $ffmpegReal) {
        Write-Error 'run-tests: the hardware run needs the FFmpeg DLLs in bin\win64 (run: git lfs pull --include "bin/win64/**")'
    }
    $noNatives = '0'
    if (-not $ffmpegReal) {
        $noNatives = '1'
        Write-Host 'run-tests: SKIPPING media engine tests: bin\win64 holds git LFS pointers, not the FFmpeg DLLs (run: git lfs pull --include "bin/win64/**")'
        $skip = 'FullyQualifiedName!~Cue2.EngineTests.Media.'
        if ($engineFilter -ne '') { $engineFilter = "($engineFilter)&$skip" } else { $engineFilter = $skip }
    }

    # Import once (translations, textures) so the first engine boot finds every resource.
    # APPDATA points at the test folder for this step too, so nothing lands in the real one.
    Write-Host 'run-tests: importing project resources'
    $realAppData = $env:APPDATA
    try {
        $env:APPDATA = $dataRoot
        $import = Start-Process -FilePath $Godot -ArgumentList @('--headless', '--path', ('"' + $repo + '"'), '--import') `
            -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput (Join-Path $engineDir 'import.log') `
            -RedirectStandardError (Join-Path $engineDir 'import.err.log')
    }
    finally {
        $env:APPDATA = $realAppData
    }
    if ($import.ExitCode -ne 0) {
        Write-Error "run-tests: Godot import failed, see $engineDir\import.log"
    }

    $esc = { param($v) [System.Security.SecurityElement]::Escape($v) }
    # Headless with a silent audio driver, except for the hardware run (real window and device).
    if ($Hardware) {
        $audioDriver = ''
        $godotParameters = ''
    }
    else {
        $audioDriver = "      <SDL_AUDIO_DRIVER>dummy</SDL_AUDIO_DRIVER>`r`n"
        $godotParameters = '--headless'
    }
    $settings = Join-Path $engineDir 'engine.runsettings'
    $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <RunConfiguration>
    <MaxCpuCount>1</MaxCpuCount>
    <TestSessionTimeout>900000</TestSessionTimeout>
    <EnvironmentVariables>
      <GODOT_BIN>$(& $esc $Godot)</GODOT_BIN>
      <CUE2_TEST_DATA_ROOT>$(& $esc $dataRoot)</CUE2_TEST_DATA_ROOT>
      <CUE2_TEST_ARTIFACTS>$(& $esc $engineDir)</CUE2_TEST_ARTIFACTS>
      <APPDATA>$(& $esc $dataRoot)</APPDATA>
      <CUE2_TEST_NO_NATIVES>$noNatives</CUE2_TEST_NO_NATIVES>
$audioDriver    </EnvironmentVariables>
  </RunConfiguration>
  <GdUnit4>
    <Parameters>$godotParameters</Parameters>
    <DisplayName>FullyQualifiedName</DisplayName>
  </GdUnit4>
</RunSettings>
"@
    [System.IO.File]::WriteAllText($settings, $xml, (New-Object System.Text.UTF8Encoding($false)))

    Write-Host "run-tests: engine tests (Godot: $Godot, user data: $dataRoot, hardware: $Hardware)"
    $testArgs = @(
        'test', (Join-Path $repo 'Cue2.csproj'),
        '-c', 'Debug', '--no-build', '-nologo',
        '--settings', $settings,
        '--results-directory', $engineDir,
        '--logger', 'trx;LogFileName=engine.trx',
        '--logger', 'console;verbosity=normal'
    )
    if ($engineFilter -ne '') { $testArgs += @('--filter', $engineFilter) }
    $staleTrx = Join-Path $engineDir 'engine.trx'
    if (Test-Path -LiteralPath $staleTrx) { Remove-Item -LiteralPath $staleTrx -Force }
    try {
        dotnet @testArgs
        $code = $LASTEXITCODE
    }
    finally {
        # The GdUnit4 adapter writes its runner scene into the project; remove it again.
        $runner = Join-Path $repo 'gdunit4_testadapter_v5'
        if (Test-Path -LiteralPath $runner) { Remove-Item -LiteralPath $runner -Recurse -Force }
    }
    # When Godot crashes early, dotnet test can still exit 0 with nothing run.
    $executed = 0
    $trx = Join-Path $engineDir 'engine.trx'
    if ($code -eq 0 -and (Test-Path -LiteralPath $trx)) {
        $match = Select-String -LiteralPath $trx -Pattern '<Counters [^>]*executed="(\d+)"' | Select-Object -First 1
        if ($match) { $executed = [int]$match.Matches[0].Groups[1].Value }
    }
    if ($code -eq 0 -and $executed -eq 0) {
        Write-Host "run-tests: the engine suite ran no tests"
        $code = 1
    }
    if ($code -ne 0) {
        # "No test is available" from dotnet test usually means Godot itself crashed before
        # any test ran, not that the tests were not found.
        Write-Host "run-tests: engine suite failed. If the output shows 'GodotRuntimeTestRunner ends with exit code'"
        Write-Host "run-tests: followed by 'No test is available', Godot crashed before the tests ran: the last"
        Write-Host "run-tests: 'Godot /err' lines above it give the reason."
        exit $code
    }
}

Push-Location $repo
try {
    Write-Host 'run-tests: building Cue2 (Debug)'
    dotnet build (Join-Path $repo 'Cue2.csproj') -c Debug -nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if ($Suite -eq 'All' -or $Suite -eq 'Plain') { Invoke-PlainTests }
    if ($Suite -eq 'All' -or $Suite -eq 'Engine') { Invoke-EngineTests }

    Write-Host 'run-tests: all selected suites passed'
    exit 0
}
finally {
    Pop-Location
}
