# SPDX-FileCopyrightText: 2026 Samuel Moxham
# SPDX-License-Identifier: MIT

<#
.SYNOPSIS
Real hardware smoke run for Cue2 on Windows (Windows PowerShell 5.1 or later). Run it before a
release; it is never part of the default run or CI.

.DESCRIPTION
Runs the engine tests in a real Godot window with the real audio device and the real video
path (d3d11va where available), so device problems that headless runs cannot see show up.
Godot's user data still goes to a fresh folder under tests\artifacts\hardware, never to the
real one. Needs the FFmpeg DLLs in bin\win64 (git lfs pull --include "bin/win64/**").
Results: tests\artifacts\hardware\engine.trx and tests\artifacts\hardware\timing.csv (the
previous run's file is kept as timing.previous.csv).

.PARAMETER Godot
Path to the Godot 4.7.1 .NET (mono) editor console binary. Defaults to $env:CUE2_GODOT.

.PARAMETER Filter
Optional dotnet test filter, for example "FullyQualifiedName~GoTiming".

.EXAMPLE
powershell -ExecutionPolicy Bypass -File tests\run-hardware-smoke.ps1 -Godot C:\Godot\Godot_v4.7.1-stable_mono_win64_console.exe
#>
param(
    [string]$Godot = $env:CUE2_GODOT,
    [string]$Filter = ''
)

$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'run-tests.ps1'
Write-Host 'run-hardware-smoke: engine tests with the real window, audio device and video path'
& $runner -Suite Engine -Godot $Godot -Filter $Filter -Hardware
exit $LASTEXITCODE
