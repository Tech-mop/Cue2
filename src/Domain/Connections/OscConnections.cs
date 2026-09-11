// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cue2.Services;
using Godot;
using Godot.Collections;
using Rug.Osc;

namespace Cue2.Domain.Connections;

/// <summary>
/// Manages named OSC send connections and transmits OSC messages for cue components.
/// Also exposes a send-side monitor log (mirrors MIDI monitor patterns).
/// </summary>
public partial class OscConnections : Node
{
    private GlobalSignals _globalSignals;

    /// <summary>Session OSC send connections (order preserved).</summary>
    public static System.Collections.Generic.List<CueOscConnection> Connections { get; set; } = new();

    private bool _monitorEnabled = true;
    private readonly ConcurrentQueue<string> _pendingLogLines = new();
    /// <summary>Work from background threads (TCP connect) — drained on the Godot main thread only.</summary>
    private readonly ConcurrentQueue<Action> _mainThreadActions = new();

    /// <summary>Maximum lines retained in the in-memory monitor buffer.</summary>
    public const int MaxMonitorLines = 500;

    /// <summary>
    /// Common default destination port for outbound OSC (TouchOSC / many show tools use 8000).
    /// </summary>
    public const int DefaultSendPort = 8000;

    /// <summary>When true, sent OSC messages are queued for the monitor log UI.</summary>
    public bool MonitorEnabled
    {
        get => _monitorEnabled;
        set
        {
            if (_monitorEnabled == value) return;
            _monitorEnabled = value;
            EmitSignal(SignalName.OscConnectionsStateChanged);
        }
    }

    /// <summary>Fired when connections are added/removed/reloaded or monitor toggles.</summary>
    [Signal]
    public delegate void OscConnectionsStateChangedEventHandler();

    /// <summary>
    /// Fired on the main thread for each send monitor line (timestamped summary).
    /// </summary>
    [Signal]
    public delegate void OscSendMonitorLineEventHandler(string line);

    public override void _Ready()
    {
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        GD.Print("OscConnections:_Ready - OSC send connections ready.");
        SetProcess(false);
    }

    /// <summary>Wakes main-thread drain from TCP connect workers.</summary>
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
        // Background TCP connect status / logs must only touch Godot nodes here.
        int actions = 0;
        while (actions < 32 && _mainThreadActions.TryDequeue(out Action action))
        {
            try { action?.Invoke(); }
            catch (Exception ex) { GD.PrintErr($"OscConnections:_Process - deferred: {ex.Message}"); }
            actions++;
        }

        int drained = 0;
        while (drained < 80 && _pendingLogLines.TryDequeue(out string line))
        {
            EmitSignal(SignalName.OscSendMonitorLine, line);
            drained++;
        }

        if (_pendingLogLines.IsEmpty && _mainThreadActions.IsEmpty)
            SetProcess(false);
    }

    /// <summary>
    /// Queues work onto the Godot main thread (safe to call from TCP connect workers).
    /// </summary>
    public void RunOnMainThread(Action action)
    {
        if (action == null) return;
        _mainThreadActions.Enqueue(action);
        WakeMainThread();
    }

    public static CueOscConnection GetCueOscConnection(int id) =>
        Connections.Find(c => c.Id == id);

    public override void _ExitTree()
    {
        foreach (var connection in Connections.ToList())
        {
            try
            {
                connection.CloseConnection();
                if (GodotObject.IsInstanceValid(connection))
                    connection.Free();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"OscConnections:_ExitTree - {ex.Message}");
            }
        }
        Connections.Clear();
    }

    /// <summary>
    /// Creates a new OSC send connection, opens its sender, and notifies UI.
    /// </summary>
    public static CueOscConnection CreateConnection(
        string name = "Osc",
        IPAddress address = null,
        int port = DefaultSendPort,
        string networkInterface = "")
    {
        var connection = new CueOscConnection
        {
            Id = CueOscConnection._nextId++,
            Name = name ?? $"OSC {CueOscConnection._nextId}",
            Address = address ?? IPAddress.Loopback,
            Port = port,
            NetworkInterface = networkInterface
        };
        Connections.Add(connection);
        connection.InitialiseSender();
        GD.Print($"OscConnections:CreateConnection - '{connection.Name}' → {connection.Address}:{connection.Port}");

        var node = Engine.GetMainLoop() is SceneTree tree
            ? tree.Root.GetNodeOrNull<OscConnections>("/root/OscConnections")
            : null;
        node?.NotifyConnectionsChanged($"— Connection created: {connection.Name} → {connection.Address}:{connection.Port}");
        return connection;
    }

    /// <summary>
    /// Removes and disposes a connection by id.
    /// </summary>
    public static bool DeleteConnection(int id)
    {
        var connection = Connections.Find(c => c.Id == id);
        if (connection == null)
        {
            GD.Print($"OscConnections:DeleteConnection - ID {id} not found");
            return false;
        }

        string label = connection.Name;
        Connections.Remove(connection);
        connection.CloseConnection();
        if (GodotObject.IsInstanceValid(connection))
            connection.Free();
        GD.Print($"OscConnections:DeleteConnection - '{label}' (ID: {id})");

        var node = Engine.GetMainLoop() is SceneTree tree
            ? tree.Root.GetNodeOrNull<OscConnections>("/root/OscConnections")
            : null;
        node?.NotifyConnectionsChanged($"— Connection deleted: {label}");
        return true;
    }

    /// <summary>Notifies listeners that the connection list changed (main thread).</summary>
    public void NotifyConnectionsChanged(string monitorNotice = null)
    {
        if (!string.IsNullOrEmpty(monitorNotice))
            EnqueueMonitorLine(monitorNotice);
        EmitSignal(SignalName.OscConnectionsStateChanged);
    }

    /// <summary>
    /// Called by <see cref="CueOscConnection"/> after a successful send for monitor logging.
    /// </summary>
    public void NotifyMessageSent(CueOscConnection connection, OscMessage message)
    {
        // May be called from any thread after a send — marshal Godot work.
        RunOnMainThread(() => NotifyMessageSentMain(connection, message));
    }

    private void NotifyMessageSentMain(CueOscConnection connection, OscMessage message)
    {
        if (!_monitorEnabled || connection == null || message == null) return;
        string transport = connection.Transport == OscTransport.Tcp ? "TCP" : "UDP";
        string args = OscListen.FormatArgs(message);
        string argPart = string.IsNullOrEmpty(args) ? "(no args)" : args;
        string dest = $"{connection.Address}:{connection.Port}";
        string name = connection.Name ?? "OSC";
        EnqueueMonitorLine(
            $"OK   [{name}] {transport} → {dest}  {message.Address}  {argPart}");
    }

    /// <summary>
    /// Called by <see cref="CueOscConnection"/> when a send fails.
    /// </summary>
    public void NotifySendError(CueOscConnection connection, string error)
    {
        RunOnMainThread(() => NotifySendErrorMain(connection, error));
    }

    private void NotifySendErrorMain(CueOscConnection connection, string error)
    {
        string name = connection?.Name ?? "?";
        string transport = connection?.Transport == OscTransport.Tcp ? "TCP" : "UDP";
        string dest = connection != null ? $"{connection.Address}:{connection.Port}" : "?";
        EnqueueMonitorLine($"FAIL  [{name}] {transport} → {dest}  {error}");
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            $"OSC send failed [{name}] {transport} → {dest}: {error}",
            (int)LogType.Warning);
        // TCP send failures close the socket — refresh per-row status indicators.
        EmitSignal(SignalName.OscConnectionsStateChanged);
    }

    /// <summary>
    /// Connection open / close / transport status for the send monitor and event log.
    /// Safe to call from background threads (marshalled to main).
    /// </summary>
    /// <param name="phase">connecting | ok | fail</param>
    public void NotifyConnectionStatus(CueOscConnection connection, string detail, string phase = "ok")
    {
        // Capture plain values — connection fields are fine to read from worker; Godot APIs are not.
        string name = connection?.Name ?? "?";
        string transport = connection?.Transport == OscTransport.Tcp ? "TCP" : "UDP";
        string dest = connection != null ? $"{connection.Address}:{connection.Port}" : "?";
        string detailCopy = detail ?? string.Empty;
        string phaseCopy = phase ?? "ok";

        RunOnMainThread(() => NotifyConnectionStatusMain(name, transport, dest, detailCopy, phaseCopy));
    }

    private void NotifyConnectionStatusMain(
        string name, string transport, string dest, string detail, string phase)
    {
        string monitor;
        string logMsg;
        int logType;
        switch (phase)
        {
            case "connecting":
                monitor = $"…  [{name}] {transport} connecting → {dest}  {detail}";
                logMsg = $"OSC [{name}] {transport} connecting → {dest}…";
                logType = (int)LogType.Info;
                break;
            case "fail":
                monitor = $"FAIL  [{name}] {transport} → {dest}  {detail}";
                logMsg = $"OSC [{name}] {transport} connect failed → {dest}: {detail}";
                logType = (int)LogType.Warning;
                break;
            default:
                monitor = $"OK   [{name}] {transport} → {dest}  {detail}";
                logMsg = $"OSC [{name}] {transport} ready → {dest}";
                logType = (int)LogType.Info;
                break;
        }

        EnqueueMonitorLine(monitor);
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), logMsg, logType);
        EmitSignal(SignalName.OscConnectionsStateChanged);
    }

    /// <summary>Clears any pending monitor log lines that have not yet been emitted.</summary>
    public void ClearPendingMonitorLines()
    {
        while (_pendingLogLines.TryDequeue(out _)) { }
    }

    private void EnqueueMonitorLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        string stamped = $"{DateTime.Now:HH:mm:ss.fff}  {line}";
        _pendingLogLines.Enqueue(stamped);
        WakeMainThread();
    }

    public Dictionary GetData()
    {
        var dict = new Dictionary();
        var connectionsArray = new Godot.Collections.Array();
        foreach (var connection in Connections)
            connectionsArray.Add(connection.GetData());
        dict["OscConnections"] = connectionsArray;
        dict["MonitorEnabled"] = _monitorEnabled;
        return dict;
    }

    public void LoadFromData(Dictionary data)
    {
        if (data == null) return;

        if (data.TryGetValue("MonitorEnabled", out var monVar))
            _monitorEnabled = monVar.AsBool();

        if (data.TryGetValue("OscConnections", out var value)
            && value.As<Godot.Collections.Array>() is Godot.Collections.Array connectionsArray)
        {
            // Dispose existing
            foreach (var connection in Connections.ToList())
            {
                try
                {
                    connection.CloseConnection();
                    if (GodotObject.IsInstanceValid(connection))
                        connection.Free();
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"OscConnections:LoadFromData - dispose: {ex.Message}");
                }
            }
            Connections.Clear();
            CueOscConnection._nextId = 0;

            foreach (var item in connectionsArray)
            {
                if (item.As<Dictionary>() is Dictionary dict)
                {
                    var connection = new CueOscConnection();
                    connection.LoadFromData(dict);
                    connection.InitialiseSender();
                    Connections.Add(connection);
                }
            }
            GD.Print($"OscConnections:LoadFromData - Loaded {Connections.Count} connection(s)");
        }

        EmitSignal(SignalName.OscConnectionsStateChanged);
    }

    /// <summary>
    /// Closes and removes all OSC send connections (New Session).
    /// </summary>
    public void ClearAll()
    {
        foreach (var connection in Connections.ToList())
        {
            try
            {
                connection.CloseConnection();
                if (GodotObject.IsInstanceValid(connection))
                    connection.Free();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"OscConnections:ClearAll - {ex.Message}");
            }
        }
        Connections.Clear();
        CueOscConnection._nextId = 0;
        _monitorEnabled = true;
        EmitSignal(SignalName.OscConnectionsStateChanged);
        GD.Print("OscConnections:ClearAll - All OSC connections cleared.");
    }

    /// <summary>
    /// Footer/status snapshot of configured OSC send connections.
    /// </summary>
    /// <remarks>
    /// <see cref="OscSendStatus.Ok"/> is false when the sender is not open and not mid-connect
    /// (e.g. failed TCP or UDP init). Connecting counts as OK so the footer stays green while
    /// a background TCP connect is still in flight.
    /// </remarks>
    /// <returns>Ordered list of display rows for the Connections footer tooltip.</returns>
    public static System.Collections.Generic.List<OscSendStatus> GetSendConnectionStatuses()
    {
        var result = new System.Collections.Generic.List<OscSendStatus>();
        var list = Connections;
        if (list == null || list.Count == 0)
            return result;

        foreach (var c in list)
        {
            if (c == null || !GodotObject.IsInstanceValid(c))
                continue;

            string transport = c.Transport == OscTransport.Tcp ? "TCP" : "UDP";
            string dest = $"{c.Address}:{c.Port}";
            string label = $"{c.Name ?? "OSC"} ({transport} → {dest})";

            bool connecting = c.IsConnecting;
            bool open = c.IsSenderOpen;
            bool ok = open || connecting;
            string detail = connecting
                ? "connecting"
                : open
                    ? "ready"
                    : "not connected";

            result.Add(new OscSendStatus(label, ok, detail, connecting));
        }

        return result;
    }
}

/// <summary>
/// One OSC send connection row for footer / status UI.
/// </summary>
/// <param name="Label">Display name including transport and destination.</param>
/// <param name="Ok">True when ready or still connecting (not a hard failure).</param>
/// <param name="Detail">Short status phrase (ready / connecting / not connected).</param>
/// <param name="IsConnecting">True while a background TCP connect is in progress.</param>
public readonly record struct OscSendStatus(string Label, bool Ok, string Detail, bool IsConnecting);

/// <summary>OSC transport for a send connection.</summary>
public enum OscTransport
{
    Udp = 0,
    Tcp = 1
}

/// <summary>
/// A named OSC sender (UDP or TCP destination IP/port + optional local network interface).
/// TCP uses binary length-prefixed framing (Rug.Osc / common OSC-over-TCP).
/// </summary>
public partial class CueOscConnection : GodotObject
{
    public static int _nextId = 0;
    public int Id { get; set; }
    public string Name = "Osc";
    public string NetworkInterface { get; set; }
    public IPAddress Address { get; set; } = IPAddress.Loopback;
    public int Port { get; set; } = OscConnections.DefaultSendPort;
    public OscTransport Transport { get; set; } = OscTransport.Udp;

    private OscSender _sender;
    private TcpClient _tcpClient;
    private readonly object _tcpLock = new();

    /// <summary>
    /// Generation counter so async TCP connect results are ignored if the user
    /// changes transport/address again before the connect finishes.
    /// </summary>
    private int _connectGeneration;

    /// <summary>True when the underlying sender has been opened.</summary>
    public bool IsSenderOpen => Transport == OscTransport.Tcp
        ? (_tcpClient != null && _tcpClient.Connected)
        : _sender != null;

    /// <summary>True while a background TCP connect is in progress.</summary>
    public bool IsConnecting { get; private set; }

    /// <summary>
    /// Latest connect or send-status detail for UI tooltips (plain English, not localized).
    /// </summary>
    /// <value>Ready / connecting phrase, or a transport error message.</value>
    public string LastStatusDetail { get; private set; } = string.Empty;

    /// <summary>
    /// Resolves <see cref="OscConnections"/> — <b>must only be called on the Godot main thread</b>.
    /// Background workers must capture this reference before leaving the main thread.
    /// </summary>
    private static OscConnections GetManagerMainThread() =>
        Engine.GetMainLoop() is SceneTree tree
            ? tree.Root.GetNodeOrNull<OscConnections>("/root/OscConnections")
            : null;

    /// <summary>
    /// Opens the sender for the current transport. UDP is synchronous; TCP connects
    /// on a background thread so the UI is never blocked by connect timeout.
    /// </summary>
    public void InitialiseSender()
    {
        int gen = System.Threading.Interlocked.Increment(ref _connectGeneration);
        CloseConnectionKeepingGeneration();

        if (Transport == OscTransport.Tcp)
        {
            BeginTcpConnectAsync(gen);
            return;
        }

        // UDP — fast; keep on calling thread
        try
        {
            IPAddress localAddress = IPAddress.Any;
            if (!string.IsNullOrEmpty(NetworkInterface))
            {
                var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                var ni = interfaces.FirstOrDefault(i => i.Name == NetworkInterface);
                if (ni != null)
                {
                    var ipProps = ni.GetIPProperties();
                    var ipv4 = ipProps.UnicastAddresses.FirstOrDefault(a =>
                        a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (ipv4 != null)
                        localAddress = ipv4.Address;
                    else
                        GD.Print($"CueOscConnection:InitialiseSender - No IPv4 for interface '{NetworkInterface}'");
                }
                else
                {
                    GD.PrintErr($"CueOscConnection:InitialiseSender - Interface '{NetworkInterface}' not found");
                }
            }

            // IMPORTANT: localPort must be 0 (ephemeral). Rug.Osc's (address, port) / (local, remote, port)
            // constructors bind the *local* socket to the same port number as the remote destination.
            // With SO_REUSEADDR that steals/shares the OSC listen port, so the app can no longer
            // receive UDP OSC (TCP still works). Always use localPort=0 for senders.
            if (!string.IsNullOrEmpty(NetworkInterface))
            {
                _sender = new OscSender(
                    localAddress,
                    localPort: 0,
                    Address,
                    Port,
                    timeToLive: 8,
                    messageBufferSize: OscSender.DefaultMessageBufferSize,
                    maxPacketSize: OscSocket.DefaultPacketSize);
                GD.Print($"CueOscConnection:InitialiseSender - UDP via {NetworkInterface} → {Name}@{Address}:{Port}");
            }
            else
            {
                _sender = new OscSender(Address, localPort: 0, Port);
                GD.Print($"CueOscConnection:InitialiseSender - UDP auto → {Name}@{Address}:{Port}");
            }
            _sender.Connect();
            LastStatusDetail = "UDP socket ready";
            GetManagerMainThread()?.NotifyConnectionStatus(this, LastStatusDetail, phase: "ok");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueOscConnection:InitialiseSender - '{Name}': {ex.Message}");
            _sender = null;
            LastStatusDetail = ex.Message;
            GetManagerMainThread()?.NotifyConnectionStatus(this, LastStatusDetail, phase: "fail");
        }
    }

    /// <summary>
    /// Starts a non-blocking TCP connect. UI selection can update immediately.
    /// Manager reference is captured on the main thread; workers never call GetNode.
    /// </summary>
    private void BeginTcpConnectAsync(int generation)
    {
        IsConnecting = true;
        LastStatusDetail = "waiting for remote…";
        var addr = Address;
        int port = Port;
        string name = Name;
        // Capture on main thread only — workers must not call GetNodeOrNull.
        var manager = GetManagerMainThread();

        manager?.NotifyConnectionStatus(this, LastStatusDetail, phase: "connecting");

        // Fire-and-forget background connect — never block the UI thread.
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var client = OscTcpTransport.Connect(addr, port);
                // Discard if superseded by a newer Reconnect / transport change
                if (generation != System.Threading.Volatile.Read(ref _connectGeneration)
                    || Transport != OscTransport.Tcp)
                {
                    try { client.Close(); } catch { /* ignore */ }
                    return;
                }

                lock (_tcpLock)
                {
                    CloseTcpUnlocked();
                    _tcpClient = client;
                }

                IsConnecting = false;
                LastStatusDetail = "TCP session open";
                GD.Print($"CueOscConnection:BeginTcpConnectAsync - TCP connected → {name}@{addr}:{port}");
                manager?.NotifyConnectionStatus(this, LastStatusDetail, phase: "ok");
            }
            catch (Exception ex)
            {
                if (generation != System.Threading.Volatile.Read(ref _connectGeneration)) return;
                IsConnecting = false;
                CloseTcp();
                LastStatusDetail = ex.Message;
                GD.PrintErr($"CueOscConnection:BeginTcpConnectAsync - '{name}': {ex.Message}");
                manager?.NotifyConnectionStatus(this, LastStatusDetail, phase: "fail");
            }
        });
    }

    private void EnsureTcpConnected()
    {
        lock (_tcpLock)
        {
            if (_tcpClient != null && _tcpClient.Connected) return;
        }

        // Synchronous fallback for send path only (should already be connected).
        // Prefer calling from main thread; status notify is marshalled either way.
        var manager = GetManagerMainThread();
        try
        {
            var client = OscTcpTransport.Connect(Address, Port);
            lock (_tcpLock)
            {
                CloseTcpUnlocked();
                _tcpClient = client;
            }
            LastStatusDetail = "TCP reconnected on send";
            manager?.NotifyConnectionStatus(this, LastStatusDetail, phase: "ok");
        }
        catch (Exception ex)
        {
            CloseTcp();
            LastStatusDetail = $"reconnect on send failed: {ex.Message}";
            manager?.NotifyConnectionStatus(this, LastStatusDetail, phase: "fail");
            throw;
        }
    }

    private void CloseTcp()
    {
        lock (_tcpLock)
            CloseTcpUnlocked();
    }

    private void CloseTcpUnlocked()
    {
        try { _tcpClient?.Close(); } catch { /* ignore */ }
        try { _tcpClient?.Dispose(); } catch { /* ignore */ }
        _tcpClient = null;
    }

    /// <summary>
    /// Reconnects the OSC sender with the current properties (non-blocking for TCP).
    /// </summary>
    public void Reconnect()
    {
        InitialiseSender();
    }

    public void SendMessage(OscMessage message)
    {
        // Prefer main-thread manager; if somehow called off-thread, notifications still marshal.
        var manager = GetManagerMainThread();

        if (Transport == OscTransport.Tcp)
        {
            try
            {
                if (IsConnecting)
                    throw new InvalidOperationException("TCP still connecting");
                EnsureTcpConnected();
                lock (_tcpLock)
                {
                    if (_tcpClient == null || !_tcpClient.Connected)
                        throw new InvalidOperationException("TCP not connected");
                    OscTcpTransport.SendPacket(_tcpClient, message);
                }
                GD.Print($"CueOscConnection:SendMessage - TCP {message} via {Name}");
                manager?.NotifyMessageSent(this, message);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"CueOscConnection:SendMessage - TCP failed {message}: {ex.Message}");
                LastStatusDetail = ex.Message;
                manager?.NotifySendError(this, ex.Message);
                CloseTcp();
            }
            return;
        }

        if (_sender == null)
        {
            GD.PrintErr("CueOscConnection:SendMessage - Sender not initialized");
            manager?.NotifySendError(this, "Sender not initialized");
            return;
        }

        try
        {
            _sender.Send(message);
            GD.Print($"CueOscConnection:SendMessage - UDP {message} via {Name}");
            manager?.NotifyMessageSent(this, message);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueOscConnection:SendMessage - Failed {message}: {ex.Message}");
            manager?.NotifySendError(this, ex.Message);
        }
    }

    public void CloseConnection()
    {
        System.Threading.Interlocked.Increment(ref _connectGeneration);
        CloseConnectionKeepingGeneration();
    }

    private void CloseConnectionKeepingGeneration()
    {
        IsConnecting = false;
        try
        {
            _sender?.Close();
            _sender?.Dispose();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueOscConnection:CloseConnection - {ex.Message}");
        }
        _sender = null;
        CloseTcp();
    }

    public Dictionary GetData()
    {
        var dict = new Dictionary();
        dict["Id"] = Id;
        dict["Name"] = Name;
        dict["NetworkInterface"] = NetworkInterface ?? string.Empty;
        dict["Address"] = Address?.ToString() ?? "";
        dict["Port"] = Port;
        dict["Transport"] = (int)Transport;
        return dict;
    }

    public void LoadFromData(Dictionary data)
    {
        Id = data.TryGetValue("Id", out var value) ? (int)value : _nextId++;
        if (Id >= _nextId) _nextId = Id + 1;
        Name = data.TryGetValue("Name", out value) ? (string)value : Name;
        NetworkInterface = data.TryGetValue("NetworkInterface", out value) ? (string)value : NetworkInterface;
        Address = data.TryGetValue("Address", out value) && !string.IsNullOrEmpty((string)value)
            ? IPAddress.Parse((string)value)
            : IPAddress.Loopback;
        Port = data.TryGetValue("Port", out value) ? (int)value : Port;
        if (data.TryGetValue("Transport", out value))
            Transport = (OscTransport)value.AsInt32();
        else if (data.TryGetValue("UseTcp", out value) && value.AsBool())
            Transport = OscTransport.Tcp;
        GD.Print($"CueOscConnection:LoadFromData - {Id}: {Name} {Transport} {Address}:{Port}");
    }
}
