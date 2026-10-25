// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Cue2.Services;

namespace Cue2.Tests.Updates;

/// <summary>
/// Runs the real Install and Restart helper script against a fake install folder in a temp
/// folder: PowerShell on Windows, bash on Linux. The test for the other OS is skipped.
/// </summary>
[TestFixture]
public class UpdateHelperScriptTests
{
    private const string Version = "9.9.9";

    private string _root;
    private string _from;
    private string _to;
    private string _backup;
    private string _result;
    private string _log;

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>Program file name for this OS.</summary>
    private static string ProgramName => IsWindows ? "Cue2.exe" : "Cue2.x86_64";

    /// <summary>Builds a fake install (old files) and an update payload (new files).</summary>
    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cue2-tests-helper-" + Guid.NewGuid().ToString("N"));
        _from = Path.Combine(_root, "updates", "payload");
        _to = Path.Combine(_root, "install");
        _backup = Path.Combine(_root, "updates", "backup");
        _result = Path.Combine(_root, "updates", "apply-result.txt");
        _log = Path.Combine(_root, "helper.log");

        Write(_to, ProgramName, Program("old"));
        Write(_to, "Cue2.pck", "old pck");
        Write(_to, "Cue2.stale.pck", "stale pck");
        Write(_to, "keep.txt", "user file");

        Write(_from, ProgramName, Program("new"));
        Write(_from, "Cue2.pck", "new pck");
        Write(_from, Path.Combine("data", "added.txt"), "added");
    }

    /// <summary>Removes the temp folder.</summary>
    [TearDown]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // A relaunched fake program may still hold the folder for a moment; the OS temp cleanup gets it.
        }
    }

    /// <summary>A good payload is copied in, stale program files go, user files stay, the result says ok.</summary>
    [Test]
    [Platform("Win,Linux")]
    public void GoodPayloadIsInstalled()
    {
        int code = RunHelper(relaunch: Path.Combine(_to, ProgramName));

        var result = ReadResult();
        Assert.That(result["status"], Is.EqualTo("ok"), File.Exists(_log) ? File.ReadAllText(_log) : "no log");
        Assert.That(code, Is.EqualTo(0));
        Assert.That(result["version"], Is.EqualTo(Version));
        Assert.That(Read(_to, "Cue2.pck"), Is.EqualTo("new pck"));
        Assert.That(Read(_to, ProgramName), Is.EqualTo(Program("new")));
        Assert.That(Read(_to, Path.Combine("data", "added.txt")), Is.EqualTo("added"));
        Assert.That(Read(_to, "keep.txt"), Is.EqualTo("user file"));
        Assert.That(File.Exists(Path.Combine(_to, "Cue2.stale.pck")), Is.False);
        Assert.That(Directory.Exists(_backup), Is.False);
    }

    /// <summary>When the new version has no program file, the old install comes back exactly.</summary>
    [Test]
    [Platform("Win,Linux")]
    public void MissingProgramRestoresOldVersion()
    {
        int code = RunHelper(relaunch: Path.Combine(_to, "missing-program"));

        var result = ReadResult();
        Assert.That(result["status"], Is.EqualTo("failed"));
        Assert.That(result["detail"], Does.Contain("The previous version was restored."));
        Assert.That(code, Is.Not.EqualTo(0));
        Assert.That(Read(_to, "Cue2.pck"), Is.EqualTo("old pck"));
        Assert.That(Read(_to, ProgramName), Is.EqualTo(Program("old")));
        Assert.That(Read(_to, "Cue2.stale.pck"), Is.EqualTo("stale pck"));
        Assert.That(Read(_to, "keep.txt"), Is.EqualTo("user file"));
        Assert.That(File.Exists(Path.Combine(_to, "data", "added.txt")), Is.False);
        Assert.That(Directory.Exists(Path.Combine(_to, "data")), Is.False);
    }

    /// <summary>Without a payload nothing in the install changes.</summary>
    [Test]
    [Platform("Win,Linux")]
    public void MissingPayloadChangesNothing()
    {
        Directory.Delete(_from, true);
        int code = RunHelper(relaunch: Path.Combine(_to, ProgramName));

        var result = ReadResult();
        Assert.That(result["status"], Is.EqualTo("failed"));
        Assert.That(result["detail"], Does.Contain("Nothing was changed."));
        Assert.That(code, Is.Not.EqualTo(0));
        Assert.That(Read(_to, "Cue2.pck"), Is.EqualTo("old pck"));
        Assert.That(Read(_to, "Cue2.stale.pck"), Is.EqualTo("stale pck"));
    }

    /// <summary>
    /// Writes the helper for this OS and runs it to the end, with the environment Cue2 gives it.
    /// The process it waits for has already exited. Old relaunch points at a missing file.
    /// </summary>
    private int RunHelper(string relaunch)
    {
        string script;
        ProcessStartInfo psi;
        if (IsWindows)
        {
            script = Path.Combine(_root, "apply-update.ps1");
            File.WriteAllText(script, UpdateApplyHelper.WindowsScript);
            psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }
            };
            psi.Environment["CUE2_ROBOCOPY"] = Path.Combine(Environment.SystemDirectory, "robocopy.exe");
        }
        else
        {
            script = Path.Combine(_root, "apply-update.sh");
            File.WriteAllText(script, UpdateApplyHelper.LinuxScript);
            psi = new ProcessStartInfo { FileName = "/bin/bash", ArgumentList = { script } };
        }

        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = _root;
        psi.Environment["CUE2_WAIT_PID"] = ExitedProcessId().ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["CUE2_FROM"] = _from;
        psi.Environment["CUE2_TO"] = _to;
        psi.Environment["CUE2_RELAUNCH"] = relaunch;
        psi.Environment["CUE2_OLD_RELAUNCH"] = Path.Combine(_root, "no-old-program");
        psi.Environment["CUE2_BACKUP"] = _backup;
        psi.Environment["CUE2_RESULT"] = _result;
        psi.Environment["CUE2_VERSION"] = Version;
        psi.Environment["CUE2_LOG"] = _log;

        using var process = Process.Start(psi);
        Assert.That(process, Is.Not.Null);
        if (!process.WaitForExit(60000))
        {
            process.Kill(true);
            Assert.Fail("The helper script did not finish within 60 s.");
        }
        return process.ExitCode;
    }

    /// <summary>Id of a process that has already exited, for the helper's wait loop.</summary>
    private static int ExitedProcessId()
    {
        var psi = IsWindows
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 0")
            : new ProcessStartInfo("/bin/true");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        using var process = Process.Start(psi);
        process!.WaitForExit();
        return process.Id;
    }

    private Dictionary<string, string> ReadResult()
    {
        Assert.That(File.Exists(_result), Is.True, "The helper wrote no result file.");
        var values = new Dictionary<string, string>();
        foreach (string line in File.ReadAllLines(_result))
        {
            int eq = line.IndexOf('=');
            if (eq > 0)
                values[line.Substring(0, eq).Trim('\uFEFF')] = line.Substring(eq + 1);
        }
        return values;
    }

    /// <summary>A fake program that exits at once when the helper relaunches it.</summary>
    private static string Program(string which) =>
        IsWindows ? $"not a real program ({which})" : $"#!/bin/sh\n# {which}\nexit 0\n";

    private static void Write(string dir, string relative, string text)
    {
        string path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string Read(string dir, string relative) => File.ReadAllText(Path.Combine(dir, relative));
}
