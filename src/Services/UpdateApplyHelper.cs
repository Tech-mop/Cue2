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
	/// Extracts <paramref name="archivePath"/> (zip or tar.gz) into a staging folder, moves it to
	/// <paramref name="destDir"/>, then writes a marker holding the archive SHA-256. Only a folder
	/// with that marker counts as complete (<see cref="IsExtractComplete"/>), so an extract cut off
	/// by a quit or crash is never installed. On macOS a zip is extracted with <c>ditto</c>. Pool thread only.
	/// </summary>
	/// <param name="archivePath">Verified archive.</param>
	/// <param name="destDir">Final extract folder.</param>
	/// <param name="archiveSha256">Hex SHA-256 of the archive, stored in the marker.</param>
	public static void ExtractArchive(string archivePath, string destDir, string archiveSha256)
	{
		DeleteExtract(destDir);
		string staging = StagingPath(destDir);
		Directory.CreateDirectory(staging);

		string lower = archivePath.ToLowerInvariant();
		if (lower.EndsWith(".tar.gz", StringComparison.Ordinal) || lower.EndsWith(".tgz", StringComparison.Ordinal))
		{
			using var fs = File.OpenRead(archivePath);
			using var gzip = new GZipStream(fs, CompressionMode.Decompress);
			TarFile.ExtractToDirectory(gzip, staging, overwriteFiles: true);
		}
		else if (OperatingSystem.IsMacOS() && File.Exists(DittoPath))
		{
			ExtractZipWithDitto(archivePath, staging);
		}
		else
		{
			ZipFile.ExtractToDirectory(archivePath, staging, overwriteFiles: true);
		}

		Directory.Move(staging, destDir);
		File.WriteAllText(MarkerPath(destDir), (archiveSha256 ?? string.Empty).Trim().ToLowerInvariant());
	}

	private const string DittoPath = "/usr/bin/ditto";

	/// <summary>Upper bound for one ditto run; a release archive takes seconds.</summary>
	private static readonly TimeSpan DittoTimeout = TimeSpan.FromMinutes(10);

	/// <summary>
	/// Extracts a macOS zip with <c>ditto</c>, which keeps symlinks, permissions and extended
	/// attributes inside the <c>.app</c> bundle (ZipFile writes symlinks as plain files, which
	/// breaks framework bundles and the code signature). Entry names are checked first, since
	/// ditto does not refuse paths outside the target. Pool thread only.
	/// </summary>
	private static void ExtractZipWithDitto(string archivePath, string staging)
	{
		string stagingFull = Path.GetFullPath(staging);
		using (ZipArchive zip = ZipFile.OpenRead(archivePath))
		{
			foreach (ZipArchiveEntry entry in zip.Entries)
			{
				string target = Path.GetFullPath(Path.Combine(stagingFull, entry.FullName));
				if (Path.IsPathRooted(entry.FullName) || !IsUnderDirectory(target, stagingFull))
					throw new InvalidDataException($"Archive entry is outside the extract folder: {entry.FullName}");
			}
		}

		var psi = new ProcessStartInfo(DittoPath)
		{
			UseShellExecute = false,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			CreateNoWindow = true
		};
		psi.ArgumentList.Add("-x");
		psi.ArgumentList.Add("-k");
		psi.ArgumentList.Add(archivePath);
		psi.ArgumentList.Add(stagingFull);

		using Process process = Process.Start(psi) ?? throw new IOException("Could not start ditto.");
		var stderr = process.StandardError.ReadToEndAsync();
		var stdout = process.StandardOutput.ReadToEndAsync();
		if (!process.WaitForExit(DittoTimeout))
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch (Exception)
			{
				// Already exited.
			}

			throw new IOException("ditto did not finish extracting the update.");
		}

		process.WaitForExit();
		_ = stdout.Result;
		if (process.ExitCode != 0)
			throw new IOException($"ditto failed ({process.ExitCode}): {stderr.Result.Trim()}");
	}

	/// <summary>
	/// True when <paramref name="destDir"/> was fully extracted from the archive with
	/// <paramref name="archiveSha256"/>. Small file read; any thread.
	/// </summary>
	public static bool IsExtractComplete(string destDir, string archiveSha256)
	{
		try
		{
			string marker = MarkerPath(destDir);
			if (string.IsNullOrWhiteSpace(archiveSha256) || !Directory.Exists(destDir) || !File.Exists(marker))
				return false;
			return string.Equals(File.ReadAllText(marker).Trim(), archiveSha256.Trim(), StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception)
		{
			return false;
		}
	}

	/// <summary>Removes an extract folder, its staging folder and its marker. Best effort.</summary>
	public static void DeleteExtract(string destDir)
	{
		if (string.IsNullOrEmpty(destDir))
			return;
		TryDeleteFile(MarkerPath(destDir));
		TryDeleteDirectory(StagingPath(destDir));
		TryDeleteDirectory(destDir);
	}

	private static string StagingPath(string destDir) => destDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".tmp";

	/// <summary>Marker sits beside the folder, never inside it, so it is never copied into the install.</summary>
	private static string MarkerPath(string destDir) => destDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".ok";

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path))
				Directory.Delete(path, recursive: true);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"UpdateApplyHelper:TryDeleteDirectory - {ex.Message}");
		}
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

	/// <summary>Result line file the helper writes into <c>user://updates</c>.</summary>
	public const string ResultFileName = "apply-result.txt";

	/// <summary>Written before the helper starts; a pending file with no result means the helper never ran.</summary>
	public const string PendingFileName = "apply-pending.txt";

	/// <summary>Folder under <c>user://updates</c> that holds the files the helper replaces until it succeeds.</summary>
	public const string BackupDirName = "backup";

	/// <summary>Helper log in the OS temp folder.</summary>
	public static string HelperLogPath => Path.Combine(Path.GetTempPath(), "Cue2Update", "apply-update.log");

	/// <summary>Outcome of the last Install and Restart, read on the next launch.</summary>
	public sealed class ApplyResult
	{
		/// <summary>True when the helper reported success.</summary>
		public bool Succeeded { get; init; }

		/// <summary>True when Cue2 asked for an install but the helper left no result (it never ran).</summary>
		public bool HelperDidNotRun { get; init; }

		/// <summary>Version the helper installed or tried to install.</summary>
		public string Version { get; init; } = string.Empty;

		/// <summary>Helper message (English, from the script).</summary>
		public string Detail { get; init; } = string.Empty;
	}

	/// <summary>
	/// Writes a local helper script and starts it. The helper waits for Cue2 to exit, backs up and
	/// replaces the install, restores it on failure, relaunches, and writes a result file.
	/// </summary>
	/// <param name="fromPayload">Extracted payload (folder or <c>.app</c>) under <c>user://updates</c>.</param>
	/// <param name="toInstallRoot">Install folder (or <c>.app</c> on macOS) to replace.</param>
	/// <param name="relaunchPath">New host to start after a successful copy.</param>
	/// <param name="oldRelaunchPath">Running host, started again when the update fails.</param>
	/// <param name="isMacApp">True for a macOS <c>.app</c> swap.</param>
	/// <param name="version">Version being installed (written into the result file).</param>
	/// <param name="error">Why the helper did not start, for the log.</param>
	/// <returns>True when the helper process started.</returns>
	public static bool LaunchReplaceAndRelaunch(
		string fromPayload,
		string toInstallRoot,
		string relaunchPath,
		string oldRelaunchPath,
		bool isMacApp,
		string version,
		out string error)
	{
		error = string.Empty;
		string fromFull = Path.GetFullPath(fromPayload);
		string toFull = Path.GetFullPath(toInstallRoot);
		string updatesRoot = Path.GetFullPath(UpdateEndpoints.GetUpdatesDirectory());

		if (!IsUnderDirectory(fromFull, updatesRoot))
		{
			error = "payload is not under user://updates.";
			GD.PrintErr($"UpdateApplyHelper:LaunchReplaceAndRelaunch - {error}");
			return false;
		}

		try
		{
			string tempDir = Path.GetDirectoryName(HelperLogPath);
			Directory.CreateDirectory(tempDir);
			string resultPath = Path.Combine(updatesRoot, ResultFileName);
			if (File.Exists(resultPath))
				File.Delete(resultPath);
			File.WriteAllText(Path.Combine(updatesRoot, PendingFileName), version ?? string.Empty);

			var env = new HelperEnvironment(
				System.Environment.ProcessId,
				fromFull,
				toFull,
				relaunchPath ?? string.Empty,
				oldRelaunchPath ?? string.Empty,
				Path.Combine(updatesRoot, BackupDirName),
				resultPath,
				version ?? string.Empty,
				HelperLogPath);

			bool started = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
				? LaunchWindows(tempDir, env)
				: LaunchUnix(tempDir, env, isMacApp);
			if (!started)
			{
				error = "helper process did not start.";
				TryDeleteFile(Path.Combine(updatesRoot, PendingFileName));
			}

			return started;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			GD.PrintErr($"UpdateApplyHelper:LaunchReplaceAndRelaunch - {ex.Message}");
			TryDeleteFile(Path.Combine(updatesRoot, PendingFileName));
			return false;
		}
	}

	/// <summary>
	/// Reads and removes the result of the last Install and Restart. Plain System.IO; any thread.
	/// </summary>
	/// <param name="updatesDir">Absolute <c>user://updates</c> folder.</param>
	/// <returns>The result, or null when no install was attempted since the last read.</returns>
	public static ApplyResult ReadAndClearApplyResult(string updatesDir)
	{
		if (string.IsNullOrEmpty(updatesDir))
			return null;

		string resultPath = Path.Combine(updatesDir, ResultFileName);
		string pendingPath = Path.Combine(updatesDir, PendingFileName);
		try
		{
			if (File.Exists(resultPath))
			{
				string status = string.Empty, version = string.Empty, detail = string.Empty;
				foreach (string raw in File.ReadAllLines(resultPath))
				{
					int eq = raw.IndexOf('=');
					if (eq <= 0)
						continue;
					string key = raw[..eq].Trim();
					string value = raw[(eq + 1)..].Trim();
					if (key == "status") status = value;
					else if (key == "version") version = value;
					else if (key == "detail") detail = value;
				}

				TryDeleteFile(resultPath);
				TryDeleteFile(pendingPath);
				return new ApplyResult
				{
					Succeeded = string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase),
					Version = version,
					Detail = detail
				};
			}

			if (File.Exists(pendingPath))
			{
				string version = File.ReadAllText(pendingPath).Trim();
				TryDeleteFile(pendingPath);
				return new ApplyResult { HelperDidNotRun = true, Version = version };
			}
		}
		catch (Exception ex)
		{
			GD.PrintErr($"UpdateApplyHelper:ReadAndClearApplyResult - {ex.Message}");
		}

		return null;
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

	/// <summary>Everything the helper script reads from its environment.</summary>
	private sealed record HelperEnvironment(
		int WaitPid,
		string From,
		string To,
		string Relaunch,
		string OldRelaunch,
		string Backup,
		string Result,
		string Version,
		string Log);

	private static bool LaunchWindows(string tempDir, HelperEnvironment env)
	{
		string script = Path.Combine(tempDir, "apply-update.ps1");
		File.WriteAllText(script, WindowsScript);
		var psi = new ProcessStartInfo
		{
			// Full System32 paths: a bare name is searched in the Cue2 folder first.
			FileName = SystemToolPath(Path.Combine("WindowsPowerShell", "v1.0", "powershell.exe")),
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = tempDir,
			ArgumentList =
			{
				"-NoProfile",
				"-ExecutionPolicy", "Bypass",
				"-File", script
			}
		};
		SetHelperEnvironment(psi, env);
		psi.Environment["CUE2_ROBOCOPY"] = SystemToolPath("robocopy.exe");
		return TryStart(psi);
	}

	/// <summary>Absolute path under the Windows System32 folder, or the bare name when it is missing.</summary>
	private static string SystemToolPath(string relative)
	{
		string full = Path.Combine(System.Environment.SystemDirectory, relative);
		return File.Exists(full) ? full : Path.GetFileName(relative);
	}

	private static bool LaunchUnix(string tempDir, HelperEnvironment env, bool isMacApp)
	{
		string script = Path.Combine(tempDir, "apply-update.sh");
		bool mac = isMacApp && RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
		File.WriteAllText(script, mac ? MacScript : LinuxScript);

		string bash = File.Exists("/bin/bash") ? "/bin/bash" : "/usr/bin/bash";
		var psi = new ProcessStartInfo
		{
			FileName = bash,
			ArgumentList = { script },
			UseShellExecute = false,
			CreateNoWindow = true,
			WorkingDirectory = tempDir
		};
		SetHelperEnvironment(psi, env);
		return TryStart(psi);
	}

	private static void SetHelperEnvironment(ProcessStartInfo psi, HelperEnvironment env)
	{
		psi.Environment["CUE2_WAIT_PID"] = env.WaitPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
		psi.Environment["CUE2_FROM"] = env.From;
		psi.Environment["CUE2_TO"] = env.To;
		psi.Environment["CUE2_RELAUNCH"] = env.Relaunch;
		psi.Environment["CUE2_OLD_RELAUNCH"] = env.OldRelaunch;
		psi.Environment["CUE2_BACKUP"] = env.Backup;
		psi.Environment["CUE2_RESULT"] = env.Result;
		psi.Environment["CUE2_VERSION"] = env.Version;
		psi.Environment["CUE2_LOG"] = env.Log;
	}

	private static void TryDeleteFile(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (Exception)
		{
			// Best effort; a stale file is reported once and then replaced.
		}
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
if ($logDir -and -not (Test-Path -LiteralPath $logDir)) { [System.IO.Directory]::CreateDirectory($logDir) | Out-Null }
function Log($m) { Add-Content -LiteralPath $log -Value (('[{0:o}] {1}' -f (Get-Date).ToUniversalTime(), $m)) -Encoding UTF8 }
function Result($status, $detail) {
  if (-not $env:CUE2_RESULT) { return }
  try {
    Set-Content -LiteralPath $env:CUE2_RESULT -Value @(""status=$status"", ""version=$($env:CUE2_VERSION)"", ""detail=$detail"") -Encoding UTF8 -ErrorAction Stop
  } catch { Log ""could not write result file: $_"" }
}
function Relaunch($path) {
  if ($path -and (Test-Path -LiteralPath $path -PathType Leaf)) {
    # Process.Start takes the path literally (Start-Process would read [ ] as wildcards).
    try {
      $psi = New-Object System.Diagnostics.ProcessStartInfo
      $psi.FileName = $path
      $psi.WorkingDirectory = [System.IO.Path]::GetDirectoryName($path)
      $psi.UseShellExecute = $false
      [System.Diagnostics.Process]::Start($psi) | Out-Null
      Log ""relaunched $path""
    } catch { Log ""relaunch failed for ${path}: $_"" }
  } else { Log ""relaunch missing $path"" }
}
function RelPath($full, $root) { return $full.Substring($root.Length).TrimStart('\', '/') }

$pidWait = [int]$env:CUE2_WAIT_PID
$from = $env:CUE2_FROM
$to = $env:CUE2_TO
$relaunch = $env:CUE2_RELAUNCH
$oldRelaunch = $env:CUE2_OLD_RELAUNCH
$backup = $env:CUE2_BACKUP
$robocopy = $env:CUE2_ROBOCOPY
if (-not $robocopy) { $robocopy = 'robocopy' }
Log ""wait pid=$pidWait from=$from to=$to relaunch=$relaunch backup=$backup""

$deadline = (Get-Date).AddSeconds(120)
while (Get-Process -Id $pidWait -ErrorAction SilentlyContinue) {
  if ((Get-Date) -gt $deadline) {
    Log 'Cue2 did not exit in time; nothing was changed'
    Result 'failed' 'Cue2 did not close in time. Nothing was changed.'
    exit 1
  }
  Start-Sleep -Milliseconds 400
}
Start-Sleep -Milliseconds 800

if (-not (Test-Path -LiteralPath $from -PathType Container)) {
  Log ""missing payload $from""
  Result 'failed' 'The downloaded update was missing. Nothing was changed.'
  Relaunch $oldRelaunch
  exit 1
}

# Back up every install file the payload replaces; remember the files and folders it adds.
$created = New-Object System.Collections.Generic.List[string]
$createdDirs = New-Object System.Collections.Generic.List[string]
try {
  if (-not $backup) { throw 'no backup folder' }
  if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction Stop }
  [System.IO.Directory]::CreateDirectory($backup) | Out-Null
  [System.IO.Directory]::CreateDirectory($to) | Out-Null
  foreach ($dir in [System.IO.Directory]::EnumerateDirectories($from, '*', [System.IO.SearchOption]::AllDirectories)) {
    $rel = RelPath $dir $from
    if (-not [System.IO.Directory]::Exists([System.IO.Path]::Combine($to, $rel))) { $createdDirs.Add($rel) }
  }
  foreach ($file in [System.IO.Directory]::EnumerateFiles($from, '*', [System.IO.SearchOption]::AllDirectories)) {
    $rel = RelPath $file $from
    $dst = [System.IO.Path]::Combine($to, $rel)
    if ([System.IO.File]::Exists($dst)) {
      $bk = [System.IO.Path]::Combine($backup, $rel)
      [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($bk)) | Out-Null
      [System.IO.File]::Copy($dst, $bk, $true)
    } else {
      $created.Add($rel)
    }
  }
} catch {
  Log ""backup failed: $_""
  if ($backup -and (Test-Path -LiteralPath $backup)) { Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue }
  Result 'failed' 'Could not back up the current version. Nothing was changed.'
  Relaunch $oldRelaunch
  exit 1
}
Log ""backup done; $($created.Count) new files""

# True when $dst already holds the backed-up bytes. Such a file was never changed by the copy
# and may still be locked, so restore leaves it alone.
function SameContent($bk, $dst) {
  try {
    $a = New-Object System.IO.FileInfo($bk)
    $b = New-Object System.IO.FileInfo($dst)
    if (-not $b.Exists -or $a.Length -ne $b.Length) { return $false }
    return (Get-FileHash -LiteralPath $bk -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash
  } catch {
    return $false
  }
}

# Put the backed-up files back and remove what the update added. True when complete.
function Restore {
  $ok = $true
  foreach ($rel in $created) {
    $p = [System.IO.Path]::Combine($to, $rel)
    try { if ([System.IO.File]::Exists($p)) { [System.IO.File]::Delete($p) } } catch { $ok = $false; Log ""restore: could not remove ${rel}: $_"" }
  }
  $dirs = @($createdDirs | Sort-Object -Descending)
  foreach ($rel in $dirs) {
    $p = [System.IO.Path]::Combine($to, $rel)
    try {
      if ([System.IO.Directory]::Exists($p) -and -not [System.IO.Directory]::EnumerateFileSystemEntries($p).GetEnumerator().MoveNext()) { [System.IO.Directory]::Delete($p) }
    } catch { Log ""restore: could not remove folder ${rel}: $_"" }
  }
  foreach ($bk in [System.IO.Directory]::EnumerateFiles($backup, '*', [System.IO.SearchOption]::AllDirectories)) {
    $rel = RelPath $bk $backup
    $dst = [System.IO.Path]::Combine($to, $rel)
    if (SameContent $bk $dst) { continue }
    $done = $false
    for ($t = 1; $t -le 10 -and -not $done; $t++) {
      try {
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($dst)) | Out-Null
        [System.IO.File]::Copy($bk, $dst, $true)
        $done = $true
      } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $done) { $ok = $false; Log ""restore failed for $rel"" }
  }
  return $ok
}

function FailAndRestore($why) {
  if (Restore) {
    Log 'restored previous version'
    Result 'failed' ""$why The previous version was restored.""
    Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
  } else {
    Log ""restore incomplete; backup kept at $backup""
    Result 'failed' ""$why The previous version could not be fully restored. Its files are in $backup.""
  }
  Relaunch $oldRelaunch
  exit 1
}

$ok = $false
for ($i = 1; $i -le 10; $i++) {
  & $robocopy $from $to /E /IS /IT /R:2 /W:1 /NFL /NDL /NJH /NJS | Out-Null
  $rc = $LASTEXITCODE
  Log ""robocopy try $i exit $rc""
  if ($rc -lt 8) { $ok = $true; break }
  Start-Sleep -Milliseconds 500
}
if (-not $ok) { FailAndRestore 'Copying the new files failed.' }

$fromNames = @{}
Get-ChildItem -LiteralPath $from -File -Force -ErrorAction SilentlyContinue | ForEach-Object { $fromNames[$_.Name] = $true }
Get-ChildItem -LiteralPath $to -File -Force -ErrorAction SilentlyContinue | ForEach-Object {
  if ($fromNames.ContainsKey($_.Name)) { return }
  $n = $_.Name
  if ($n -like 'Cue2*.exe' -or $n -like 'Cue2*.pck') {
    try {
      [System.IO.File]::Copy($_.FullName, [System.IO.Path]::Combine($backup, $n), $true)
      Log ""remove stale $n""
      Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
    } catch { Log ""kept stale ${n}: $_"" }
  }
}

if (-not $relaunch -or -not (Test-Path -LiteralPath $relaunch -PathType Leaf)) {
  Log ""relaunch missing $relaunch""
  FailAndRestore 'The new version has no program file.'
}
Relaunch $relaunch
Result 'ok' 'Installed.'
Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
Log 'ok'
exit 0
";

	private const string MacScript =
		@"#!/bin/bash
LOG=""${CUE2_LOG:-${TMPDIR:-/tmp}/Cue2Update/apply-update.log}""
mkdir -p ""$(dirname ""$LOG"")""
log() { echo ""$(date -u +%Y-%m-%dT%H:%M:%SZ) $*"" >> ""$LOG""; }
result() {
  [ -n ""$CUE2_RESULT"" ] || return 0
  printf 'status=%s\nversion=%s\ndetail=%s\n' ""$1"" ""$CUE2_VERSION"" ""$2"" > ""$CUE2_RESULT"" 2>/dev/null || log ""could not write result file""
}
WAIT_PID=""$CUE2_WAIT_PID""
FROM=""$CUE2_FROM""
TO=""$CUE2_TO""
log ""mac wait=$WAIT_PID from=$FROM to=$TO""

# Report, reopen the app that is in place now (old or restored), stop.
fail_reopen() {
  log ""$1""
  result failed ""$2""
  if [ -d ""$TO"" ]; then open ""$TO"" || log ""could not reopen $TO""; fi
  exit 1
}

i=0
while kill -0 ""$WAIT_PID"" 2>/dev/null; do
  i=$((i+1))
  if [ ""$i"" -gt 300 ]; then
    log ""Cue2 did not exit in time; nothing was changed""
    result failed ""Cue2 did not close in time. Nothing was changed.""
    exit 1
  fi
  sleep 0.4
done
sleep 0.8
if [ ! -d ""$FROM"" ]; then
  fail_reopen ""missing payload"" ""The downloaded update was missing. Nothing was changed.""
fi
PARENT=""$(dirname ""$TO"")""
BASE=""$(basename ""$TO"")""
NEW=""$PARENT/$BASE.new""
OLD=""$PARENT/$BASE.old""
rm -rf ""$NEW"" ""$OLD""
if ! ditto ""$FROM"" ""$NEW"" 2>>""$LOG""; then
  rm -rf ""$NEW""
  fail_reopen ""ditto failed"" ""Copying the new version failed. Nothing was changed.""
fi
if [ -e ""$TO"" ]; then
  if ! mv ""$TO"" ""$OLD"" 2>>""$LOG""; then
    rm -rf ""$NEW""
    fail_reopen ""could not move live app aside"" ""Cue2 could not replace the app. Nothing was changed.""
  fi
fi
if ! mv ""$NEW"" ""$TO"" 2>>""$LOG""; then
  if [ -e ""$OLD"" ]; then mv ""$OLD"" ""$TO"" 2>>""$LOG"" || log ""could not restore $OLD""; fi
  rm -rf ""$NEW""
  if [ -d ""$TO"" ]; then
    fail_reopen ""could not move new app into place"" ""Cue2 could not move the new version into place. The previous version was restored.""
  fi
  fail_reopen ""restore failed"" ""Cue2 could not move the new version into place and could not restore the previous version. It is at $OLD.""
fi
open ""$TO"" || log ""could not open $TO""
rm -rf ""$OLD"" || true
result ok ""Installed.""
log ""ok""
exit 0
";

	private const string LinuxScript =
		@"#!/bin/bash
LOG=""${CUE2_LOG:-${TMPDIR:-/tmp}/Cue2Update/apply-update.log}""
mkdir -p ""$(dirname ""$LOG"")""
log() { echo ""$(date -u +%Y-%m-%dT%H:%M:%SZ) $*"" >> ""$LOG""; }
result() {
  [ -n ""$CUE2_RESULT"" ] || return 0
  printf 'status=%s\nversion=%s\ndetail=%s\n' ""$1"" ""$CUE2_VERSION"" ""$2"" > ""$CUE2_RESULT"" 2>/dev/null || log ""could not write result file""
}
relaunch() {
  local p=""$1""
  if [ -n ""$p"" ] && [ -f ""$p"" ]; then
    chmod +x ""$p"" 2>/dev/null || true
    ( cd ""$(dirname ""$p"")"" && nohup ""$p"" >/dev/null 2>&1 & )
    log ""relaunched $p""
  else
    log ""relaunch missing $p""
  fi
}
WAIT_PID=""$CUE2_WAIT_PID""
FROM=""$CUE2_FROM""
TO=""$CUE2_TO""
RELAUNCH=""$CUE2_RELAUNCH""
OLD=""$CUE2_OLD_RELAUNCH""
BACKUP=""$CUE2_BACKUP""
CREATED=""$BACKUP.created""
DIRS=""$BACKUP.dirs""
log ""linux wait=$WAIT_PID from=$FROM to=$TO relaunch=$RELAUNCH backup=$BACKUP""

# Back up every install file the payload replaces; list the files it adds.
backup_install() {
  rm -rf -- ""$BACKUP"" ""$CREATED"" ""$DIRS"" ""$BACKUP.list"" || return 1
  mkdir -p -- ""$BACKUP"" || return 1
  : > ""$CREATED"" || return 1
  : > ""$DIRS"" || return 1
  ( cd -- ""$FROM"" && find . -mindepth 1 -type d -print0 ) > ""$BACKUP.list"" || return 1
  local rel
  while IFS= read -r -d '' rel; do
    rel=""${rel#./}""
    [ -e ""$TO/$rel"" ] || printf '%s\0' ""$rel"" >> ""$DIRS"" || return 1
  done < ""$BACKUP.list""
  ( cd -- ""$FROM"" && find . \( -type f -o -type l \) -print0 ) > ""$BACKUP.list"" || return 1
  while IFS= read -r -d '' rel; do
    rel=""${rel#./}""
    if [ -e ""$TO/$rel"" ] || [ -L ""$TO/$rel"" ]; then
      mkdir -p -- ""$(dirname -- ""$BACKUP/$rel"")"" || return 1
      cp -a -- ""$TO/$rel"" ""$BACKUP/$rel"" 2>>""$LOG"" || return 1
    else
      printf '%s\0' ""$rel"" >> ""$CREATED"" || return 1
    fi
  done < ""$BACKUP.list""
  rm -f -- ""$BACKUP.list""
  return 0
}

# Put the backed-up files back and remove the files the update added.
restore_install() {
  local ok=0 rel t
  if [ -f ""$CREATED"" ]; then
    while IFS= read -r -d '' rel; do
      rm -f -- ""$TO/$rel"" || ok=1
    done < ""$CREATED""
  fi
  # Folders the update added, children before parents (reverse order); only removed when empty.
  if [ -f ""$DIRS"" ]; then
    while IFS= read -r -d '' rel; do
      rmdir -- ""$TO/$rel"" 2>/dev/null || true
    done < <(sort -z -r < ""$DIRS"")
  fi
  ( cd -- ""$BACKUP"" && find . \( -type f -o -type l \) -print0 ) > ""$BACKUP.list"" 2>>""$LOG"" || return 1
  local put
  while IFS= read -r -d '' rel; do
    rel=""${rel#./}""
    # Skip files the copy never changed: they may be busy, and they are already right.
    if [ ! -L ""$BACKUP/$rel"" ] && [ ! -L ""$TO/$rel"" ] && [ -f ""$TO/$rel"" ] && cmp -s -- ""$BACKUP/$rel"" ""$TO/$rel""; then
      continue
    fi
    put=0
    for t in 1 2 3 4 5; do
      if mkdir -p -- ""$(dirname -- ""$TO/$rel"")"" && cp -a -- ""$BACKUP/$rel"" ""$TO/$rel"" 2>>""$LOG""; then put=1; break; fi
      sleep 0.5
    done
    if [ ""$put"" -ne 1 ]; then log ""restore failed for $rel""; ok=1; fi
  done < ""$BACKUP.list""
  rm -f -- ""$BACKUP.list""
  return $ok
}

# Restore, report, relaunch the old version, stop.
fail_and_restore() {
  if restore_install; then
    log ""restored previous version""
    result failed ""$1 The previous version was restored.""
    rm -rf -- ""$BACKUP"" ""$CREATED"" ""$DIRS""
  else
    log ""restore incomplete; backup kept at $BACKUP""
    result failed ""$1 The previous version could not be fully restored. Its files are in $BACKUP.""
  fi
  relaunch ""$OLD""
  exit 1
}

i=0
while kill -0 ""$WAIT_PID"" 2>/dev/null; do
  i=$((i+1))
  if [ ""$i"" -gt 300 ]; then
    log ""Cue2 did not exit in time; nothing was changed""
    result failed ""Cue2 did not close in time. Nothing was changed.""
    exit 1
  fi
  sleep 0.4
done
sleep 0.8

if [ ! -d ""$FROM"" ]; then
  log ""missing payload""
  result failed ""The downloaded update was missing. Nothing was changed.""
  relaunch ""$OLD""
  exit 1
fi
if [ -z ""$BACKUP"" ] || ! mkdir -p -- ""$TO"" || ! backup_install; then
  log ""backup failed""
  rm -rf -- ""$BACKUP"" ""$CREATED"" ""$DIRS"" ""$BACKUP.list""
  result failed ""Could not back up the current version. Nothing was changed.""
  relaunch ""$OLD""
  exit 1
fi
log ""backup done""

copied=0
for t in 1 2 3 4 5 6 7 8 9 10; do
  if cp -a -- ""$FROM""/. ""$TO""/ 2>>""$LOG""; then copied=1; break; fi
  log ""cp try $t failed""
  sleep 0.5
done
if [ ""$copied"" -ne 1 ]; then
  log ""copy failed""
  fail_and_restore ""Copying the new files failed.""
fi

for f in ""$TO""/Cue2*; do
  [ -f ""$f"" ] || continue
  base=""$(basename ""$f"")""
  if [ ! -e ""$FROM/$base"" ]; then
    case ""$base"" in
      *.so*) ;;
      Cue2|Cue2.*|*.pck)
        if cp -a -- ""$f"" ""$BACKUP/$base""; then
          log ""remove stale $base""
          rm -f -- ""$f"" || true
        fi
        ;;
    esac
  fi
done

if [ -z ""$RELAUNCH"" ] || [ ! -f ""$RELAUNCH"" ]; then
  log ""relaunch missing""
  fail_and_restore ""The new version has no program file.""
fi
relaunch ""$RELAUNCH""
result ok ""Installed.""
rm -rf -- ""$BACKUP"" ""$CREATED"" ""$DIRS""
log ""ok""
exit 0
";
}
