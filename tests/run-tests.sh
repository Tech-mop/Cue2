#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2026 Samuel Moxham
# SPDX-License-Identifier: MIT
#
# Runs Cue2's automated tests on Linux: the same suites as .github/workflows/tests.yml.
#
#   plain   C# tests in tests/Cue2.Tests (no engine), with dotnet test.
#   engine  Tests in tests/engine, run inside headless Godot 4.7.1 through GdUnit4Net.
#           User data goes to a fresh folder under tests/artifacts, never to the real one.
#           Media tests (tests/engine/Media) need the FFmpeg libraries in bin/linux64
#           (git LFS); without them they are skipped and the reason is printed.
#
# The real hardware smoke run is separate: tests/run-hardware-smoke.ps1 (Windows).
# Results (.trx) go to tests/artifacts/, GO timings to tests/artifacts/engine/timing.csv
# (the previous run's file is kept as timing.previous.csv). Exits non-zero when anything fails.
#
# Usage: tests/run-tests.sh [--suite all|plain|engine] [--godot PATH] [--filter EXPR]
#   --godot defaults to $CUE2_GODOT. Point it at the Godot 4.7.1 .NET (mono) editor binary.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
artifacts="$repo/tests/artifacts"
suite="all"
godot="${CUE2_GODOT:-}"
filter=""

while [ $# -gt 0 ]; do
    case "$1" in
        --suite) suite="$2"; shift 2 ;;
        --godot) godot="$2"; shift 2 ;;
        --filter) filter="$2"; shift 2 ;;
        *) echo "run-tests: unknown option $1" >&2; exit 2 ;;
    esac
done
case "$suite" in
    all|plain|engine) ;;
    *) echo "run-tests: --suite must be all, plain or engine" >&2; exit 2 ;;
esac

mkdir -p "$artifacts"
cd "$repo"

echo "run-tests: building Cue2 (Debug)"
dotnet build "$repo/Cue2.csproj" -c Debug -nologo

run_plain() {
    echo "run-tests: plain C# tests"
    local args=(test "$repo/tests/Cue2.Tests.sln" -c Debug -nologo --blame-crash
        --results-directory "$artifacts/plain"
        --logger "trx;LogFileName=plain.trx"
        --logger "console;verbosity=normal")
    if [ -n "$filter" ]; then args+=(--filter "$filter"); fi
    dotnet "${args[@]}"
}

cleanup_engine() {
    # The GdUnit4 adapter writes its runner scene into the project; remove it again.
    rm -rf "$repo/gdunit4_testadapter_v5"
}

run_engine() {
    if [ "$(uname -s)" != "Linux" ]; then
        echo "run-tests: the engine suite runs on Linux and Windows only (user data isolation is set up for those)" >&2
        exit 2
    fi
    if [ -z "$godot" ] || [ ! -x "$godot" ]; then
        echo "run-tests: engine suite needs the Godot 4.7.1 .NET editor binary: pass --godot PATH or set CUE2_GODOT" >&2
        exit 2
    fi

    local engine_dir="$artifacts/engine"
    local data_root="$engine_dir/userdata"
    rm -rf "$data_root"
    mkdir -p "$data_root"
    # Keep the last run's GO timings next to the new ones, for comparing.
    if [ -f "$engine_dir/timing.csv" ]; then mv -f "$engine_dir/timing.csv" "$engine_dir/timing.previous.csv"; fi

    local engine_filter="$filter"
    local no_natives=0
    if ! head -c 4 "$repo/bin/linux64/libavcodec.so.63" 2>/dev/null | grep -q "ELF"; then
        local reason="bin/linux64 holds git LFS pointers, not the FFmpeg libraries (run: git lfs pull --include \"bin/linux64/**\")"
        echo "run-tests: SKIPPING media engine tests: $reason"
        if [ -n "${GITHUB_ACTIONS:-}" ]; then echo "::warning title=Media engine tests skipped::$reason"; fi
        no_natives=1
        local skip="FullyQualifiedName!~Cue2.EngineTests.Media."
        if [ -n "$engine_filter" ]; then engine_filter="($engine_filter)&$skip"; else engine_filter="$skip"; fi
    fi

    local dotnet_root="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"

    # Import once (translations, textures) so the first engine boot finds every resource.
    echo "run-tests: importing project resources"
    XDG_DATA_HOME="$data_root" XDG_CONFIG_HOME="$data_root/config" XDG_CACHE_HOME="$data_root/cache" \
        DOTNET_ROOT="$dotnet_root" "$godot" --headless --path "$repo" --import > "$engine_dir/import.log" 2>&1 \
        || { echo "run-tests: Godot import failed, see $engine_dir/import.log" >&2; exit 1; }

    local settings="$engine_dir/engine.runsettings"
    cat > "$settings" <<SETTINGS
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <RunConfiguration>
    <MaxCpuCount>1</MaxCpuCount>
    <TestSessionTimeout>900000</TestSessionTimeout>
    <EnvironmentVariables>
      <GODOT_BIN>$godot</GODOT_BIN>
      <CUE2_TEST_DATA_ROOT>$data_root</CUE2_TEST_DATA_ROOT>
      <CUE2_TEST_ARTIFACTS>$engine_dir</CUE2_TEST_ARTIFACTS>
      <XDG_DATA_HOME>$data_root</XDG_DATA_HOME>
      <XDG_CONFIG_HOME>$data_root/config</XDG_CONFIG_HOME>
      <XDG_CACHE_HOME>$data_root/cache</XDG_CACHE_HOME>
      <DOTNET_ROOT>$dotnet_root</DOTNET_ROOT>
      <SDL_AUDIO_DRIVER>dummy</SDL_AUDIO_DRIVER>
      <CUE2_TEST_NO_NATIVES>$no_natives</CUE2_TEST_NO_NATIVES>
    </EnvironmentVariables>
  </RunConfiguration>
  <GdUnit4>
    <Parameters>--headless</Parameters>
    <DisplayName>FullyQualifiedName</DisplayName>
  </GdUnit4>
</RunSettings>
SETTINGS

    echo "run-tests: engine tests (Godot: $godot, user data: $data_root)"
    local args=(test "$repo/Cue2.csproj" -c Debug --no-build -nologo
        --settings "$settings"
        --results-directory "$engine_dir"
        --logger "trx;LogFileName=engine.trx"
        --logger "console;verbosity=normal")
    if [ -n "$engine_filter" ]; then args+=(--filter "$engine_filter"); fi
    rm -f "$engine_dir/engine.trx"
    trap cleanup_engine EXIT
    local code=0
    dotnet "${args[@]}" || code=$?
    cleanup_engine
    trap - EXIT
    if [ "$code" -ne 0 ]; then
        engine_failure_hint
        exit "$code"
    fi
    # When Godot crashes early, dotnet test can still exit 0 with nothing run.
    local executed
    executed="$(grep -o '<Counters [^>]*executed="[0-9]*"' "$engine_dir/engine.trx" 2>/dev/null \
        | sed 's/.*executed="\([0-9]*\)"/\1/' || true)"
    if [ -z "$executed" ] || [ "$executed" -eq 0 ]; then
        echo "run-tests: the engine suite ran no tests" >&2
        engine_failure_hint
        exit 1
    fi
}

engine_failure_hint() {
    # "No test is available" from dotnet test usually means Godot itself crashed before any
    # test ran, not that the tests were not found.
    echo "run-tests: engine suite failed. If the output shows 'GodotRuntimeTestRunner ends with exit code'" >&2
    echo "run-tests: followed by 'No test is available', Godot crashed before the tests ran: the last" >&2
    echo "run-tests: 'Godot /err' lines above it give the reason." >&2
}

if [ "$suite" = "all" ] || [ "$suite" = "plain" ]; then run_plain; fi
if [ "$suite" = "all" ] || [ "$suite" = "engine" ]; then run_engine; fi

echo "run-tests: all selected suites passed"
