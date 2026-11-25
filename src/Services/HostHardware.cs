// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Cue2.Services;

/// <summary>
/// Host OS / CPU checks used at startup. Cue2's macOS build ships Apple Silicon natives only.
/// </summary>
/// <remarks>
/// The Godot macOS export is a universal binary, so an Intel Mac can launch the x86_64 slice.
/// FFmpeg, SDL, and MIDI dylibs are arm64, so those loads then fail. This helper flags that
/// hardware so the startup dialog can explain it instead of looking like a generic native error.
/// Godot-free so plain tests can cover the rules.
/// </remarks>
public static class HostHardware
{
	/// <summary>Startup-fault id for an Intel Mac running the macOS build.</summary>
	public const string UnsupportedMacIntelId = "macos-intel";

	/// <summary>Technical detail stored on the hardware fault (log + dialog details).</summary>
	public const string UnsupportedMacIntelDetail =
		"This Mac uses an Intel processor. Cue2 for macOS requires Apple Silicon (M1 or later).";

	/// <summary>
	/// True on this process when Cue2 is running on an Intel Mac.
	/// </summary>
	/// <returns>True when the machine is macOS x86_64 (not Apple Silicon).</returns>
	public static bool IsUnsupportedMacIntel()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			return false;

		return IsUnsupportedMacIntel(
			isMacOs: true,
			RuntimeInformation.OSArchitecture,
			RuntimeInformation.ProcessArchitecture,
			TryQueryAppleSilicon());
	}

	/// <summary>
	/// Rule for whether a Mac is Intel-only hardware Cue2 does not support.
	/// </summary>
	/// <param name="isMacOs">True when the host OS is macOS.</param>
	/// <param name="osArchitecture">OS / machine architecture.</param>
	/// <param name="processArchitecture">Architecture of this process (may be x86_64 under Rosetta).</param>
	/// <param name="appleSilicon">
	/// <see langword="true"/> when <c>hw.optional.arm64</c> is 1, <see langword="false"/> when it is 0
	/// or missing (Intel), <see langword="null"/> when sysctl was not queried.
	/// </param>
	/// <returns>True when this is an Intel Mac.</returns>
	public static bool IsUnsupportedMacIntel(
		bool isMacOs,
		Architecture osArchitecture,
		Architecture processArchitecture,
		bool? appleSilicon)
	{
		if (!isMacOs)
			return false;

		if (appleSilicon == true)
			return false;
		if (appleSilicon == false)
			return true;

		if (osArchitecture == Architecture.Arm64 || processArchitecture == Architecture.Arm64)
			return false;

		return IsX86(osArchitecture) || IsX86(processArchitecture);
	}

	/// <summary>
	/// True when <paramref name="faults"/> includes the Intel Mac hardware fault.
	/// </summary>
	/// <param name="faults">Recorded boot faults.</param>
	/// <returns>True when the unsupported-hardware id is present.</returns>
	public static bool HasUnsupportedMacIntelFault(IReadOnlyList<StartupFault> faults)
	{
		if (faults == null)
			return false;

		for (int i = 0; i < faults.Count; i++)
		{
			if (string.Equals(faults[i].Id, UnsupportedMacIntelId, StringComparison.Ordinal))
				return true;
		}

		return false;
	}

	/// <summary>
	/// Reads <c>hw.optional.arm64</c>. 1 is Apple Silicon; missing/0 is Intel.
	/// </summary>
	/// <returns>
	/// True on Apple Silicon, false on Intel, null when sysctl cannot be called.
	/// </returns>
	internal static bool? TryQueryAppleSilicon()
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			return null;

		try
		{
			int value = 0;
			UIntPtr size = (UIntPtr)sizeof(int);
			int rc = sysctlbyname("hw.optional.arm64", ref value, ref size, IntPtr.Zero, UIntPtr.Zero);
			if (rc == 0)
				return value == 1;

			// ENOENT: the key does not exist on Intel Macs.
			return false;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static bool IsX86(Architecture architecture) =>
		architecture == Architecture.X64 || architecture == Architecture.X86;

	[DllImport("libc", EntryPoint = "sysctlbyname", SetLastError = true)]
	private static extern int sysctlbyname(
		string name,
		ref int oldp,
		ref UIntPtr oldlenp,
		IntPtr newp,
		UIntPtr newlen);
}
