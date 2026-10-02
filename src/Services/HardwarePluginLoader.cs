// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
/// <remarks>
/// Loading is skipped when <see cref="FeatureFlags.CueNetEnabled"/> is false (public Cue2 releases).
/// </remarks>
public partial class HardwarePluginLoader : Node
{
    /// <summary>
    /// Cue2-side snapshot of a discovered CueNet device. Host types stay in the plugin load context.
    /// </summary>
    public sealed class DeviceInfo
    {
        /// <summary>Stable device id from the HELLO beacon.</summary>
        public string Id { get; init; } = "";

        /// <summary>Product SKU token (for example <c>testpcb</c>).</summary>
        public string Sku { get; init; } = "";

        /// <summary>Firmware version string reported by the node.</summary>
        public string FirmwareVersion { get; init; } = "";

        /// <summary>IPv4/IPv6 address of the node.</summary>
        public string IpAddress { get; init; } = "";

        /// <summary>UDP discovery / DATA port.</summary>
        public int Port { get; init; }

        /// <summary>TCP control session port (HELLO <c>tcp=</c>).</summary>
        public int TcpPort { get; init; }

        /// <summary>Comma-separated HELLO <c>comps=</c> tokens.</summary>
        public string Components { get; init; } = "";

        /// <summary>Single-line summary for logs and the Settings list.</summary>
        public string Summary { get; init; } = "";
    }

    /// <summary>
    /// Cue2-side snapshot of a TCP control session. Host types stay in the plugin load context.
    /// </summary>
    public sealed class SessionInfo
    {
        public string DeviceId { get; init; } = "";
        public string IpAddress { get; init; } = "";
        public int TcpPort { get; init; }
        public string State { get; init; } = "";
        public bool IsConnected { get; init; }
        public string Error { get; init; } = "";
        public string Summary { get; init; } = "";
    }

    /// <summary>Cue2-side snapshot of a device EVENT (button, later encoder).</summary>
    public sealed class DeviceEventInfo
    {
        public string DeviceId { get; init; } = "";
        public string Component { get; init; } = "";
        public string Op { get; init; } = "";
        public string IpAddress { get; init; } = "";
        public string Summary { get; init; } = "";
        public IReadOnlyDictionary<string, string> Fields { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Result of <see cref="CommandAsync"/>.</summary>
    public sealed class CommandResult
    {
        public bool Ok { get; init; }
        public string Error { get; init; } = "";
        public string Raw { get; init; } = "";
        public IReadOnlyDictionary<string, string> Fields { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Raised on the main thread when the discovered-device set changes.
    /// </summary>
    [Signal]
    public delegate void DevicesChangedEventHandler();

    /// <summary>
    /// Raised on the main thread when a TCP session connects, drops, or is lost.
    /// </summary>
    [Signal]
    public delegate void ConnectionChangedEventHandler();

    /// <summary>
    /// Raised on the main thread when the device emits <c>CUENET/1 EVENT</c> (SW3 press, …).
    /// </summary>
    [Signal]
    public delegate void DeviceEventEventHandler();

    private GlobalSignals? _globalSignals;
    private AssemblyLoadContext? _loadContext;
    private object? _plugin;
    private MethodInfo? _startAsync;
    private MethodInfo? _stopAsync;
    private MethodInfo? _connectAsync;
    private MethodInfo? _disconnectAsync;
    private MethodInfo? _discoverNowAsync;
    private MethodInfo? _commandAsync;
    private MethodInfo? _sendDataAsync;
    private Type? _commandType;
    private CancellationTokenSource? _cts;
    private PropertyInfo? _devicesProperty;
    private PropertyInfo? _sessionsProperty;
    private DeviceEventInfo? _lastDeviceEvent;
    private bool _runtimeEnabled = true;
    private bool _hostStarted;
    private readonly Dictionary<string, HashSet<string>> _disabledComponents =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Showfile / history key for CueNet runtime enable and per-device component flags.</summary>
    public const string HistoryKey = "CueNet";

    /// <summary>
    /// Whether CueNet is compiled in for this build (<see cref="FeatureFlags.CueNetEnabled"/>).
    /// </summary>
    public bool IsEnabled => FeatureFlags.CueNetEnabled;

    /// <summary>
    /// Show-scoped master switch. When false, discovery and TCP sessions are stopped.
    /// </summary>
    public bool RuntimeEnabled => _runtimeEnabled;

    /// <summary>
    /// Whether the host plugin <c>StartAsync</c> is currently running.
    /// </summary>
    public bool IsHostRunning => _hostStarted;

    /// <summary>
    /// Whether the host plugin type was constructed.
    /// </summary>
    public bool IsPluginLoaded => _plugin is not null;

    /// <summary>
    /// Absolute path of the loaded <c>CueNet.Host.dll</c>, or empty when not loaded.
    /// </summary>
    public string HostPath { get; private set; } = "";

    /// <summary>
    /// Plugin <c>DisplayName</c>, or empty when not loaded.
    /// </summary>
    public string PluginDisplayName { get; private set; } = "";

    /// <summary>
    /// Plugin <c>Version</c>, or empty when not loaded.
    /// </summary>
    public string PluginVersion { get; private set; } = "";

    /// <summary>Most recent device EVENT (SW3 press, …).</summary>
    public DeviceEventInfo? LastDeviceEvent => _lastDeviceEvent;

    /// <inheritdoc />
    public override void _Ready()
    {
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        TreeExiting += OnTreeExiting;
        _ = InitializeAsync();
    }

    /// <summary>
    /// Snapshots devices currently known to the host plugin.
    /// </summary>
    /// <returns>A new list; empty when the plugin is not running.</returns>
    public IReadOnlyList<DeviceInfo> SnapshotDevices()
    {
        if (_plugin is null || _devicesProperty is null)
            return Array.Empty<DeviceInfo>();

        try
        {
            object? value = _devicesProperty.GetValue(_plugin);
            if (value is not System.Collections.IEnumerable enumerable)
                return Array.Empty<DeviceInfo>();

            var list = new List<DeviceInfo>();
            foreach (object? item in enumerable)
            {
                if (item is null)
                    continue;
                DeviceInfo? info = ReadDevice(item);
                if (info is not null)
                    list.Add(info);
            }

            return list;
        }
        catch (Exception ex)
        {
            Log($"HardwarePluginLoader:SnapshotDevices - {ex.GetType().Name}: {ex.Message}", 1);
            return Array.Empty<DeviceInfo>();
        }
    }

    /// <summary>
    /// Snapshots TCP control sessions. Watch <see cref="ConnectionChanged"/> for persistence.
    /// </summary>
    public IReadOnlyList<SessionInfo> SnapshotSessions()
    {
        if (_plugin is null || _sessionsProperty is null)
            return Array.Empty<SessionInfo>();

        try
        {
            object? value = _sessionsProperty.GetValue(_plugin);
            if (value is not System.Collections.IEnumerable enumerable)
                return Array.Empty<SessionInfo>();

            var list = new List<SessionInfo>();
            foreach (object? item in enumerable)
            {
                if (item is null)
                    continue;
                SessionInfo? info = ReadSession(item);
                if (info is not null)
                    list.Add(info);
            }

            return list;
        }
        catch (Exception ex)
        {
            Log($"HardwarePluginLoader:SnapshotSessions - {ex.GetType().Name}: {ex.Message}", 1);
            return Array.Empty<SessionInfo>();
        }
    }

    /// <summary>
    /// Open the TCP control session to a discovered device (by id or IP).
    /// </summary>
    public async Task<SessionInfo?> ConnectAsync(string deviceIdOrIp)
    {
        if (!_runtimeEnabled || _plugin is null || _connectAsync is null)
            return null;
        try
        {
            object? boxed = _connectAsync.Invoke(_plugin, new object[] { deviceIdOrIp, _cts?.Token ?? CancellationToken.None });
            if (boxed is not Task task)
                return null;
            await task.ConfigureAwait(true);
            object? session = task.GetType().GetProperty("Result")?.GetValue(task);
            return session is null ? null : ReadSession(session);
        }
        catch (Exception ex)
        {
            Log($"HardwarePluginLoader:ConnectAsync - {ex.GetType().Name}: {ex.Message}", 2);
            return null;
        }
    }

    /// <summary>
    /// Broadcast CueNet <c>DISCOVER</c> on every local IPv4 interface.
    /// </summary>
    public async Task DiscoverNowAsync()
    {
        if (!_runtimeEnabled || !_hostStarted || _plugin is null || _discoverNowAsync is null)
            return;
        try
        {
            object? boxed = _discoverNowAsync.Invoke(_plugin, Array.Empty<object>());
            if (boxed is Task task)
                await task.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log($"HardwarePluginLoader:DiscoverNowAsync - {ex.GetType().Name}: {ex.Message}", 2);
        }
    }

    /// <summary>
    /// Pings existing TCP sessions with core <c>list</c>. Does not broadcast DISCOVER.
    /// </summary>
    public async Task RefreshConnectionsAsync()
    {
        if (!_runtimeEnabled || !_hostStarted)
            return;
        foreach (SessionInfo session in SnapshotSessions())
        {
            if (string.IsNullOrEmpty(session.DeviceId))
                continue;
            CommandResult reply = await CommandAsync(session.DeviceId, "", "list");
            Log($"HardwarePluginLoader:RefreshConnections - {session.DeviceId} ok={reply.Ok} {reply.Error}",
                reply.Ok ? 0 : 1);
        }
    }

    /// <summary>
    /// Starts or stops CueNet discovery and sessions without unloading the host DLL.
    /// </summary>
    public async Task SetRuntimeEnabledAsync(bool enabled)
    {
        _runtimeEnabled = enabled;
        if (!FeatureFlags.CueNetEnabled)
            return;
        if (enabled)
            await StartHostAsync();
        else
            await StopHostAsync();
        CallDeferred(MethodName.NotifyDevicesChanged);
        CallDeferred(MethodName.NotifyConnectionChanged);
    }

    /// <summary>Whether this device component is enabled for Cue2 (default true).</summary>
    public bool IsComponentEnabled(string deviceId, string token)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(token))
            return true;
        return !_disabledComponents.TryGetValue(deviceId, out HashSet<string>? set)
               || !set.Contains(token);
    }

    /// <summary>Enable or disable a HELLO component token for a device.</summary>
    public void SetComponentEnabled(string deviceId, string token, bool enabled)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(token))
            return;
        if (!_disabledComponents.TryGetValue(deviceId, out HashSet<string>? set))
        {
            if (enabled)
                return;
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _disabledComponents[deviceId] = set;
        }

        if (enabled)
            set.Remove(token);
        else
            set.Add(token);
    }

    /// <summary>Showfile slice: enabled flag and disabled component tokens per device.</summary>
    public Godot.Collections.Dictionary GetData()
    {
        var dict = new Godot.Collections.Dictionary { ["Enabled"] = _runtimeEnabled };
        var comps = new Godot.Collections.Dictionary();
        foreach (KeyValuePair<string, HashSet<string>> pair in _disabledComponents)
        {
            if (pair.Value.Count == 0)
                continue;
            var arr = new Godot.Collections.Array();
            foreach (string token in pair.Value)
                arr.Add(token);
            comps[pair.Key] = arr;
        }

        dict["DisabledComponents"] = comps;
        return dict;
    }

    /// <summary>Applies a previously saved <see cref="GetData"/> slice.</summary>
    public void LoadFromData(Godot.Collections.Dictionary data)
    {
        if (data == null)
            return;
        _disabledComponents.Clear();
        if (data.TryGetValue("DisabledComponents", out Variant compsVar)
            && compsVar.VariantType == Variant.Type.Dictionary)
        {
            foreach (KeyValuePair<Variant, Variant> pair in compsVar.AsGodotDictionary())
            {
                string id = pair.Key.AsString();
                if (string.IsNullOrEmpty(id))
                    continue;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (pair.Value.VariantType == Variant.Type.Array)
                {
                    foreach (Variant item in pair.Value.AsGodotArray())
                    {
                        string token = item.AsString();
                        if (!string.IsNullOrEmpty(token))
                            set.Add(token);
                    }
                }

                if (set.Count > 0)
                    _disabledComponents[id] = set;
            }
        }

        bool enabled = !data.ContainsKey("Enabled") || data["Enabled"].AsBool();
        _ = SetRuntimeEnabledAsync(enabled);
        Log($"HardwarePluginLoader:LoadFromData - Enabled={enabled}", 0);
    }

    /// <summary>New Session: CueNet on, no disabled components.</summary>
    public void ResetToDefaults()
    {
        _disabledComponents.Clear();
        _ = SetRuntimeEnabledAsync(true);
        Log("HardwarePluginLoader:ResetToDefaults - CueNet enabled.", 0);
    }

    /// <summary>Close the TCP control session.</summary>
    public async Task DisconnectAsync(string deviceIdOrIp)
    {
        if (_plugin is null || _disconnectAsync is null)
            return;
        try
        {
            object? boxed = _disconnectAsync.Invoke(_plugin, new object[] { deviceIdOrIp });
            if (boxed is Task task)
                await task.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log($"HardwarePluginLoader:DisconnectAsync - {ex.GetType().Name}: {ex.Message}", 2);
        }
    }

    /// <summary>
    /// Reliable <c>CMD</c> to a device (TCP when connected, else UDP). See CueNet <c>docs/cue2-api.md</c>.
    /// </summary>
    public async Task<CommandResult> CommandAsync(
        string deviceIdOrIp,
        string component,
        string op,
        IReadOnlyDictionary<string, string>? fields = null)
    {
        if (!_runtimeEnabled || _plugin is null || _commandAsync is null || _commandType is null)
            return new CommandResult { Ok = false, Error = "no_plugin" };
        try
        {
            object cmd = Activator.CreateInstance(_commandType)!;
            _commandType.GetProperty("Component")?.SetValue(cmd, component ?? "");
            _commandType.GetProperty("Op")?.SetValue(cmd, op ?? "");
            var dict = fields is not null
                ? new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _commandType.GetProperty("Fields")?.SetValue(cmd, dict);

            object? boxed = _commandAsync.Invoke(_plugin, new object[] { deviceIdOrIp, cmd, _cts?.Token ?? CancellationToken.None });
            if (boxed is not Task task)
                return new CommandResult { Ok = false, Error = "bad_invoke" };
            await task.ConfigureAwait(true);
            object? reply = task.GetType().GetProperty("Result")?.GetValue(task);
            return ReadReply(reply);
        }
        catch (Exception ex)
        {
            Log($"HardwarePluginLoader:CommandAsync - {ex.GetType().Name}: {ex.Message}", 2);
            return new CommandResult { Ok = false, Error = ex.Message };
        }
    }

    /// <summary>UDP fire-and-forget <c>DATA</c>.</summary>
    public async Task SendDataAsync(
        string deviceIdOrIp,
        string component,
        IReadOnlyDictionary<string, string>? fields = null)
    {
        if (_plugin is null || _sendDataAsync is null)
            return;
        try
        {
            object? boxed = _sendDataAsync.Invoke(_plugin, new object[]
            {
                deviceIdOrIp,
                component,
                fields ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                _cts?.Token ?? CancellationToken.None
            });
            if (boxed is Task task)
                await task.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log($"HardwarePluginLoader:SendDataAsync - {ex.GetType().Name}: {ex.Message}", 2);
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (!FeatureFlags.CueNetEnabled)
            {
                Log("HardwarePluginLoader:_Ready - CueNet disabled (FeatureFlags.CueNetEnabled).", 0);
                return;
            }

            LoadPlugin();
            if (_runtimeEnabled)
                await StartHostAsync();
        }
        catch (Exception ex)
        {
            Log($"HardwarePluginLoader:_Ready - {ex.GetType().Name}: {ex.Message}", 2);
        }
    }

    private void LoadPlugin()
    {
        if (_plugin is not null)
            return;

        string? hostPath = FindHostAssembly();
        if (hostPath is null)
        {
            Log("HardwarePluginLoader:_Ready - CueNet.Host.dll not found (Cue2 runs without hardware).", 0);
            return;
        }

        HostPath = Path.GetFullPath(hostPath);
        string dir = Path.GetDirectoryName(HostPath)!;
        _loadContext = new PluginLoadContext(dir);
        Assembly assembly = _loadContext.LoadFromAssemblyPath(HostPath);
        Type? pluginType = FindPluginType(assembly);
        if (pluginType is null)
        {
            Log($"HardwarePluginLoader:_Ready - No [CueNetPlugin] type in {HostPath}", 2);
            return;
        }

        _plugin = Activator.CreateInstance(pluginType);
        if (_plugin is null)
        {
            Log("HardwarePluginLoader:_Ready - Failed to construct plugin type.", 2);
            return;
        }

        _devicesProperty = pluginType.GetProperty("Devices", BindingFlags.Instance | BindingFlags.Public);
        _sessionsProperty = pluginType.GetProperty("Sessions", BindingFlags.Instance | BindingFlags.Public);
        _connectAsync = pluginType.GetMethod("ConnectAsync", BindingFlags.Instance | BindingFlags.Public);
        _disconnectAsync = pluginType.GetMethod("DisconnectAsync", BindingFlags.Instance | BindingFlags.Public);
        _discoverNowAsync = pluginType.GetMethod("DiscoverNowAsync", BindingFlags.Instance | BindingFlags.Public);
        _commandAsync = pluginType.GetMethod("CommandAsync", BindingFlags.Instance | BindingFlags.Public);
        _sendDataAsync = pluginType.GetMethod("SendDataAsync", BindingFlags.Instance | BindingFlags.Public);
        _startAsync = pluginType.GetMethod("StartAsync", BindingFlags.Instance | BindingFlags.Public);
        _stopAsync = pluginType.GetMethod("StopAsync", BindingFlags.Instance | BindingFlags.Public);
        _commandType = FindContractsType(_loadContext, "CueNet.Contracts.CueNetCommand");
        PluginVersion = pluginType.GetProperty("Version")?.GetValue(_plugin) as string ?? "";
        PluginDisplayName = pluginType.GetProperty("DisplayName")?.GetValue(_plugin) as string ?? pluginType.Name;

        BindDeviceEvents(_plugin, pluginType);
        BindEvent(_plugin, pluginType, "ConnectionChanged", nameof(OnPluginConnectionChanged));
        BindEvent(_plugin, pluginType, "DeviceEvent", nameof(OnPluginDeviceEvent));
    }

    private async Task StartHostAsync()
    {
        if (_hostStarted)
            return;
        LoadPlugin();
        if (_plugin is null || _startAsync is null)
            return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        Action<string, int> log = Log;
        object? result = _startAsync.Invoke(_plugin, new object[] { log, _cts.Token });
        if (result is Task task)
            await task.ConfigureAwait(true);
        _hostStarted = true;
        Log($"HardwarePluginLoader:StartHost - Started {PluginDisplayName} {PluginVersion}", 0);
        NotifyDevicesChanged();
    }

    private async Task StopHostAsync()
    {
        if (!_hostStarted)
            return;
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
            Log($"HardwarePluginLoader:StopHost - {ex.GetType().Name}: {ex.Message}", 1);
        }
        finally
        {
            _hostStarted = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void BindDeviceEvents(object plugin, Type pluginType)
    {
        BindEvent(plugin, pluginType, "DeviceFound", nameof(OnPluginDeviceFound));
        BindEvent(plugin, pluginType, "DeviceLost", nameof(OnPluginDeviceLost));
    }

    private void BindEvent(object plugin, Type pluginType, string eventName, string methodName)
    {
        EventInfo? evt = pluginType.GetEvent(eventName);
        if (evt?.EventHandlerType is null)
            return;
        Delegate? handler = Delegate.CreateDelegate(
            evt.EventHandlerType, this, methodName, ignoreCase: false, throwOnBindFailure: false);
        if (handler is null)
            return;
        evt.AddEventHandler(plugin, handler);
    }

    private void OnPluginDeviceFound(object device)
    {
        LogDevice("DeviceFound", device);
        CallDeferred(MethodName.NotifyDevicesChanged);
    }

    private void OnPluginDeviceLost(object device)
    {
        LogDevice("DeviceLost", device);
        CallDeferred(MethodName.NotifyDevicesChanged);
    }

    private void OnPluginConnectionChanged(object session)
    {
        SessionInfo? info = ReadSession(session);
        Log($"HardwarePluginLoader:ConnectionChanged - {info?.Summary ?? session} state={info?.State}", 0);
        CallDeferred(MethodName.NotifyConnectionChanged);
    }

    private void OnPluginDeviceEvent(object evt)
    {
        DeviceEventInfo? info = ReadDeviceEvent(evt);
        _lastDeviceEvent = info;
        Log($"HardwarePluginLoader:DeviceEvent - {info?.Summary ?? evt.ToString()}", 0);
        CallDeferred(MethodName.NotifyDeviceEvent);
    }

    private void NotifyDeviceEvent()
    {
        if (!IsInsideTree())
            return;
        EmitSignal(SignalName.DeviceEvent);
    }

    private void NotifyDevicesChanged()
    {
        if (!IsInsideTree())
            return;
        EmitSignal(SignalName.DevicesChanged);
    }

    private void NotifyConnectionChanged()
    {
        if (!IsInsideTree())
            return;
        EmitSignal(SignalName.ConnectionChanged);
    }

    private static DeviceInfo? ReadDevice(object device)
    {
        Type t = device.GetType();
        string id = t.GetProperty("Id")?.GetValue(device) as string ?? "";
        string sku = t.GetProperty("Sku")?.GetValue(device) as string ?? "";
        string fw = t.GetProperty("FirmwareVersion")?.GetValue(device) as string ?? "";
        string ip = t.GetProperty("IpAddress")?.GetValue(device) as string ?? "";
        int port = 0;
        object? portObj = t.GetProperty("UdpPort")?.GetValue(device)
                          ?? t.GetProperty("Port")?.GetValue(device);
        if (portObj is int p)
            port = p;
        int tcpPort = 0;
        object? tcpObj = t.GetProperty("TcpPort")?.GetValue(device);
        if (tcpObj is int tp)
            tcpPort = tp;

        string comps = "";
        object? compsObj = t.GetProperty("Components")?.GetValue(device)
                           ?? t.GetProperty("Capabilities")?.GetValue(device);
        if (compsObj is System.Collections.IEnumerable enumerable)
        {
            comps = string.Join(',', enumerable.Cast<object>()
                .Select(x => x?.ToString())
                .Where(s => !string.IsNullOrEmpty(s)));
        }

        string summary = t.GetMethod("ToString")?.Invoke(device, null)?.ToString()
                         ?? $"{sku} {id} {ip}:{port} fw={fw} comps={comps}";

        return new DeviceInfo
        {
            Id = id,
            Sku = sku,
            FirmwareVersion = fw,
            IpAddress = ip,
            Port = port,
            TcpPort = tcpPort,
            Components = comps,
            Summary = summary,
        };
    }

    private static SessionInfo? ReadSession(object session)
    {
        Type t = session.GetType();
        string id = t.GetProperty("DeviceId")?.GetValue(session) as string ?? "";
        string ip = t.GetProperty("IpAddress")?.GetValue(session) as string ?? "";
        int tcp = 0;
        object? tcpObj = t.GetProperty("TcpPort")?.GetValue(session);
        if (tcpObj is int tp)
            tcp = tp;
        object? stateObj = t.GetProperty("State")?.GetValue(session);
        string state = stateObj?.ToString() ?? "";
        bool connected = t.GetProperty("IsConnected")?.GetValue(session) as bool? ?? false;
        string error = t.GetProperty("Error")?.GetValue(session) as string ?? "";
        string summary = t.GetMethod("ToString")?.Invoke(session, null)?.ToString()
                         ?? $"{id} {state} {ip}:{tcp}";
        return new SessionInfo
        {
            DeviceId = id,
            IpAddress = ip,
            TcpPort = tcp,
            State = state,
            IsConnected = connected,
            Error = error,
            Summary = summary,
        };
    }

    private void LogDevice(string tag, object device)
    {
        Type t = device.GetType();
        string summary = t.GetMethod("ToString")?.Invoke(device, null)?.ToString() ?? t.Name;
        object? comps = t.GetProperty("Components")?.GetValue(device)
                        ?? t.GetProperty("Capabilities")?.GetValue(device);
        string list = "";
        if (comps is System.Collections.IEnumerable enumerable)
        {
            list = string.Join(',', enumerable.Cast<object>()
                .Select(x => x?.ToString())
                .Where(s => !string.IsNullOrEmpty(s)));
        }

        Log($"HardwarePluginLoader:{tag} - {summary} comps={list}", 0);
    }

    private static CommandResult ReadReply(object? reply)
    {
        if (reply is null)
            return new CommandResult { Ok = false, Error = "empty_reply" };
        Type t = reply.GetType();
        bool ok = t.GetProperty("Ok")?.GetValue(reply) as bool? ?? false;
        string error = t.GetProperty("Error")?.GetValue(reply) as string ?? "";
        string raw = t.GetProperty("Raw")?.GetValue(reply) as string ?? "";
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (t.GetProperty("Fields")?.GetValue(reply) is System.Collections.IDictionary dict)
        {
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                string? key = entry.Key?.ToString();
                if (string.IsNullOrEmpty(key))
                    continue;
                fields[key] = entry.Value?.ToString() ?? "";
            }
        }

        return new CommandResult { Ok = ok, Error = error, Raw = raw, Fields = fields };
    }

    private static DeviceEventInfo? ReadDeviceEvent(object evt)
    {
        Type t = evt.GetType();
        string id = t.GetProperty("DeviceId")?.GetValue(evt) as string ?? "";
        string comp = t.GetProperty("Component")?.GetValue(evt) as string ?? "";
        string op = t.GetProperty("Op")?.GetValue(evt) as string ?? "";
        string ip = t.GetProperty("IpAddress")?.GetValue(evt) as string ?? "";
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (t.GetProperty("Fields")?.GetValue(evt) is System.Collections.IDictionary dict)
        {
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                string? key = entry.Key?.ToString();
                if (string.IsNullOrEmpty(key))
                    continue;
                fields[key] = entry.Value?.ToString() ?? "";
            }
        }

        string summary = t.GetMethod("ToString")?.Invoke(evt, null)?.ToString()
                         ?? $"{id} {comp} {op}";
        return new DeviceEventInfo
        {
            DeviceId = id,
            Component = comp,
            Op = op,
            IpAddress = ip,
            Fields = fields,
            Summary = summary,
        };
    }

    private static Type? FindContractsType(AssemblyLoadContext? ctx, string fullName)
    {
        if (ctx is null)
            return null;
        foreach (Assembly assembly in ctx.Assemblies)
        {
            Type? type = assembly.GetType(fullName);
            if (type is not null)
                return type;
        }

        try
        {
            return ctx.LoadFromAssemblyName(new AssemblyName("CueNet.Contracts")).GetType(fullName);
        }
        catch (Exception)
        {
            return null;
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
        var found = EnumerateSearchPaths()
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                System.Version version = new(0, 0, 0, 0);
                try
                {
                    string? raw = FileVersionInfo.GetVersionInfo(path).FileVersion;
                    if (!string.IsNullOrWhiteSpace(raw) && System.Version.TryParse(raw, out System.Version? parsed) && parsed is not null)
                        version = parsed;
                }
                catch (Exception)
                {
                    // Keep 0.0.0.0 and fall through to LastWriteTime.
                }

                return (
                    Path: path,
                    Version: version,
                    Written: File.GetLastWriteTimeUtc(path));
            })
            .OrderByDescending(x => x.Version)
            .ThenByDescending(x => x.Written)
            .ToList();

        return found.Count == 0 ? null : found[0].Path;
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
        // CueNet discovery/session callbacks run on thread-pool workers.
        CallDeferred(nameof(EmitLogOnMain), message, level);
    }

    private void EmitLogOnMain(string message, int level)
    {
        if (!GodotObject.IsInstanceValid(this) || _globalSignals is null)
            return;
        if (!GodotObject.IsInstanceValid(_globalSignals))
            return;
        _globalSignals.EmitSignal(nameof(GlobalSignals.Log), message, level);
    }

    private async void OnTreeExiting()
    {
        TreeExiting -= OnTreeExiting;
        try
        {
            await StopHostAsync();
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
