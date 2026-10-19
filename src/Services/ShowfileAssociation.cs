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
/// Exported builds re-apply the per-user association on every launch (no installer).
/// Editor builds do nothing so <c>.c2</c> is never pointed at Godot.
/// </summary>
public static class ShowfileAssociation
{
	/// <summary>Windows ProgID for Cue2 showfiles.</summary>
	public const string WindowsProgId = "Cue2.Showfile";

	/// <summary>Linux/macOS MIME type.</summary>
	public const string MimeType = "application/x-cue2";

	/// <summary>macOS bundle identifier (export preset <c>application/bundle_identifier</c>).</summary>
	public const string MacBundleId = "live.cue2";

	/// <summary>macOS Uniform Type Identifier for Cue2 showfiles.</summary>
	public const string MacUti = "live.cue2.show";

	/// <summary>Linux desktop file id (without path).</summary>
	public const string DesktopFileId = "live.cue2.desktop";

	/// <summary>True when this process should not touch OS associations (Godot editor).</summary>
	public static bool IsEditorBuild => OS.HasFeature("editor");

	/// <summary>Absolute path of the host executable used in open-with commands.</summary>
	public static string HostExecutablePath => OS.GetExecutablePath();

	/// <summary>
	/// Ensures this exported build is the handler for <c>.c2</c>.
	/// Re-writes the association when the exe path changed (portable move / updater)
	/// or when another app had claimed the type.
	/// </summary>
	public static void MaybeRegisterOnFirstLaunch()
	{
		if (IsEditorBuild)
			return;

		try
		{
			if (IsRegisteredToThisBuild())
				return;

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

		string exe = HostExecutablePath;
		if (string.IsNullOrEmpty(exe))
			return false;

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			return WindowsCommandPointsAt(exe) && !WindowsUserChoiceIsForeign();
		if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			return LinuxDesktopPointsAt(exe) && LinuxDefaultIsCue2();
		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
			return MacDefaultIsCue2();
		return false;
	}

	/// <summary>
	/// True when <c>.c2</c> is associated with a different application.
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

		if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
		{
			string handler = CopyMacDefaultHandler(MacUti);
			if (string.IsNullOrEmpty(handler))
				return false;
			return !string.Equals(handler, MacBundleId, StringComparison.OrdinalIgnoreCase);
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
				RegisterMac(exe);
				return true;
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
			using RegistryKey openWith = ext?.CreateSubKey("OpenWithProgids");
			openWith?.SetValue(WindowsProgId, Array.Empty<byte>(), RegistryValueKind.None);
		}

		using (RegistryKey prog = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + WindowsProgId))
		{
			prog?.SetValue("", "Cue2 Show");
			using RegistryKey icon = prog?.CreateSubKey("DefaultIcon");
			icon?.SetValue("", $"\"{exe}\",0");
			using RegistryKey cmd = prog?.CreateSubKey(@"shell\open\command");
			cmd?.SetValue("", $"\"{exe}\" \"%1\"");
		}

		string exeName = Path.GetFileName(exe);
		if (!string.IsNullOrEmpty(exeName))
		{
			using RegistryKey app = Registry.CurrentUser.CreateSubKey(
				@"Software\Classes\Applications\" + exeName);
			using RegistryKey appCmd = app?.CreateSubKey(@"shell\open\command");
			appCmd?.SetValue("", $"\"{exe}\" \"%1\"");
			using RegistryKey supported = app?.CreateSubKey("SupportedTypes");
			supported?.SetValue(".c2", "");
		}

		using (RegistryKey caps = Registry.CurrentUser.CreateSubKey(@"Software\Cue2\Capabilities"))
		{
			caps?.SetValue("ApplicationName", "Cue2");
			caps?.SetValue("ApplicationDescription", "Cue2 show playback");
			caps?.SetValue("ApplicationIcon", $"\"{exe}\",0");
			using RegistryKey fa = caps?.CreateSubKey("FileAssociations");
			fa?.SetValue(".c2", WindowsProgId);
		}

		using (RegistryKey registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
			registered?.SetValue("Cue2", @"Software\Cue2\Capabilities");

		using (RegistryKey fileExts = Registry.CurrentUser.CreateSubKey(
			       @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.c2\OpenWithProgids"))
			fileExts?.SetValue(WindowsProgId, Array.Empty<byte>(), RegistryValueKind.None);

		ClearWindowsUserChoiceIfForeign();
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
	private static bool WindowsUserChoiceIsForeign()
	{
		try
		{
			string prog = ReadWindowsUserChoiceProgId();
			return !string.IsNullOrEmpty(prog) &&
			       !string.Equals(prog, WindowsProgId, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception)
		{
			return false;
		}
	}

	[SupportedOSPlatform("windows")]
	private static string ReadWindowsUserChoiceProgId()
	{
		using (RegistryKey choice = Registry.CurrentUser.OpenSubKey(
			       @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.c2\UserChoice"))
		{
			string prog = choice?.GetValue("ProgId") as string;
			if (!string.IsNullOrEmpty(prog))
				return prog;
		}

		using (RegistryKey latest = Registry.CurrentUser.OpenSubKey(
			       @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.c2\UserChoiceLatest"))
		{
			if (latest == null)
				return string.Empty;
			string prog = latest.GetValue("ProgId") as string;
			if (!string.IsNullOrEmpty(prog))
				return prog;
			using RegistryKey nested = latest.OpenSubKey("ProgId");
			return nested?.GetValue("") as string ?? string.Empty;
		}
	}

	[SupportedOSPlatform("windows")]
	private static void ClearWindowsUserChoiceIfForeign()
	{
		if (!WindowsUserChoiceIsForeign())
			return;

		const string extKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.c2";
		try
		{
			using RegistryKey ext = Registry.CurrentUser.OpenSubKey(extKey, writable: true);
			if (ext == null)
				return;
			foreach (string name in new[] { "UserChoice", "UserChoiceLatest" })
			{
				try
				{
					ext.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
				}
				catch (Exception ex)
				{
					GD.Print($"ShowfileAssociation:ClearWindowsUserChoice - {name}: {ex.Message}");
				}
			}
		}
		catch (Exception ex)
		{
			GD.Print($"ShowfileAssociation:ClearWindowsUserChoice - {ex.Message}");
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

	private static bool LinuxDefaultIsCue2()
	{
		string handler = QueryXdgDefault();
		if (string.IsNullOrEmpty(handler))
			return false;
		return handler.Contains("live.cue2", StringComparison.OrdinalIgnoreCase)
		       || handler.Contains("Cue2", StringComparison.OrdinalIgnoreCase);
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

	private static void RegisterMac(string exe)
	{
		string app = TryFindMacAppBundle(exe);
		if (string.IsNullOrEmpty(app))
			throw new InvalidOperationException("Cue2.app bundle not found from the running executable.");
		if (app.IndexOf("AppTranslocation", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			throw new InvalidOperationException(
				"Cue2 is running from a translocated copy (often Downloads). " +
				"Move Cue2.app to Applications or another local folder, then launch it again to associate .c2 files.");
		}

		const string lsregister =
			"/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";
		if (File.Exists(lsregister))
			RunSilent(lsregister, "-f", "-R", app);

		SetMacDefaultHandler(MacUti);
		string extUti = MacUtiForExtension("c2");
		if (!string.IsNullOrEmpty(extUti) &&
		    !string.Equals(extUti, MacUti, StringComparison.OrdinalIgnoreCase))
			SetMacDefaultHandler(extUti);
	}

	private static bool MacDefaultIsCue2()
	{
		if (HandlerIsCue2(CopyMacDefaultHandler(MacUti)))
			return true;
		string extUti = MacUtiForExtension("c2");
		return !string.IsNullOrEmpty(extUti) && HandlerIsCue2(CopyMacDefaultHandler(extUti));
	}

	private static bool HandlerIsCue2(string handler) =>
		!string.IsNullOrEmpty(handler) &&
		string.Equals(handler, MacBundleId, StringComparison.OrdinalIgnoreCase);

	private static string TryFindMacAppBundle(string exe)
	{
		string dir = Path.GetDirectoryName(exe);
		while (!string.IsNullOrEmpty(dir))
		{
			if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
				return dir;
			dir = Path.GetDirectoryName(dir);
		}

		return null;
	}

	private static void SetMacDefaultHandler(string uti)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || string.IsNullOrEmpty(uti))
			return;

		IntPtr utiCf = CreateCfString(uti);
		IntPtr bundleCf = CreateCfString(MacBundleId);
		try
		{
			int status = LSSetDefaultRoleHandlerForContentType(utiCf, kLSRolesAll, bundleCf);
			if (status != 0)
				GD.Print($"ShowfileAssociation:SetMacDefaultHandler - {uti} status {status}");
		}
		finally
		{
			if (utiCf != IntPtr.Zero)
				CFRelease(utiCf);
			if (bundleCf != IntPtr.Zero)
				CFRelease(bundleCf);
		}
	}

	private static string CopyMacDefaultHandler(string uti)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || string.IsNullOrEmpty(uti))
			return string.Empty;

		IntPtr utiCf = CreateCfString(uti);
		IntPtr handler = IntPtr.Zero;
		try
		{
			handler = LSCopyDefaultRoleHandlerForContentType(utiCf, kLSRolesAll);
			return CfStringToManaged(handler);
		}
		catch (DllNotFoundException)
		{
			return string.Empty;
		}
		catch (EntryPointNotFoundException)
		{
			return string.Empty;
		}
		finally
		{
			if (utiCf != IntPtr.Zero)
				CFRelease(utiCf);
			if (handler != IntPtr.Zero)
				CFRelease(handler);
		}
	}

	private static string MacUtiForExtension(string extension)
	{
		if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || string.IsNullOrEmpty(extension))
			return string.Empty;

		IntPtr tagClass = CreateCfString("public.filename-extension");
		IntPtr tag = CreateCfString(extension);
		IntPtr uti = IntPtr.Zero;
		try
		{
			uti = UTTypeCreatePreferredIdentifierForTag(tagClass, tag, IntPtr.Zero);
			return CfStringToManaged(uti);
		}
		catch (DllNotFoundException)
		{
			return string.Empty;
		}
		catch (EntryPointNotFoundException)
		{
			return string.Empty;
		}
		finally
		{
			if (tagClass != IntPtr.Zero)
				CFRelease(tagClass);
			if (tag != IntPtr.Zero)
				CFRelease(tag);
			if (uti != IntPtr.Zero)
				CFRelease(uti);
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

	private const uint kCFStringEncodingUTF8 = 0x08000100;
	private const uint kLSRolesAll = 0xFFFFFFFF;

	private static IntPtr CreateCfString(string value)
	{
		if (string.IsNullOrEmpty(value))
			return IntPtr.Zero;
		return CFStringCreateWithCString(IntPtr.Zero, value, kCFStringEncodingUTF8);
	}

	private static string CfStringToManaged(IntPtr cf)
	{
		if (cf == IntPtr.Zero)
			return string.Empty;
		long length = CFStringGetLength(cf);
		long maxSize = CFStringGetMaximumSizeForEncoding(length, kCFStringEncodingUTF8) + 1;
		if (maxSize <= 1)
			return string.Empty;
		IntPtr buffer = Marshal.AllocHGlobal((nint)maxSize);
		try
		{
			if (!CFStringGetCString(cf, buffer, maxSize, kCFStringEncodingUTF8))
				return string.Empty;
			return Marshal.PtrToStringUTF8(buffer) ?? string.Empty;
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	[DllImport("shell32.dll")]
	private static extern void ShChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

	[DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
	private static extern IntPtr CFStringCreateWithCString(
		IntPtr alloc,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string str,
		uint encoding);

	[DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
	private static extern long CFStringGetLength(IntPtr theString);

	[DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
	private static extern long CFStringGetMaximumSizeForEncoding(long length, uint encoding);

	[DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
	[return: MarshalAs(UnmanagedType.I1)]
	private static extern bool CFStringGetCString(IntPtr theString, IntPtr buffer, long bufferSize, uint encoding);

	[DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
	private static extern void CFRelease(IntPtr cf);

	[DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
	private static extern int LSSetDefaultRoleHandlerForContentType(
		IntPtr inContentTypeName,
		uint inRole,
		IntPtr inHandlerBundleID);

	[DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
	private static extern IntPtr LSCopyDefaultRoleHandlerForContentType(IntPtr inContentTypeName, uint inRole);

	[DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
	private static extern IntPtr UTTypeCreatePreferredIdentifierForTag(
		IntPtr inTagClass,
		IntPtr inTag,
		IntPtr inConformingToUTI);
}
