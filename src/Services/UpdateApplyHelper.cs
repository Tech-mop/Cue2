// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Formats.Tar;
using System.Runtime.InteropServices;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Finds the payload inside a downloaded archive and launches a local (not downloaded)
/// script that waits for Cue2 to exit, copies files, and relaunches.
/// </summary>
public static class UpdateApplyHelper
{
	/// <summary>Extracted payload ready to copy over the install root.</summary>
	public sealed class ExtractedLayout
	{
		/// <summary>Folder or <c>.app</c> to copy onto <see cref="UpdateEndpoints.GetInstallRoot"/>.</summary>
		public string PayloadRoot { get; init; } = string.Empty;

		/// <summary>Relative path of the new host executable (inside the payload), or empty for macOS <c>open</c>.</summary>
		public string RelativeRelaunch { get; init; } = string.Empty;

		/// <summary>True when payload is a macOS <c>.app</c>.</summary>
		public bool IsMacApp { get; init; }
	}

	private static readonly string[] LinuxHostNames =
	{
		"Cue2",
		"Cue2.x86_64",
		"Cue2.arm64",
		"Cue2.x86_32"
	};

	private static readonly string[] LinuxSkipSuffixes =
	{
		".pck", ".so", ".dll", ".zip", ".gz", ".tgz", ".json", ".txt", ".md",
		".debug", ".pdb", ".sha256"
	};

	/// <summary>
	/// Extracts <paramref name="archivePath"/> into <paramref name="destDir"/> (zip or tar.gz).
	/// </summary>
	public static void ExtractArchive(string archivePath, string destDir)
	{
		if (Directory.Exists(destDir))
			Directory.Delete(destDir, recursive: true);
		Directory.CreateDirectory(destDir);

		string lower = archivePath.ToLowerInvariant();
		if (lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal))
		{
			using var fs = File.OpenRead(archivePath);
			using var gzip = new GZipStream(fs, CompressionMode.Decompress);
			TarFile.ExtractToDirectory(gzip, destDir, overwriteFiles: true);
			return;
		}

		ZipFile.ExtractToDirectory(archivePath, destDir, overwriteFiles: true);
	}

	/// <summary>
	/// Locates <c>Cue2.app</c>, a Cue2 host exe, or a single inner folder that contains them.
	/// </summary>
	public static ExtractedLayout FindLayout(string extractDir)
	{
		if (string.IsNullOrEmpty(extractDir) || !Directory.Exists(extractDir))
			return null;

		string app = FindMacApp(extractDir);
		if (!string.IsNullOrEmpty(app))
		{
			return new ExtractedLayout
			{
				PayloadRoot = app,
				RelativeRelaunch = string.Empty,
				IsMacApp = true
			};
		}

		string host = FindHostBinary(extractDir);
		if (!string.IsNullOrEmpty(host))
		{
			return new ExtractedLayout
			{
				PayloadRoot = extractDir,
				RelativeRelaunch = Path.GetRelativePath(extractDir, host),
				IsMacApp = false
			};
		}

		// Zip of a single top-level folder.
		string[] dirs = Directory.GetDirectories(extractDir);
		string[] files = Directory.GetFiles(extractDir);
		if (dirs.Length == 1 && files.Length == 0)
			return FindLayout(dirs[0]);

		return null;
	}

	/// <summary>
	/// Writes a local helper script and starts it with environment variables for from/to/relaunch.
	/// </summary>
	/// <returns>True when the helper process started.</returns>
	public static bool LaunchReplaceAndRelaunch(string fromPayload, string toInstallRoot, string relaunchPath, bool isMacApp)
	{
		string fromFull = Path.GetFullPath(fromPayload);
		string toFull = Path.GetFullPath(toInstallRoot);
		string updatesRoot = Path.GetFullPath(UpdateEndpoints.GetUpdatesDirectory());

		if (!IsUnderDirectory(fromFull, updatesRoot))
		{
			GD.PrintErr("UpdateApplyHelper:LaunchReplaceAndRelaunch - payload is not under user://updates.");
			return false;
		}

		int pid = System.Environment.ProcessId;
		string tempDir = Path.Combine(Path.GetTempPath(), "Cue2Update");
		Directory.CreateDirectory(tempDir);
		string logPath = Path.Combine(tempDir, "apply-update.log");

		try
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				return LaunchWindows(tempDir, logPath, pid, fromFull, toFull, relaunchPath);
			return LaunchUnix(tempDir, logPath, pid, fromFull, toFull, relaunchPath, isMacApp);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"UpdateApplyHelper:LaunchReplaceAndRelaunch - {ex.Message}");
			return false;
		}
	}

	/// <summary>
	/// True when <paramref name="path"/> is <paramref name="root"/> or a file/folder under it.
	/// </summary>
	internal static bool IsUnderDirectory(string path, string root)
	{
		if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
			return false;

		string full = Path.GetFullPath(path)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string rootFull = Path.GetFullPath(root)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		StringComparison cmp = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		if (string.Equals(full, rootFull, cmp))
			return true;

		string prefix = rootFull + Path.DirectorySeparatorChar;
		if (full.StartsWith(prefix, cmp))
			return true;
		if (Path.DirectorySeparatorChar != Path.AltDirectorySeparatorChar)
		{
			string alt = rootFull + Path.AltDirectorySeparatorChar;
			if (full.StartsWith(alt, cmp))
				return true;
		}

		return false;
	}

	private static string FindMacApp(string root)
	{
		if (root.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
			return root;
		foreach (string dir in Directory.GetDirectories(root))
		{
			if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
				return dir;
		}

		return null;
	}

	private static string FindHostBinary(string root)
	{
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return FindWindowsHost(root);
		return FindUnixHost(root);
	}

	private static string FindWindowsHost(string root)
	{
		string exact = Path.Combine(root, "Cue2.exe");
		if (File.Exists(exact))
			return exact;

		foreach (string exe in Directory.GetFiles(root, "*.exe"))
		{
			string name = Path.GetFileName(exe);
			if (name.Contains(".console.", StringComparison.OrdinalIgnoreCase))
				continue;
			if (name.StartsWith("Cue2", StringComparison.OrdinalIgnoreCase))
				return exe;
		}

		return null;
	}

	private static string FindUnixHost(string root)
	{
		foreach (string name in LinuxHostNames)
		{
			string path = Path.Combine(root, name);
			if (File.Exists(path))
				return path;
		}

		foreach (string file in Directory.GetFiles(root))
		{
			string name = Path.GetFileName(file);
			if (!name.StartsWith("Cue2", StringComparison.OrdinalIgnoreCase))
				continue;
			if (HasSkippedUnixSuffix(name))
				continue;
			return file;
		}

		return null;
	}

	private static bool HasSkippedUnixSuffix(string fileName)
	{
		string lower = fileName.ToLowerInvariant();
		foreach (string suffix in LinuxSkipSuffixes)
		{
			if (lower.EndsWith(suffix, StringComparison.Ordinal))
				return true;
		}

		return false;
	}

	private static bool LaunchWindows(string tempDir, string logPath, int pid, string from, string to, string relaunch)
	{
		string script = Path.Combine(tempDir, "apply-update.ps1");
		File.WriteAllText(script, WindowsScript);
		var psi = new ProcessStartInfo
		{
			FileName = "powershell.exe",
			UseShellExecute = false,
			CreateNoWindow = true,
			ArgumentList =
			{
				"-NoProfile",
				"-ExecutionPolicy", "Bypass",
				"-File", script
			}
		};
		SetHelperEnvironment(psi, pid, from, to, relaunch, logPath);
		return TryStart(psi);
	}

	private static bool LaunchUnix(string tempDir, string logPath, int pid, string from, string to, string relaunch, bool isMacApp)
	{
		string script = Path.Combine(tempDir, "apply-update.sh");
		bool mac = isMacApp && RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
		File.WriteAllText(script, mac ? MacScript : LinuxScript);

		string bash = File.Exists("/bin/bash") ? "/bin/bash" : "/usr/bin/bash";
		if (File.Exists("/bin/chmod"))
		{
			var chmod = new ProcessStartInfo
			{
				FileName = "/bin/chmod",
				ArgumentList = { "+x", script },
				UseShellExecute = false,
				CreateNoWindow = true
			};
			Process.Start(chmod)?.WaitForExit(2000);
		}

		var psi = new ProcessStartInfo
		{
			FileName = bash,
			ArgumentList = { script },
			UseShellExecute = false,
			CreateNoWindow = true
		};
		SetHelperEnvironment(psi, pid, from, to, relaunch, logPath);
		return TryStart(psi);
	}

	private static void SetHelperEnvironment(ProcessStartInfo psi, int pid, string from, string to, string relaunch, string logPath)
	{
		psi.Environment["CUE2_WAIT_PID"] = pid.ToString();
		psi.Environment["CUE2_FROM"] = from;
		psi.Environment["CUE2_TO"] = to;
		psi.Environment["CUE2_RELAUNCH"] = relaunch ?? "";
		psi.Environment["CUE2_LOG"] = logPath ?? "";
	}

	private static bool TryStart(ProcessStartInfo psi)
	{
		Process proc = Process.Start(psi);
		if (proc == null)
		{
			GD.PrintErr("UpdateApplyHelper:TryStart - helper process did not start.");
			return false;
		}

		return true;
	}

	private const string WindowsScript =
		@"$ErrorActionPreference = 'Continue'
$log = $env:CUE2_LOG
if (-not $log) { $log = Join-Path $env:TEMP 'Cue2Update\apply-update.log' }
$logDir = Split-Path -Parent $log
if ($logDir -and -not (Test-Path -LiteralPath $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
function Log($m) { Add-Content -LiteralPath $log -Value (('[{0:o}] {1}' -f (Get-Date).ToUniversalTime(), $m)) -Encoding UTF8 }

$pidWait = [int]$env:CUE2_WAIT_PID
$from = $env:CUE2_FROM
$to = $env:CUE2_TO
$relaunch = $env:CUE2_RELAUNCH
Log ""wait pid=$pidWait from=$from to=$to relaunch=$relaunch""

$deadline = (Get-Date).AddSeconds(60)
while (Get-Process -Id $pidWait -ErrorAction SilentlyContinue) {
  if ((Get-Date) -gt $deadline) { Log 'timeout waiting for Cue2 to exit'; break }
  Start-Sleep -Milliseconds 400
}
Start-Sleep -Milliseconds 800

if (-not (Test-Path -LiteralPath $from)) { Log ""missing payload $from""; exit 1 }
if (-not (Test-Path -LiteralPath $to)) { New-Item -ItemType Directory -Path $to | Out-Null }

$ok = $false
for ($i = 1; $i -le 30; $i++) {
  & robocopy $from $to /E /IS /IT /R:2 /W:1 /NFL /NDL /NJH /NJS | Out-Null
  $rc = $LASTEXITCODE
  Log ""robocopy try $i exit $rc""
  if ($rc -lt 8) { $ok = $true; break }
  Start-Sleep -Milliseconds 500
}
if (-not $ok) { Log 'robocopy failed'; exit 1 }

$fromNames = @{}
Get-ChildItem -LiteralPath $from -File -ErrorAction SilentlyContinue | ForEach-Object { $fromNames[$_.Name] = $true }
Get-ChildItem -LiteralPath $to -File -ErrorAction SilentlyContinue | ForEach-Object {
  if ($fromNames.ContainsKey($_.Name)) { return }
  $n = $_.Name
  if ($n -like 'Cue2*.exe' -or $n -like 'Cue2*.pck') {
    Log ""remove stale $n""
    Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
  }
}

if (-not $relaunch -or -not (Test-Path -LiteralPath $relaunch)) { Log ""relaunch missing $relaunch""; exit 1 }
$wd = Split-Path -Parent $relaunch
Start-Process -FilePath $relaunch -WorkingDirectory $wd
Log 'relaunched'
";

	private const string MacScript =
		@"#!/bin/bash
LOG=""${CUE2_LOG:-${TMPDIR:-/tmp}/Cue2Update/apply-update.log}""
mkdir -p ""$(dirname ""$LOG"")""
log() { echo ""$(date -u +%Y-%m-%dT%H:%M:%SZ) $*"" >> ""$LOG""; }
WAIT_PID=""$CUE2_WAIT_PID""
FROM=""$CUE2_FROM""
TO=""$CUE2_TO""
log ""mac wait=$WAIT_PID from=$FROM to=$TO""
i=0
while kill -0 ""$WAIT_PID"" 2>/dev/null; do
  i=$((i+1))
  if [ ""$i"" -gt 150 ]; then log ""timeout waiting for Cue2 to exit""; break; fi
  sleep 0.4
done
sleep 0.8
if [ ! -d ""$FROM"" ]; then log ""missing payload""; exit 1; fi
PARENT=""$(dirname ""$TO"")""
BASE=""$(basename ""$TO"")""
NEW=""$PARENT/$BASE.new""
OLD=""$PARENT/$BASE.old""
rm -rf ""$NEW"" ""$OLD""
if ! ditto ""$FROM"" ""$NEW""; then
  log ""ditto failed""
  rm -rf ""$NEW""
  exit 1
fi
if [ -e ""$TO"" ]; then
  if ! mv ""$TO"" ""$OLD""; then
    log ""could not move live app aside""
    rm -rf ""$NEW""
    exit 1
  fi
fi
if ! mv ""$NEW"" ""$TO""; then
  log ""could not move new app into place""
  if [ -e ""$OLD"" ]; then mv ""$OLD"" ""$TO"" || true; fi
  rm -rf ""$NEW""
  exit 1
fi
open ""$TO""
rm -rf ""$OLD"" || true
log ""ok""
exit 0
";

	private const string LinuxScript =
		@"#!/bin/bash
LOG=""${CUE2_LOG:-${TMPDIR:-/tmp}/Cue2Update/apply-update.log}""
mkdir -p ""$(dirname ""$LOG"")""
log() { echo ""$(date -u +%Y-%m-%dT%H:%M:%SZ) $*"" >> ""$LOG""; }
WAIT_PID=""$CUE2_WAIT_PID""
FROM=""$CUE2_FROM""
TO=""$CUE2_TO""
RELAUNCH=""$CUE2_RELAUNCH""
log ""linux wait=$WAIT_PID from=$FROM to=$TO relaunch=$RELAUNCH""
i=0
while kill -0 ""$WAIT_PID"" 2>/dev/null; do
  i=$((i+1))
  if [ ""$i"" -gt 150 ]; then log ""timeout waiting for Cue2 to exit""; break; fi
  sleep 0.4
done
sleep 0.8
if [ ! -d ""$FROM"" ]; then log ""missing payload""; exit 1; fi
mkdir -p ""$TO""
copied=0
for t in 1 2 3 4 5 6 7 8 9 10; do
  if cp -a ""$FROM""/. ""$TO""/; then copied=1; break; fi
  log ""cp try $t failed""
  sleep 0.5
done
if [ ""$copied"" -ne 1 ]; then log ""copy failed""; exit 1; fi
for f in ""$TO""/Cue2*; do
  [ -f ""$f"" ] || continue
  base=""$(basename ""$f"")""
  if [ ! -e ""$FROM/$base"" ]; then
    case ""$base"" in
      *.so*) ;;
      Cue2|Cue2.*|*.pck)
        log ""remove stale $base""
        rm -f ""$f"" || true
        ;;
    esac
  fi
done
if [ -z ""$RELAUNCH"" ] || [ ! -f ""$RELAUNCH"" ]; then
  log ""relaunch missing""
  exit 1
fi
chmod +x ""$RELAUNCH"" || true
wd=""$(dirname ""$RELAUNCH"")""
( cd ""$wd"" && nohup ""$RELAUNCH"" >/dev/null 2>&1 & )
log ""relaunched""
exit 0
";
}
