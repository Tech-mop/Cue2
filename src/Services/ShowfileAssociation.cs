// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Godot;
using Microsoft.Win32;

namespace Cue2.Services;

/// <summary>
/// Registers <c>.c2</c> showfiles so the OS shows the Cue2 icon and opens them with this build.
/// macOS uses the exported Info.plist (no runtime register). Editor builds do nothing.
/// </summary>
public static class ShowfileAssociation
{
	/// <summary>Windows ProgID for Cue2 showfiles.</summary>
	public const string WindowsProgId = "Cue2.Showfile";

	/// <summary>Linux/macOS MIME type.</summary>
	public const string MimeType = "application/x-cue2";

	/// <summary>Linux desktop file id (without path).</summary>
	public const string DesktopFileId = "live.cue2.desktop";

	/// <summary>True when this process should not touch OS associations (Godot editor).</summary>
	public static bool IsEditorBuild => OS.HasFeature("editor");

	/// <summary>Absolute path of the host executable used in open-with commands.</summary>
	public static string HostExecutablePath => OS.GetExecutablePath();

	/// <summary>
	/// Registers this exported build as the handler for <c>.c2</c> when no other app owns the type,
	/// or when we already own it but the exe path changed (portable move / updater).
	/// </summary>
	public static void MaybeRegisterOnFirstLaunch()
	{
		if (IsEditorBuild)
			return;
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			return;

		try
		{
			if (IsRegisteredToThisBuild())
				return;
			if (HasForeignHandler())
			{
				GD.Print("ShowfileAssociation:MaybeRegisterOnFirstLaunch - .c2 already owned by another app.");
				return;
			}

			if (TryRegister(out string error))
				GD.Print("ShowfileAssociation:MaybeRegisterOnFirstLaunch - registered .c2 with this build.");
			else
				GD.PrintErr($"ShowfileAssociation:MaybeRegisterOnFirstLaunch - {error}");
		}
		catch (Exception ex)
		{
			GD.PrintErr($"ShowfileAssociation:MaybeRegisterOnFirstLaunch - {ex.Message}");
		}
	}

	/// <summary>
	/// True when this machine already routes <c>.c2</c> to the currently running executable.
	/// </summary>
	public static bool IsRegisteredToThisBuild()
	{
		if (IsEditorBuild)
			return false;
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			return true;

		string exe = HostExecutablePath;
		if (string.IsNullOrEmpty(exe))
			return false;

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return WindowsCommandPointsAt(exe);
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			return LinuxDesktopPointsAt(exe);
		return false;
	}

	/// <summary>
	/// True when <c>.c2</c> is associated with a different application (do not steal on first launch).
	/// </summary>
	public static bool HasForeignHandler()
	{
		if (IsEditorBuild)
			return false;
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			string prog = ReadUserClassesDefault(@".c2");
			return !string.IsNullOrEmpty(prog) &&
			       !string.Equals(prog, WindowsProgId, StringComparison.OrdinalIgnoreCase);
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
		{
			string handler = QueryXdgDefault();
			if (string.IsNullOrEmpty(handler))
				return false;
			return !handler.Contains("live.cue2", StringComparison.OrdinalIgnoreCase)
			       && !handler.Contains("Cue2", StringComparison.OrdinalIgnoreCase);
		}

		return false;
	}

	/// <summary>
	/// Writes OS associations for this executable (user-level, no admin).
	/// </summary>
	/// <returns>True on success.</returns>
	public static bool TryRegister(out string error)
	{
		error = string.Empty;
		if (IsEditorBuild)
		{
			error = "File association is only for exported Cue2 builds.";
			return false;
		}

		string exe = HostExecutablePath;
		if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
		{
			error = "Could not find the Cue2 executable.";
			return false;
		}

		try
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				RegisterWindows(exe);
				return true;
			}

			if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				RegisterLinux(exe);
				return true;
			}

			if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			{
				error = "macOS registers .c2 from the Cue2.app Info.plist. Reinstall or rebuild the app bundle.";
				return false;
			}

			error = "This operating system does not support in-app .c2 registration.";
			return false;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

	[SupportedOSPlatform("windows")]
	private static void RegisterWindows(string exe)
	{
		using (RegistryKey ext = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.c2"))
		{
			ext?.SetValue("", WindowsProgId);
			ext?.SetValue("Content Type", MimeType);
		}

		using (RegistryKey prog = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + WindowsProgId))
		{
			prog?.SetValue("", "Cue2 Show");
			using RegistryKey icon = prog?.CreateSubKey("DefaultIcon");
			icon?.SetValue("", $"\"{exe}\",0");
			using RegistryKey cmd = prog?.CreateSubKey(@"shell\open\command");
			cmd?.SetValue("", $"\"{exe}\" \"%1\"");
		}

		ShChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
	}

	[SupportedOSPlatform("windows")]
	private static bool WindowsCommandPointsAt(string exe)
	{
		try
		{
			using RegistryKey cmd = Registry.CurrentUser.OpenSubKey(
				$@"Software\Classes\{WindowsProgId}\shell\open\command");
			string value = cmd?.GetValue("") as string ?? string.Empty;
			return value.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;
		}
		catch (Exception)
		{
			return false;
		}
	}

	[SupportedOSPlatform("windows")]
	private static string ReadUserClassesDefault(string subKey)
	{
		try
		{
			using RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + subKey.TrimStart('\\'));
			return key?.GetValue("") as string ?? string.Empty;
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	private static void RegisterLinux(string exe)
	{
		string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrEmpty(home))
			home = System.Environment.GetEnvironmentVariable("HOME") ?? "";
		if (string.IsNullOrEmpty(home))
			throw new InvalidOperationException("HOME is not set.");

		string mimePkg = Path.Combine(home, ".local/share/mime/packages");
		Directory.CreateDirectory(mimePkg);
		File.WriteAllText(Path.Combine(mimePkg, "live.cue2.xml"), LinuxMimeXml, new UTF8Encoding(false));

		string apps = Path.Combine(home, ".local/share/applications");
		Directory.CreateDirectory(apps);
		string execEscaped = exe.Replace("\"", "\\\"");
		string desktop =
			"[Desktop Entry]\n" +
			"Type=Application\n" +
			"Name=Cue2\n" +
			"Comment=Cue2 show playback\n" +
			$"Exec=\"{execEscaped}\" %f\n" +
			"MimeType=" + MimeType + ";\n" +
			"Icon=live.cue2\n" +
			"StartupWMClass=Cue2\n" +
			"Terminal=false\n" +
			"Categories=AudioVideo;Audio;\n";
		File.WriteAllText(Path.Combine(apps, DesktopFileId), desktop, new UTF8Encoding(false));

		WriteLinuxIcons(home);
		RunSilent("update-mime-database", Path.Combine(home, ".local/share/mime"));
		RunSilent("update-desktop-database", apps);
		RunSilent("xdg-mime", "default", DesktopFileId, MimeType);
	}

	private static bool LinuxDesktopPointsAt(string exe)
	{
		string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrEmpty(home))
			home = System.Environment.GetEnvironmentVariable("HOME") ?? "";
		string desktopPath = Path.Combine(home, ".local/share/applications", DesktopFileId);
		if (!File.Exists(desktopPath))
			return false;
		try
		{
			string text = File.ReadAllText(desktopPath);
			return text.IndexOf(exe, StringComparison.Ordinal) >= 0;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static string QueryXdgDefault()
	{
		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = "xdg-mime",
				ArgumentList = { "query", "default", MimeType },
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			using var proc = Process.Start(psi);
			if (proc == null)
				return string.Empty;
			string output = proc.StandardOutput.ReadToEnd();
			proc.WaitForExit(3000);
			return (output ?? string.Empty).Trim();
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	private static void WriteLinuxIcons(string home)
	{
		var tex = ResourceLoader.Load<Texture2D>("res://icon.svg");
		if (tex == null)
			return;

		int[] sizes = { 16, 32, 48, 128, 256 };
		foreach (int size in sizes)
		{
			Image img = tex.GetImage();
			if (img == null)
				continue;
			if (img.GetWidth() != size || img.GetHeight() != size)
				img.Resize(size, size, Image.Interpolation.Lanczos);

			string mimeDir = Path.Combine(home, $".local/share/icons/hicolor/{size}x{size}/mimetypes");
			string appDir = Path.Combine(home, $".local/share/icons/hicolor/{size}x{size}/apps");
			Directory.CreateDirectory(mimeDir);
			Directory.CreateDirectory(appDir);
			img.SavePng(Path.Combine(mimeDir, "application-x-cue2.png"));
			img.SavePng(Path.Combine(appDir, "live.cue2.png"));
		}
	}

	private static void RunSilent(string fileName, params string[] args)
	{
		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = fileName,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardError = true,
				RedirectStandardOutput = true
			};
			foreach (string a in args)
				psi.ArgumentList.Add(a);
			Process.Start(psi)?.WaitForExit(8000);
		}
		catch (Exception ex)
		{
			GD.Print($"ShowfileAssociation:RunSilent - {fileName}: {ex.Message}");
		}
	}

	private const string LinuxMimeXml =
		"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
		"<mime-info xmlns=\"http://www.freedesktop.org/standards/shared-mime-info\">\n" +
		"  <mime-type type=\"application/x-cue2\">\n" +
		"    <comment>Cue2 Show</comment>\n" +
		"    <glob pattern=\"*.c2\"/>\n" +
		"    <icon name=\"application-x-cue2\"/>\n" +
		"  </mime-type>\n" +
		"</mime-info>\n";

	[DllImport("shell32.dll")]
	private static extern void ShChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
