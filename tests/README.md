# Cue2 automated tests

## Layout

| Folder | What | Runs with |
|---|---|---|
| `tests/Cue2.Tests/` | Plain C# tests (NUnit) for Cue2 logic that does not need the Godot engine | `dotnet test tests/Cue2.Tests.sln` |
| `tests/engine/` | Engine tests (GdUnit4Net) that run inside headless Godot 4.7.1 with the real autoloads and main window | the run scripts below |
| `tests/engine/Media/` | Engine tests that create or play media; they need the FFmpeg libraries from git LFS | the run scripts below |

`tests/.gdignore` keeps Godot (editor, import and export) out of this folder. `Cue2.csproj`
removes `tests/**` from the game build and compiles `tests/engine/**` (with the GdUnit4
packages) into Debug builds only, so exports never contain test code or test packages.

## Run locally

Use the scripts: they run the same suites as `.github/workflows/tests.yml` and set up the
isolated user data folder for the engine suite.

Windows (Windows PowerShell 5.1 or later):

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1 -Godot C:\path\to\Godot_v4.7.1-stable_mono_win64_console.exe
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1 -Suite Plain
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1 -Suite Engine -Godot C:\path\to\Godot_v4.7.1-stable_mono_win64_console.exe
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1 -Suite Plain -Filter "FullyQualifiedName~Timecode"
```

Linux:

```bash
tests/run-tests.sh --godot /path/to/Godot_v4.7.1-stable_mono_linux.x86_64
tests/run-tests.sh --suite plain --filter "FullyQualifiedName~Timecode"
```

`-Godot` / `--godot` can be left out when the `CUE2_GODOT` environment variable points at the
Godot binary. Results (`.trx`, `timing.csv`, the import log) are written to `tests/artifacts/`
(ignored by git).

## CI (GitHub Actions)

`.github/workflows/tests.yml` runs on every push and pull request, with two jobs that call the
same script as a local Linux run:

- **Plain C# tests**: `tests/run-tests.sh --suite plain` on Ubuntu with .NET 8. No LFS files.
- **Engine tests**: downloads Godot 4.7.1 .NET (checked against its SHA-512, cached), fetches
  only `bin/linux64/**` from git LFS (cached by the LFS ids, so the quota is used only when the
  libraries change), then runs `tests/run-tests.sh --suite engine` headless with the silent SDL
  audio driver. When the libraries cannot be fetched, the media tests are skipped with a
  warning that says why, and the other engine tests still run.

Both jobs write a short summary (totals, GO timing medians) to the run page and upload their
results as artifacts: `plain-test-results` (`plain.trx`) and `engine-test-results`
(`engine.trx`, `timing.csv`, the import log). The hardware smoke run is never part of CI.
`tests/test-summary.py` builds the summary.

## GO timing

The engine harness turns on `PlaybackTimingRecorder` (off in the app), which records the GO
handler time, content start, first queued audio, output start and first picture as numbers
next to the existing log lines. `tests/engine/Media/GoTimingTests.cs` repeats GO on an audio
and a video cue, appends every point to `tests/artifacts/engine/timing.csv` and fails only when
a median is over a loose budget (GO handler 100 ms, content start 150 ms, first audio or
picture 250 ms). Each run moves the last file to `timing.previous.csv`, so two runs can be
compared side by side. Columns: `test,label,stage,ms_after_go,ms_after_play`.

## Real hardware smoke run (Windows, before a release)

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-hardware-smoke.ps1 -Godot C:\path\to\Godot_v4.7.1-stable_mono_win64_console.exe
```

This runs the engine tests in a real window with the real audio device and video path, still
with isolated user data, and writes `tests\artifacts\hardware\engine.trx` and `timing.csv`.
It needs the FFmpeg DLLs in `bin\win64` (`git lfs pull --include "bin/win64/**"`). It is never
part of the default run or CI.

## User data safety

Engine tests start Godot with `APPDATA` (Windows) or `XDG_DATA_HOME` (Linux) pointing at
`tests/artifacts/engine/userdata`, created fresh for every run, so the run never reads or
writes the real Cue2 settings, recent shows or logs. Every engine suite also checks that
Godot's user folder is inside that folder before doing anything.

Do not set `GODOT_BIN` globally and do not run `dotnet test Cue2.csproj` by hand: the GdUnit4
adapter launches Godot from `GODOT_BIN`, and without the scripts the user data would not be
redirected. The scripts write `GODOT_BIN` into a generated runsettings file only.

During a run the GdUnit4 adapter writes `gdunit4_testadapter_v5/` into the project folder; the
scripts delete it afterwards and git ignores it.

## Writing plain tests

- Plain tests may use Godot value types (`Vector2`, `Color`, `Mathf`) but must not call into
  the engine (`GD.Print`, `Godot.Collections.Dictionary`, `Variant`, `Json`, nodes). Doing so
  crashes the whole test host; `--blame-crash` in the run scripts names the test that did it.
- Cue2 internals are visible to the test project (`InternalsVisibleTo`).
- Name tests after the behaviour they check.

## Writing engine tests

- One `[TestSuite]` class per file (GdUnit4 only finds the first suite in a file).
- Mark suites `[RequireGodotRuntime]` and start with `await EngineHarness.BootAsync()` in a
  `[Before]` method; use `EngineHarness.ResetShowAsync()` in `[BeforeTest]` for an empty show.
- Generate media with `Fixtures/MediaFixtures` and build shows with `Fixtures/ShowBuilder`;
  never add media files to git. Put tests that need media under `tests/engine/Media/`.
- Wait with `EngineHarness.FramesAsync` / `UntilAsync`, never with `Thread.Sleep`.
- Finish with `EngineHarness.AssertNoNewErrors(mark)` so new engine errors fail the test.
