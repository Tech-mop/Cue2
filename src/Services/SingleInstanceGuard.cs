// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Ensures one Cue2 process per user. A second launch forwards a <c>.c2</c> path and exits.
/// Editor runs are not limited (multiple Godot instances are normal).
/// </summary>
public partial class SingleInstanceGuard : Node
{
	/// <summary>True when this process is exiting because another Cue2 already owns the session.</summary>
	public static bool IsSecondary { get; private set; }

	private const string MutexName = "live.cue2.instance";
	private static readonly string PipeName = "live.cue2.open." + SanitizeToken(System.Environment.UserName);

	private static System.Threading.Mutex _mutex;
	private static FileStream _lockFile;

	private CancellationTokenSource _cts;
	private Socket _listenSocket;
	private NamedPipeServerStream _pipeServer;
	private readonly object _ioLock = new();

	/// <summary>Raised on the main thread when another process asked this instance to open a show.</summary>
	[Signal]
	public delegate void OpenShowRequestedEventHandler(string path);

	/// <summary>
	/// Tries to become the unique exported instance. On failure, forwards <paramref name="pendingShowPath"/>
	/// to the running process.
	/// </summary>
	/// <returns>True when this process should continue as primary.</returns>
	public static bool TryClaimExclusive(string pendingShowPath)
	{
		if (OS.HasFeature("editor"))
			return true;

		try
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				return TryClaimWindows(pendingShowPath);
			return TryClaimUnix(pendingShowPath);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SingleInstanceGuard:TryClaimExclusive - {ex.Message}");
			return true;
		}
	}

	/// <inheritdoc />
	public override void _Ready()
	{
		if (IsSecondary || OS.HasFeature("editor"))
			return;
		CallDeferred(nameof(BeginAcceptLoop));
	}

	/// <inheritdoc />
	public override void _ExitTree()
	{
		// Cancel first so AcceptAsync / WaitForConnectionAsync can unwind without a
		// blocking Close() from the main thread (that deadlock freezes exported quit).
		try
		{
			_cts?.Cancel();
		}
		catch
		{
			// ignore
		}

		Socket listenSocket;
		NamedPipeServerStream pipeServer;
		lock (_ioLock)
		{
			listenSocket = _listenSocket;
			pipeServer = _pipeServer;
			_listenSocket = null;
			_pipeServer = null;
		}

		try
		{
			listenSocket?.Dispose();
		}
		catch
		{
			// ignore
		}

		try
		{
			pipeServer?.Dispose();
		}
		catch
		{
			// ignore
		}

		try
		{
			string sockPath = UnixSocketPath();
			if (File.Exists(sockPath))
				File.Delete(sockPath);
		}
		catch
		{
			// ignore
		}

		try
		{
			_lockFile?.Dispose();
		}
		catch
		{
			// ignore
		}

		try
		{
			_mutex?.Dispose();
		}
		catch
		{
			// ignore
		}

		_cts?.Dispose();
		_cts = null;
		_lockFile = null;
		_mutex = null;
	}

	private void BeginAcceptLoop()
	{
		if (!GodotObject.IsInstanceValid(this) || IsSecondary)
			return;

		_cts = new CancellationTokenSource();
		_ = Task.Run(() => AcceptLoopAsync(_cts.Token));
	}

	private async Task AcceptLoopAsync(CancellationToken token)
	{
		try
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
				await AcceptWindowsAsync(token).ConfigureAwait(false);
			else
				await AcceptUnixAsync(token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// shutdown
		}
		catch (ObjectDisposedException)
		{
			// shutdown
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SingleInstanceGuard:AcceptLoop - {ex.Message}");
		}
	}

	private void DispatchOpen(string path)
	{
		string copy = path ?? string.Empty;
		Callable.From(() =>
		{
			if (!GodotObject.IsInstanceValid(this))
				return;
			EmitSignal(SignalName.OpenShowRequested, copy);
		}).CallDeferred();
	}

	private static bool TryClaimWindows(string pendingShowPath)
	{
		_mutex = new System.Threading.Mutex(true, MutexName, out bool created);
		if (created)
			return true;

		IsSecondary = true;
		ForwardWindows(pendingShowPath);
		return false;
	}

	private static bool TryClaimUnix(string pendingShowPath)
	{
		string lockPath = ProjectSettings.GlobalizePath("user://instance.lock");
		string sockPath = UnixSocketPath();
		Directory.CreateDirectory(Path.GetDirectoryName(lockPath) ?? ".");

		try
		{
			_lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, FileShare.None);
		}
		catch (IOException)
		{
			IsSecondary = true;
			ForwardUnix(pendingShowPath);
			return false;
		}

		try
		{
			if (File.Exists(sockPath))
				File.Delete(sockPath);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SingleInstanceGuard:TryClaimUnix - stale socket: {ex.Message}");
		}

		return true;
	}

	private static void ForwardWindows(string path)
	{
		try
		{
			using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
			client.Connect(2000);
			WriteLine(client, path ?? string.Empty);
			ReadLine(client);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SingleInstanceGuard:ForwardWindows - {ex.Message}");
		}
	}

	private static void ForwardUnix(string path)
	{
		string sockPath = UnixSocketPath();
		try
		{
			using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
			socket.Connect(new UnixDomainSocketEndPoint(sockPath));
			using var ns = new NetworkStream(socket, ownsSocket: false);
			WriteLine(ns, path ?? string.Empty);
			ReadLine(ns);
		}
		catch (Exception ex)
		{
			GD.PrintErr($"SingleInstanceGuard:ForwardUnix - {ex.Message}");
		}
	}

	private async Task AcceptWindowsAsync(CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			NamedPipeServerStream server = null;
			try
			{
				server = new NamedPipeServerStream(
					PipeName,
					PipeDirection.InOut,
					1,
					PipeTransmissionMode.Byte,
					PipeOptions.Asynchronous);
				lock (_ioLock)
					_pipeServer = server;
				await server.WaitForConnectionAsync(token).ConfigureAwait(false);
				if (token.IsCancellationRequested)
					break;
				string path = ReadLine(server);
				WriteLine(server, "OK");
				DispatchOpen(path);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			catch (Exception ex)
			{
				if (token.IsCancellationRequested)
					break;
				GD.PrintErr($"SingleInstanceGuard:AcceptWindows - {ex.Message}");
				try
				{
					await Task.Delay(200, token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}
			}
			finally
			{
				lock (_ioLock)
				{
					if (ReferenceEquals(_pipeServer, server))
						_pipeServer = null;
				}

				try
				{
					server?.Dispose();
				}
				catch
				{
					// ignore
				}
			}
		}
	}

	private async Task AcceptUnixAsync(CancellationToken token)
	{
		string sockPath = UnixSocketPath();
		var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
		socket.Bind(new UnixDomainSocketEndPoint(sockPath));
		socket.Listen(2);
		lock (_ioLock)
			_listenSocket = socket;

		while (!token.IsCancellationRequested)
		{
			Socket client = null;
			try
			{
				client = await socket.AcceptAsync(token).ConfigureAwait(false);
				if (token.IsCancellationRequested)
					break;
				await using var ns = new NetworkStream(client, ownsSocket: false);
				string path = ReadLine(ns);
				WriteLine(ns, "OK");
				DispatchOpen(path);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			catch (Exception ex)
			{
				if (token.IsCancellationRequested)
					break;
				GD.PrintErr($"SingleInstanceGuard:AcceptUnix - {ex.Message}");
				try
				{
					await Task.Delay(200, token).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}
			}
			finally
			{
				try
				{
					client?.Dispose();
				}
				catch
				{
					// ignore
				}
			}
		}
	}

	private static string UnixSocketPath() =>
		ProjectSettings.GlobalizePath("user://instance.sock");

	private static string SanitizeToken(string name)
	{
		if (string.IsNullOrEmpty(name))
			return "user";
		var sb = new StringBuilder();
		foreach (char c in name)
		{
			if (char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
				sb.Append(c);
		}

		return sb.Length > 0 ? sb.ToString() : "user";
	}

	private static void WriteLine(Stream stream, string text)
	{
		string line = (text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ') + "\n";
		byte[] bytes = Encoding.UTF8.GetBytes(line);
		stream.Write(bytes, 0, bytes.Length);
		stream.Flush();
	}

	private static string ReadLine(Stream stream)
	{
		var sb = new StringBuilder();
		int b;
		while ((b = stream.ReadByte()) >= 0)
		{
			if (b == '\n')
				break;
			if (b == '\r')
				continue;
			sb.Append((char)b);
			if (sb.Length > 4096)
				break;
		}

		return sb.ToString().Trim();
	}
}
