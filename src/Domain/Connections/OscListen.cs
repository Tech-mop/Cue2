// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Cue2.Domain.Cues;
using Cue2.Services;
using Godot;
using Godot.Collections;
using Rug.Osc;

namespace Cue2.Domain.Connections;

/// <summary>
/// Normalized OSC input message used for monitoring, capture, built-ins, and Input Map.
/// </summary>
public readonly struct OscInputMessage
{
    public string Address { get; init; }
    public string ArgsDisplay { get; init; }
    public int ArgCount { get; init; }
    public IReadOnlyList<object> Args { get; init; }
    public double? FirstFloat { get; init; }
    public double? SecondFloat { get; init; }
    /// <summary>Sender endpoint when available (for replies).</summary>
    public IPEndPoint Origin { get; init; }

    public bool IsValid => !string.IsNullOrEmpty(Address);

    public object ArgAt(int index)
    {
        if (Args == null || index < 0 || index >= Args.Count) return null;
        return Args[index];
    }
}

/// <summary>
/// Application-wide OSC receive service: UDP listen, built-in show control, Input Map,
/// per-cue OSC triggers, optional session prefix, and optional reply/feedback.
/// </summary>
public partial class OscListen : Node
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private InputActionsListener _inputActionsListener;

    private OscReceiver _receiver;
    private Thread _thread;
    private volatile bool _running;
    /// <summary>
    /// Default UDP/TCP listen port. 8000 is the most common OSC receive default
    /// (TouchOSC, many show-control apps). UDP and TCP may share the same port number.
    /// </summary>
    public const int DefaultListenPort = 8000;

    private int _port = DefaultListenPort;
    private string _sessionName = string.Empty;
    private bool _enabled;
    private bool _monitorEnabled = true;
    private bool _isCapturing;
    private bool _replyEnabled = true;
    private bool _pushFeedback;
    private bool _tcpEnabled;
    private int _tcpPort = DefaultListenPort;

    /// <summary>
    /// Last remote that sent us OSC (used for query replies when origin is known,
    /// and for push feedback). Not a configured "reply port" — OSC has no standard for that.
    /// </summary>
    private IPEndPoint _lastRemote;

    /// <summary>Allowed remote IPv4/IPv6 strings. Empty = allow all.</summary>
    private readonly List<string> _allowlist = new();
    private readonly object _allowlistLock = new();

    private TcpListener _tcpListener;
    private Thread _tcpAcceptThread;
    private readonly ConcurrentBag<TcpClient> _tcpClients = new();

    private readonly System.Collections.Generic.Dictionary<string, OscActionBinding> _inputMapBindings =
        new(StringComparer.Ordinal);

    private readonly ConcurrentQueue<OscInputMessage> _pendingMessages = new();
    private readonly ConcurrentQueue<string> _pendingLogLines = new();
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();

    private readonly object _replyLock = new();
    private OscSender _replySender;
    private string _replySenderKey = string.Empty;

    public const int MaxMonitorLines = 500;

    // ── Public state ────────────────────────────────────────────────────────

    public int Port
    {
        get => _port;
        set
        {
            int clamped = Math.Clamp(value, 1, 65535);
            if (_port == clamped) return;
            _port = clamped;
            if (_enabled) RestartReceiver();
            EmitSignal(SignalName.OscStateChanged);
        }
    }

    /// <summary>
    /// Optional OSC session name. When non-empty, <b>all</b> received paths must start with
    /// <c>/{SessionName}/…</c> (or equal <c>/{SessionName}</c>). The prefix is stripped before
    /// built-ins, cue triggers, and Input Map matching (so bindings stay as <c>/Go</c>, etc.).
    /// </summary>
    public string SessionName
    {
        get => _sessionName;
        set
        {
            string next = value ?? string.Empty;
            if (_sessionName == next) return;
            _sessionName = next;
            EmitSignal(SignalName.OscStateChanged);
        }
    }

    /// <summary>True when a session name is set and thus a prefix is required on all receive paths.</summary>
    public bool HasRequiredSessionPrefix => !string.IsNullOrEmpty(_sessionName);

    public bool OscListenEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (_enabled) StartListening();
            else StopListening();
            EmitSignal(SignalName.OscStateChanged);
        }
    }

    public bool MonitorEnabled
    {
        get => _monitorEnabled;
        set
        {
            if (_monitorEnabled == value) return;
            _monitorEnabled = value;
            EmitSignal(SignalName.OscStateChanged);
        }
    }

    /// <summary>When true, query commands send OSC replies.</summary>
    public bool ReplyEnabled
    {
        get => _replyEnabled;
        set
        {
            if (_replyEnabled == value) return;
            _replyEnabled = value;
            EmitSignal(SignalName.OscStateChanged);
        }
    }

    /// <summary>When true, push playhead/selection feedback on shell focus changes.</summary>
    public bool PushFeedback
    {
        get => _pushFeedback;
        set
        {
            if (_pushFeedback == value) return;
            _pushFeedback = value;
            EmitSignal(SignalName.OscStateChanged);
        }
    }

    /// <summary>When true, also accept OSC over TCP (binary framed) on <see cref="TcpPort"/>.</summary>
    public bool TcpEnabled
    {
        get => _tcpEnabled;
        set
        {
            if (_tcpEnabled == value) return;
            _tcpEnabled = value;
            if (_enabled)
                RestartReceiver();
            EmitSignal(SignalName.OscStateChanged);
        }
    }

    /// <summary>TCP listen port (may match UDP port; different transport).</summary>
    public int TcpPort
    {
        get => _tcpPort;
        set
        {
            int clamped = Math.Clamp(value, 1, 65535);
            if (_tcpPort == clamped) return;
            _tcpPort = clamped;
            if (_enabled && _tcpEnabled)
                RestartReceiver();
            EmitSignal(SignalName.OscStateChanged);
        }
    }

    /// <summary>
    /// Snapshot of the IP allowlist. Empty means all sources are accepted.
    /// Entries are normalized IP strings (no CIDR).
    /// </summary>
    public IReadOnlyList<string> AllowlistIps
    {
        get
        {
            lock (_allowlistLock)
                return _allowlist.ToList();
        }
    }

    /// <summary>Replaces the allowlist. Empty/null clears (allow all).</summary>
    public void SetAllowlist(IEnumerable<string> ips)
    {
        lock (_allowlistLock)
        {
            _allowlist.Clear();
            if (ips != null)
            {
                foreach (string raw in ips)
                {
                    if (TryNormalizeIp(raw, out string norm) && !_allowlist.Contains(norm, StringComparer.OrdinalIgnoreCase))
                        _allowlist.Add(norm);
                }
            }
        }
        EmitSignal(SignalName.OscStateChanged);
    }

    /// <summary>Adds one IP to the allowlist. Returns false if invalid or already present.</summary>
    public bool AddAllowlistIp(string ip)
    {
        if (!TryNormalizeIp(ip, out string norm)) return false;
        lock (_allowlistLock)
        {
            if (_allowlist.Contains(norm, StringComparer.OrdinalIgnoreCase)) return false;
            _allowlist.Add(norm);
        }
        EmitSignal(SignalName.OscStateChanged);
        return true;
    }

    /// <summary>Removes one IP from the allowlist.</summary>
    public bool RemoveAllowlistIp(string ip)
    {
        if (!TryNormalizeIp(ip, out string norm)) return false;
        bool removed;
        lock (_allowlistLock)
            removed = _allowlist.RemoveAll(x => string.Equals(x, norm, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
            EmitSignal(SignalName.OscStateChanged);
        return removed;
    }

    public bool IsListening => _running && (_receiver != null || _tcpListener != null);
    public bool IsCapturing => _isCapturing;

    [Signal] public delegate void OscStateChangedEventHandler();
    [Signal] public delegate void OscMonitorLineEventHandler(string line);
    [Signal] public delegate void OscCapturedEventHandler(string address, string argsDisplay);

    public override void _Ready()
    {
        SetProcess(false);
        if (SingleInstanceGuard.IsSecondary)
            return;

        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");
        _inputActionsListener = GetNodeOrNull<InputActionsListener>("/root/InputActionsListener");
        if (_globalSignals != null)
            _globalSignals.ShellFocused += OnShellFocusedFeedback;
        GD.Print("OscListen:_Ready - OSC listener ready.");
        if (_enabled)
            StartListening();
    }

    /// <summary>Wakes main-thread drain from the OSC receive thread.</summary>
    private void WakeMainThread()
    {
        CallDeferred(MethodName.EnableProcess);
    }

    /// <summary>Enables <c>_Process</c> on the main thread.</summary>
    private void EnableProcess()
    {
        if (GodotObject.IsInstanceValid(this))
            SetProcess(true);
    }

    public override void _Process(double delta)
    {
        int actions = 0;
        while (actions < 32 && _mainThreadActions.TryDequeue(out Action act))
        {
            try { act?.Invoke(); }
            catch (Exception ex) { GD.PrintErr($"OscListen:_Process - action: {ex.Message}"); }
            actions++;
        }

        int drained = 0;
        while (drained < 64 && _pendingMessages.TryDequeue(out OscInputMessage msg))
        {
            ProcessMainThreadMessage(msg);
            drained++;
        }

        while (drained < 80 && _pendingLogLines.TryDequeue(out string line))
        {
            EmitSignal(SignalName.OscMonitorLine, line);
            drained++;
        }

        if (_pendingMessages.IsEmpty && _pendingLogLines.IsEmpty && _mainThreadActions.IsEmpty)
            SetProcess(false);
    }

    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.ShellFocused -= OnShellFocusedFeedback;
        StopListening();
        CloseReplySender();
    }

    private void OnShellFocusedFeedback(int cueId)
    {
        if (!_pushFeedback || !_replyEnabled) return;
        // Push only if we know a remote (last sender) — no separate reply port.
        if (_lastRemote == null) return;
        try
        {
            var cue = CueList.FetchCueFromId(cueId);
            if (cue == null)
            {
                SendReply(_lastRemote, new OscMessage("/reply/playhead", -1, "", ""));
                return;
            }
            SendReply(_lastRemote, new OscMessage("/reply/playhead", cue.Id, cue.CueNum ?? "", cue.Name ?? ""));
        }
        catch (Exception ex)
        {
            GD.PrintErr($"OscListen:OnShellFocusedFeedback - {ex.Message}");
        }
    }

    public void StartCapture()
    {
        _isCapturing = true;
        EnqueueMonitorLine("— Capture armed: waiting for OSC…");
        EmitSignal(SignalName.OscStateChanged);
    }

    public void CancelCapture()
    {
        if (!_isCapturing) return;
        _isCapturing = false;
        EnqueueMonitorLine("— Capture cancelled");
        EmitSignal(SignalName.OscStateChanged);
    }

    public void ClearPendingMonitorLines()
    {
        while (_pendingLogLines.TryDequeue(out _)) { }
    }

    // ── Message processing ──────────────────────────────────────────────────

    private void ProcessMainThreadMessage(OscInputMessage msg)
    {
        if (!msg.IsValid) return;

        // Remember sender for replies / push (standard: answer the origin, not a fixed reply port).
        if (msg.Origin != null)
            _lastRemote = msg.Origin;

        // Session prefix handling
        if (!TryNormalizeAddress(msg, out string address, out string rejectReason))
        {
            if (!string.IsNullOrEmpty(rejectReason))
                LogBuiltIn(rejectReason, LogType.Warning);
            return;
        }

        // Rebuild message with normalized address for downstream handlers
        msg = new OscInputMessage
        {
            Address = address,
            ArgsDisplay = msg.ArgsDisplay,
            ArgCount = msg.ArgCount,
            Args = msg.Args,
            FirstFloat = msg.FirstFloat,
            SecondFloat = msg.SecondFloat,
            Origin = msg.Origin
        };

        if (_monitorEnabled)
            EmitSignal(SignalName.OscMonitorLine, FormatOscInputMessage(msg));

        if (_isCapturing)
        {
            _isCapturing = false;
            EmitSignal(SignalName.OscCaptured, msg.Address, msg.ArgsDisplay ?? string.Empty);
            EmitSignal(SignalName.OscStateChanged);
            return;
        }

        if (TryFireBuiltInCommands(msg))
            return;

        if (TryFireCueOscTriggers(msg))
            return;

        TryFireInputMapBindings(msg);
    }

    /// <summary>
    /// When <see cref="SessionName"/> is set, requires <c>/{SessionName}/…</c> and strips it.
    /// Applies to every received path (built-ins, Input Map, cue triggers).
    /// </summary>
    private bool TryNormalizeAddress(OscInputMessage msg, out string address, out string rejectReason)
    {
        address = msg.Address;
        rejectReason = null;
        if (string.IsNullOrEmpty(_sessionName))
            return true;

        string prefix = "/" + _sessionName;
        if (address.StartsWith(prefix + "/", StringComparison.Ordinal)
            || string.Equals(address, prefix, StringComparison.Ordinal))
        {
            address = address.Length == prefix.Length
                ? "/"
                : address.Substring(prefix.Length);
            if (string.IsNullOrEmpty(address) || address[0] != '/')
                address = "/" + address.TrimStart('/');
            return true;
        }

        rejectReason = $"Rejected (session prefix /{_sessionName}/ required): {msg.Address}";
        return false;
    }

    private void TryFireInputMapBindings(OscInputMessage msg)
    {
        _inputActionsListener ??= GetNodeOrNull<InputActionsListener>("/root/InputActionsListener");
        if (_inputActionsListener == null) return;

        foreach (string action in GlobalData.MappableInputActions)
        {
            var binding = GetInputMapBinding(action);
            if (binding == null || !binding.HasBinding) continue;
            if (!binding.Matches(msg.Address, msg.ArgsDisplay)) continue;

            if (_inputActionsListener.TryTriggerAction(action, ignoreFocusGate: true))
            {
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    $"OSC Input Map: {action} ← {binding.GetDisplay()}", (int)LogType.Info);
            }
        }
    }

    /// <summary>GO every armed cue whose per-cue OSC trigger matches the address.</summary>
    private bool TryFireCueOscTriggers(OscInputMessage msg)
    {
        if (CueList.CueIndex == null || CueList.CueIndex.Count == 0) return false;
        var executor = _globalData?.CueCommandExecutor;
        if (executor == null) return false;

        bool any = false;
        foreach (Cue cue in CueList.CueIndex.Values)
        {
            if (cue == null || !cue.CanFireOscTrigger) continue;
            if (!cue.OscTriggerMatches(msg.Address)) continue;

            GD.Print($"OscListen:TryFireCueOscTriggers - \"{cue.Name}\" ← {msg.Address}");
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"OSC cue trigger: \"{cue.Name}\" ← {msg.Address}", (int)LogType.Info);
            executor.ActivateSequenceFrom(cue);
            any = true;
        }
        return any;
    }

    // ── OSC Input Map ───────────────────────────────────────────────────────

    public OscActionBinding GetInputMapBinding(string actionName)
    {
        if (string.IsNullOrEmpty(actionName))
            return OscActionBinding.Unbound();
        if (_inputMapBindings.TryGetValue(actionName, out var b) && b != null)
            return b.Clone();
        return OscActionBinding.GetDefaultFor(actionName);
    }

    public bool IsInputMapBindingOverridden(string actionName) =>
        !string.IsNullOrEmpty(actionName) && _inputMapBindings.ContainsKey(actionName);

    public void SetInputMapBinding(string actionName, OscActionBinding binding)
    {
        if (string.IsNullOrEmpty(actionName)) return;
        var effective = binding?.Clone() ?? OscActionBinding.Unbound();
        var factory = OscActionBinding.GetDefaultFor(actionName);
        if (effective.EqualsBinding(factory))
            _inputMapBindings.Remove(actionName);
        else
            _inputMapBindings[actionName] = effective;
        EmitSignal(SignalName.OscStateChanged);
    }

    public void ResetInputMapBinding(string actionName)
    {
        if (string.IsNullOrEmpty(actionName)) return;
        if (!_inputMapBindings.Remove(actionName)) return;
        EmitSignal(SignalName.OscStateChanged);
    }

    public string FindConflictingInputMapAction(string excludeAction, OscActionBinding candidate)
    {
        if (candidate == null || !candidate.HasBinding) return null;
        foreach (string action in GlobalData.MappableInputActions)
        {
            if (string.Equals(action, excludeAction, StringComparison.Ordinal)) continue;
            var other = GetInputMapBinding(action);
            if (other == null || !other.HasBinding) continue;
            if (BindingsConflict(candidate, other))
                return action;
        }
        return null;
    }

    private static bool BindingsConflict(OscActionBinding a, OscActionBinding b)
    {
        if (!string.Equals(a.Address, b.Address, StringComparison.Ordinal)) return false;
        if (!a.MatchArgs || !b.MatchArgs) return true;
        return string.Equals(a.ArgsDisplay ?? string.Empty, b.ArgsDisplay ?? string.Empty, StringComparison.Ordinal);
    }

    public Dictionary GetInputMapBindingsData()
    {
        var dict = new Dictionary();
        foreach (var kvp in _inputMapBindings)
        {
            if (kvp.Value == null) continue;
            dict[kvp.Key] = kvp.Value.ToDict();
        }
        return dict;
    }

    public void LoadInputMapBindingsData(Dictionary data)
    {
        _inputMapBindings.Clear();
        if (data != null)
        {
            foreach (var key in data.Keys)
            {
                string action = key.AsString();
                if (string.IsNullOrEmpty(action)) continue;
                if (data[key].VariantType != Variant.Type.Dictionary) continue;
                _inputMapBindings[action] = OscActionBinding.FromDict(data[key].AsGodotDictionary());
            }
        }
        EmitSignal(SignalName.OscStateChanged);
    }

    // ── Replies ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends an OSC reply to the request origin (standard UDP behaviour).
    /// Falls back to the last known remote if <paramref name="origin"/> is null.
    /// There is no separate configured "reply port" — OSC does not define one.
    /// </summary>
    public void SendReply(IPEndPoint origin, OscMessage message)
    {
        if (!_replyEnabled || message == null) return;

        IPEndPoint target = origin ?? _lastRemote;
        if (target?.Address == null || target.Port <= 0)
        {
            if (_monitorEnabled)
                EnqueueMonitorLine($"— Reply skipped (no remote origin): {message.Address}");
            return;
        }

        try
        {
            IPAddress host = target.Address;
            int port = target.Port;
            string key = $"{host}:{port}";
            lock (_replyLock)
            {
                if (_replySender == null || _replySenderKey != key)
                {
                    CloseReplySenderUnlocked();
                    // Ephemeral local port — never bind the listen port for outbound replies.
                    _replySender = new OscSender(host, localPort: 0, port);
                    _replySender.Connect();
                    _replySenderKey = key;
                }
                _replySender.Send(message);
            }

            if (_monitorEnabled)
                EnqueueMonitorLine($"← reply {message.Address} {OscMessageUtil.FormatArgs(message)} → {host}:{port}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"OscListen:SendReply - {ex.Message}");
            EnqueueMonitorLine($"— Reply failed: {ex.Message}");
        }
    }

    private void CloseReplySender()
    {
        lock (_replyLock)
            CloseReplySenderUnlocked();
    }

    private void CloseReplySenderUnlocked()
    {
        try
        {
            _replySender?.Close();
            _replySender?.Dispose();
        }
        catch { /* ignore */ }
        _replySender = null;
        _replySenderKey = string.Empty;
    }

    // ── Allowlist ───────────────────────────────────────────────────────────

    private bool IsOriginAllowed(IPEndPoint origin)
    {
        lock (_allowlistLock)
        {
            if (_allowlist.Count == 0) return true; // empty = allow all
        }

        if (origin?.Address == null)
        {
            // Unknown origin with allowlist active — reject.
            return false;
        }

        // Always allow loopback when allowlist is active (local tools / same machine).
        if (IPAddress.IsLoopback(origin.Address))
            return true;

        string ip = origin.Address.ToString();
        // Map IPv4-mapped IPv6 if needed
        if (origin.Address.IsIPv4MappedToIPv6)
            ip = origin.Address.MapToIPv4().ToString();

        lock (_allowlistLock)
        {
            return _allowlist.Any(a => string.Equals(a, ip, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static bool TryNormalizeIp(string raw, out string normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string t = raw.Trim();
        // Strip optional port if user pasted host:port
        if (t.Contains(':') && !t.Contains("::") && t.Count(c => c == ':') == 1)
        {
            // IPv4:port
            t = t.Split(':')[0];
        }
        if (!IPAddress.TryParse(t, out var addr)) return false;
        if (addr.IsIPv4MappedToIPv6)
            addr = addr.MapToIPv4();
        normalized = addr.ToString();
        return true;
    }

    private void EnqueuePacket(OscPacket packet, IPEndPoint origin)
    {
        if (packet == null) return;
        if (!IsOriginAllowed(origin))
        {
            string src = origin?.Address?.ToString() ?? "?";
            EnqueueMonitorLine($"— Rejected (allowlist): {src}");
            return;
        }

        if (packet is OscMessage oscMessage)
            _pendingMessages.Enqueue(FromOscMessage(oscMessage, origin));
        else if (packet is OscBundle bundle)
        {
            foreach (var nested in bundle)
            {
                if (nested is OscMessage nestedMsg)
                    _pendingMessages.Enqueue(FromOscMessage(nestedMsg, origin));
            }
        }
        WakeMainThread();
    }

    // ── Receiver ────────────────────────────────────────────────────────────

    private void StartListening()
    {
        if (_running) return;

        bool udpOk = false;
        try
        {
            _receiver = new OscReceiver(_port);
            _receiver.Connect();
            GD.Print($"OscListen:StartListening - UDP on port {_port}");
            EnqueueMonitorLine($"— Listening UDP port {_port}");
            udpOk = true;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"OscListen:StartListening - UDP bind failed: {ex.Message}");
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"OSC Listener: UDP bind {_port} failed: {ex.Message}", (int)LogType.Error);
            _receiver = null;
        }

        bool tcpOk = false;
        if (_tcpEnabled)
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, _tcpPort);
                _tcpListener.Start();
                GD.Print($"OscListen:StartListening - TCP on port {_tcpPort}");
                EnqueueMonitorLine($"— Listening TCP port {_tcpPort} (binary OSC)");
                tcpOk = true;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"OscListen:StartListening - TCP bind failed: {ex.Message}");
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    $"OSC Listener: TCP bind {_tcpPort} failed: {ex.Message}", (int)LogType.Error);
                _tcpListener = null;
            }
        }

        if (!udpOk && !tcpOk)
        {
            _enabled = false;
            return;
        }

        _running = true;

        if (udpOk)
        {
            _thread = new Thread(ReceiveLoopUdp)
            {
                IsBackground = true,
                Name = "Cue2-OscListen-UDP"
            };
            _thread.Start();
        }

        if (tcpOk)
        {
            _tcpAcceptThread = new Thread(TcpAcceptLoop)
            {
                IsBackground = true,
                Name = "Cue2-OscListen-TCP"
            };
            _tcpAcceptThread.Start();
        }
    }

    private void StopListening()
    {
        if (!_running && _receiver == null && _tcpListener == null) return;
        _running = false;

        try { _receiver?.Close(); }
        catch (Exception ex) { GD.PrintErr($"OscListen:StopListening - UDP Close: {ex.Message}"); }

        try { _tcpListener?.Stop(); }
        catch (Exception ex) { GD.PrintErr($"OscListen:StopListening - TCP Stop: {ex.Message}"); }

        foreach (var client in _tcpClients)
        {
            try { client.Close(); } catch { /* ignore */ }
        }
        while (_tcpClients.TryTake(out _)) { }

        try { _thread?.Join(1500); }
        catch (Exception ex) { GD.PrintErr($"OscListen:StopListening - UDP Join: {ex.Message}"); }
        try { _tcpAcceptThread?.Join(1500); }
        catch (Exception ex) { GD.PrintErr($"OscListen:StopListening - TCP Join: {ex.Message}"); }

        _receiver = null;
        _thread = null;
        _tcpListener = null;
        _tcpAcceptThread = null;
        EnqueueMonitorLine("— Listener stopped");
    }

    private void RestartReceiver()
    {
        StopListening();
        if (_enabled) StartListening();
    }

    private void ReceiveLoopUdp()
    {
        while (_running)
        {
            try
            {
                var receiver = _receiver;
                if (receiver == null) break;
                OscPacket packet = receiver.Receive();
                if (!_running) break;
                if (packet == null) continue;

                if (packet.Error != OscPacketError.None)
                {
                    EnqueueMonitorLine($"— UDP parse error: {packet.ErrorMessage}");
                    continue;
                }

                IPEndPoint origin = null;
                try { origin = packet.Origin; }
                catch { /* older Rug.Osc */ }

                EnqueuePacket(packet, origin);
            }
            catch (Exception ex)
            {
                if (!_running) break;

                // Rug.Osc throws on Close() — expected shutdown path.
                string msg = ex.Message ?? string.Empty;
                if (msg.IndexOf("disconnected", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("closed", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("SocketIsClosed", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    break;
                }

                // Transient wait/parse errors must not kill the UDP listener for the session.
                GD.PrintErr($"OscListen:ReceiveLoopUdp - {ex.Message}");
                EnqueueMonitorLine($"— UDP receive error: {ex.Message}");
                try { Thread.Sleep(25); } catch { /* ignore */ }
            }
        }
    }

    private void TcpAcceptLoop()
    {
        while (_running)
        {
            try
            {
                var listener = _tcpListener;
                if (listener == null) break;
                TcpClient client = listener.AcceptTcpClient();
                if (!_running)
                {
                    try { client.Close(); } catch { /* ignore */ }
                    break;
                }

                IPEndPoint remote = null;
                try { remote = client.Client?.RemoteEndPoint as IPEndPoint; }
                catch { /* ignore */ }

                if (!IsOriginAllowed(remote))
                {
                    EnqueueMonitorLine($"— TCP rejected (allowlist): {remote?.Address}");
                    try { client.Close(); } catch { /* ignore */ }
                    continue;
                }

                _tcpClients.Add(client);
                EnqueueMonitorLine($"— TCP client connected: {remote?.Address}:{remote?.Port}");

                var thread = new Thread(() =>
                {
                    try
                    {
                        OscTcpTransport.ReceiveLoop(client, () => _running, (packet, origin) =>
                        {
                            EnqueuePacket(packet, origin ?? remote);
                        });
                    }
                    finally
                    {
                        EnqueueMonitorLine($"— TCP client disconnected: {remote?.Address}");
                    }
                })
                {
                    IsBackground = true,
                    Name = "Cue2-OscListen-TCP-Client"
                };
                thread.Start();
            }
            catch (SocketException)
            {
                if (!_running) break;
            }
            catch (Exception ex)
            {
                if (!_running) break;
                GD.PrintErr($"OscListen:TcpAcceptLoop - {ex.Message}");
                EnqueueMonitorLine($"— TCP accept error: {ex.Message}");
                break;
            }
        }
    }

    private void EnqueueMonitorLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        _pendingLogLines.Enqueue($"{DateTime.Now:HH:mm:ss.fff}  {line}");
        WakeMainThread();
    }

    private static string FormatOscInputMessage(OscInputMessage msg)
    {
        var sb = new StringBuilder(96);
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"));
        sb.Append("  ");
        sb.Append(msg.Address);
        if (!string.IsNullOrEmpty(msg.ArgsDisplay))
        {
            sb.Append("  ");
            sb.Append(msg.ArgsDisplay);
        }
        return sb.ToString();
    }

    private static OscInputMessage FromOscMessage(OscMessage message, IPEndPoint origin)
    {
        var args = new List<object>();
        if (message != null)
        {
            for (int i = 0; i < message.Count; i++)
                args.Add(message[i]);
        }

        double? f0 = null, f1 = null;
        if (OscMessageUtil.TryGetFloat(args, 0, out double a0)) f0 = a0;
        if (OscMessageUtil.TryGetFloat(args, 1, out double a1)) f1 = a1;

        return new OscInputMessage
        {
            Address = message?.Address ?? string.Empty,
            ArgsDisplay = OscMessageUtil.FormatArgs(args),
            ArgCount = args.Count,
            Args = args,
            FirstFloat = f0,
            SecondFloat = f1,
            Origin = origin
        };
    }

    /// <summary>Formats OSC message arguments for monitor / binding display.</summary>
    public static string FormatArgs(OscMessage message) => OscMessageUtil.FormatArgs(message);

    // ── Serialization ───────────────────────────────────────────────────────

    public Dictionary GetData()
    {
        var d = new Dictionary();
        d["OscListenEnabled"] = _enabled;
        d["Port"] = _port;
        d["SessionName"] = _sessionName ?? string.Empty;
        d["MonitorEnabled"] = _monitorEnabled;
        d["ReplyEnabled"] = _replyEnabled;
        d["PushFeedback"] = _pushFeedback;
        d["TcpEnabled"] = _tcpEnabled;
        d["TcpPort"] = _tcpPort;
        lock (_allowlistLock)
        {
            var arr = new Godot.Collections.Array();
            foreach (string ip in _allowlist)
                arr.Add(ip);
            d["Allowlist"] = arr;
        }
        return d;
    }

    public void LoadFromData(Dictionary data)
    {
        if (data == null) return;

        if (data.TryGetValue("Port", out var portVar))
            _port = Math.Clamp(portVar.AsInt32(), 1, 65535);
        if (data.TryGetValue("SessionName", out var sessionVar))
            _sessionName = sessionVar.AsString() ?? string.Empty;
        if (data.TryGetValue("MonitorEnabled", out var monVar))
            _monitorEnabled = monVar.AsBool();
        // Legacy RequireSessionPrefix ignored — non-empty SessionName always requires prefix.
        if (data.TryGetValue("ReplyEnabled", out var repVar))
            _replyEnabled = repVar.AsBool();
        if (data.TryGetValue("PushFeedback", out var pushVar))
            _pushFeedback = pushVar.AsBool();
        // Legacy ReplyHost / ReplyPort ignored — replies use message origin.
        if (data.TryGetValue("TcpEnabled", out var tcpEnVar))
            _tcpEnabled = tcpEnVar.AsBool();
        if (data.TryGetValue("TcpPort", out var tcpPortVar))
            _tcpPort = Math.Clamp(tcpPortVar.AsInt32(), 1, 65535);
        else
            _tcpPort = _port;

        if (data.TryGetValue("Allowlist", out var allowVar) && allowVar.VariantType == Variant.Type.Array)
        {
            var list = new List<string>();
            foreach (var item in allowVar.AsGodotArray())
            {
                string s = item.AsString();
                if (TryNormalizeIp(s, out string norm))
                    list.Add(norm);
            }
            lock (_allowlistLock)
            {
                _allowlist.Clear();
                _allowlist.AddRange(list.Distinct(StringComparer.OrdinalIgnoreCase));
            }
        }

        bool wantEnabled = data.TryGetValue("OscListenEnabled", out var enVar) && enVar.AsBool();
        if (wantEnabled != _enabled || (wantEnabled && !_running))
        {
            _enabled = wantEnabled;
            if (_enabled) { StopListening(); StartListening(); }
            else StopListening();
        }

        EmitSignal(SignalName.OscStateChanged);
    }

    public void ResetToDefaults()
    {
        _isCapturing = false;
        _enabled = false;
        StopListening();
        _port = DefaultListenPort;
        _sessionName = string.Empty;
        _monitorEnabled = true;
        _replyEnabled = true;
        _pushFeedback = false;
        _lastRemote = null;
        _tcpEnabled = false;
        _tcpPort = DefaultListenPort;
        lock (_allowlistLock)
            _allowlist.Clear();
        _inputMapBindings.Clear();
        CloseReplySender();
        EmitSignal(SignalName.OscStateChanged);
    }

    public void SetEnabled(bool enabled) => OscListenEnabled = enabled;
    public void SetPort(int port) => Port = port;

    // LogBuiltIn used by partial BuiltInCommands
    private void LogBuiltIn(string message, LogType type)
    {
        GD.Print($"OscListen:BuiltIn - {message}");
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), $"OSC {message}", (int)type);
        if (_monitorEnabled)
            EnqueueMonitorLine($"— {message}");
    }
}
