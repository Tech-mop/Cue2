// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Loads <c>CueNet.Host.dll</c> (and future hardware plugins) without compiling them into Cue2.
/// Search paths and the reflected entry point are documented in CueNet <c>docs/plugin-loading.md</c>.
/// </summary>
public partial class HardwarePluginLoader : Node
{
    private GlobalSignals? _globalSignals;
    private AssemblyLoadContext? _loadContext;
    private object? _plugin;
    private MethodInfo? _stopAsync;
    private CancellationTokenSource? _cts;

    public bool IsPluginLoaded => _plugin is not null;

    public override void _Ready()
    {
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        TreeExiting += OnTreeExiting;
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            string? hostPath = FindHostAssembly();
            if (hostPath is null)
            {
                Log("HardwarePluginLoader:_Ready - CueNet.Host.dll not found (Cue2 runs without hardware).", 0);
                return;
            }

            string dir = Path.GetDirectoryName(hostPath)!;
            _loadContext = new PluginLoadContext(dir);
            Assembly assembly = _loadContext.LoadFromAssemblyPath(hostPath);
            Type? pluginType = FindPluginType(assembly);
            if (pluginType is null)
            {
                Log($"HardwarePluginLoader:_Ready - No [CueNetPlugin] type in {hostPath}", 2);
                return;
            }

            _plugin = Activator.CreateInstance(pluginType);
            if (_plugin is null)
            {
                Log("HardwarePluginLoader:_Ready - Failed to construct plugin type.", 2);
                return;
            }

            MethodInfo? start = pluginType.GetMethod("StartAsync", BindingFlags.Instance | BindingFlags.Public);
            _stopAsync = pluginType.GetMethod("StopAsync", BindingFlags.Instance | BindingFlags.Public);
            if (start is null)
            {
                Log("HardwarePluginLoader:_Ready - Plugin missing StartAsync.", 2);
                return;
            }

            _cts = new CancellationTokenSource();
            Action<string, int> log = Log;
            object? result = start.Invoke(_plugin, new object[] { log, _cts.Token });
            if (result is Task task)
                await task.ConfigureAwait(true);

            string version = pluginType.GetProperty("Version")?.GetValue(_plugin) as string ?? "?";
            string name = pluginType.GetProperty("DisplayName")?.GetValue(_plugin) as string ?? pluginType.Name;
            Log($"HardwarePluginLoader:_Ready - Started {name} {version} from {hostPath}", 0);
        }
        catch (Exception ex)
        {
            Log($"HardwarePluginLoader:_Ready - {ex.GetType().Name}: {ex.Message}", 2);
        }
    }

    private static Type? FindPluginType(Assembly assembly)
    {
        foreach (Type type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null)
                continue;
            bool marked = type.GetCustomAttributes(inherit: false)
                .Any(a => a.GetType().Name == "CueNetPluginAttribute");
            if (marked)
                return type;
        }

        return assembly.GetType("CueNet.Host.CueNetPlugin");
    }

    private static string? FindHostAssembly()
    {
        foreach (string candidate in EnumerateSearchPaths())
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static IEnumerable<string> EnumerateSearchPaths()
    {
        string exeDir = AppContext.BaseDirectory;
        yield return Path.Combine(exeDir, "plugins", "cuenet", "CueNet.Host.dll");

        string? userDir = TryGlobalize("user://");
        if (userDir is not null)
            yield return Path.Combine(userDir, "plugins", "cuenet", "CueNet.Host.dll");

        // Godot editor: Cue2/ project folder (not the hidden .godot build dir).
        string? projectRes = TryGlobalize("res://");
        if (projectRes is not null)
            yield return Path.Combine(projectRes, "plugins", "cuenet", "CueNet.Host.dll");

        // Sibling CueNet tree (Cue2_Home/CueNet).
        string? walk = exeDir;
        for (int i = 0; i < 8 && walk is not null; i++)
        {
            string sibling = Path.Combine(walk, "CueNet", "src", "CueNet.Host", "bin", "Debug", "net8.0", "CueNet.Host.dll");
            yield return sibling;
            string siblingRelease = Path.Combine(walk, "CueNet", "src", "CueNet.Host", "bin", "Release", "net8.0", "CueNet.Host.dll");
            yield return siblingRelease;
            DirectoryInfo? parent = Directory.GetParent(walk);
            walk = parent?.FullName;
        }
    }

    private static string? TryGlobalize(string godotPath)
    {
        try
        {
            return ProjectSettings.GlobalizePath(godotPath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Log(string message, int level)
    {
        GD.Print(message);
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), message, level);
    }

    private async void OnTreeExiting()
    {
        TreeExiting -= OnTreeExiting;
        try
        {
            _cts?.Cancel();
            if (_plugin is not null && _stopAsync is not null)
            {
                object? result = _stopAsync.Invoke(_plugin, Array.Empty<object>());
                if (result is Task task)
                    await task.ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"HardwarePluginLoader:OnTreeExiting - {ex.Message}");
        }
        finally
        {
            _plugin = null;
            _loadContext?.Unload();
            _loadContext = null;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Loads Host from a folder; Contracts in that folder win, everything else defers to Cue2's default context.
    /// </summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginDirectory)
            : base(isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(Path.Combine(pluginDirectory, "CueNet.Host.dll"));
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            string? path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is not null)
                return LoadFromAssemblyPath(path);
            return null;
        }
    }
}
