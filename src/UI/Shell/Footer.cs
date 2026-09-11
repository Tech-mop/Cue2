// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Cue2.Domain.Connections;
using Cue2.Services;
using Cue2.UI.Utilities;
using static Cue2.UI.Utilities.UiLocalizer;

namespace Cue2.UI.Shell;


/// <summary>
/// Main application footer bar: device status, connections, log readout,
/// total cue count, background process progress, and CPU/memory usage.
/// </summary>
public partial class Footer : Control
{
    private GlobalSignals _globalSignals;
    
    private List<string> _last5Logs = new List<string>();
    private Node _logWindow;

    // Ui
    private Label _totalCuesLabel;
    private Label _cpuUsageLabel;
    private Label _memoryUsageLabel;
    private Control _processProgressHost;
    private ProgressBar _bkgProcessStatusBar;
    private Label _processStatusLabel;
    private Control _processSeparator;
    private Button _logCountButton;
    private string _logPrintoutBaseTooltip = "Log";
    
    private Timer _updateTimer;
    private Timer _processHideTimer;
    private bool _updateAvailableClickable;

    // Process resource tracking (CPU / memory)
    private Process _currentProcess;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleUtc;
    private bool _hasCpuBaseline;

    // Devices status
    private GlobalData _globalData;
    private AudioDevices _audioDevices;
    private DisplaysManager _displaysManager;
    private Button _devicesFooterButton;

    // Connections status (OSC send/listen + MIDI session devices)
    private Button _connectionsFooterButton;
    private OscConnections _oscConnections;
    private OscListen _oscListen;
    private MidiManager _midiManager;
    
    public override void _Ready()
    {
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");

        _globalSignals.LogUpdated += _updateLog;
        
        _logCountButton = GetNode<Button>("%LogCountButton");
        
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _audioDevices = GetNode<AudioDevices>("/root/AudioDevices");
        _displaysManager = GetNode<DisplaysManager>("/root/DisplaysManager");
        _oscConnections = GetNodeOrNull<OscConnections>("/root/OscConnections");
        _oscListen = GetNodeOrNull<OscListen>("/root/OscListen");
        _midiManager = GetNodeOrNull<MidiManager>("/root/MidiManager");

        _devicesFooterButton = GetNode<Button>("%DevicesFooterButton");
        _devicesFooterButton.TooltipText = T("Devices");
        _devicesFooterButton.MouseEntered += UpdateDevicesFooterTooltip;
        
        _connectionsFooterButton = GetNodeOrNull<Button>("%ConnectionsFooterButton");
        if (_connectionsFooterButton != null)
        {
            _connectionsFooterButton.TooltipText = T("Connections");
            _connectionsFooterButton.MouseEntered += UpdateConnectionsFooterTooltip;
        }

        _globalSignals.AudioDevicesChanged += UpdateDevicesFooterTooltip;
        _globalSignals.DisplaysChanged += UpdateDevicesFooterTooltip;

        if (_oscConnections != null)
            _oscConnections.OscConnectionsStateChanged += UpdateConnectionsFooterTooltip;
        if (_oscListen != null)
            _oscListen.OscStateChanged += UpdateConnectionsFooterTooltip;
        if (_midiManager != null)
            _midiManager.MidiStateChanged += UpdateConnectionsFooterTooltip;
        
        _logCountButton.Toggled += OnLogCountToggled;

        _globalSignals.ToggleLogWindow += ToggleLogWindow;

        _syncHotkeys();
        
        _totalCuesLabel = GetNodeOrNull<Label>("%TotalCuesLabel");
        // Formatted at runtime ("{0} total cues") — do not let LocalizeTree capture "0 total cues".
        _totalCuesLabel?.SetMeta(MetaSkip, true);
        _cpuUsageLabel = GetNode<Label>("%CpuUsageLabel");
        _memoryUsageLabel = GetNode<Label>("%MemoryUsageLabel");

        // Background process progress (media backup, cuelist bulk ops) — left of total cues / CPU
        _processProgressHost = GetNodeOrNull<Control>("%ProcessProgressHost");
        _bkgProcessStatusBar = GetNodeOrNull<ProgressBar>("%BkgProcessStatusBar");
        _processStatusLabel = GetNodeOrNull<Label>("%ProcessStatusLabel");
        _processSeparator = GetNodeOrNull<Control>("%VSeparator5");
        SetProcessProgressVisible(false);
        _globalSignals.MediaBackupProgress += OnMediaBackupProgress;
        _globalSignals.MediaBackupCompleted += OnMediaBackupCompleted;
        _globalSignals.BackgroundProcessProgress += OnBackgroundProcessProgress;
        _globalSignals.BackgroundProcessCompleted += OnBackgroundProcessCompleted;
        _globalSignals.UpdateUiStateChanged += OnUpdateUiStateChanged;
        if (_processProgressHost != null)
            _processProgressHost.GuiInput += OnProcessProgressGuiInput;
        if (_bkgProcessStatusBar != null)
            _bkgProcessStatusBar.GuiInput += OnProcessProgressGuiInput;
        _globalSignals.TotalCuesChanged += OnTotalCuesChanged;

        try
        {
            _currentProcess = Process.GetCurrentProcess();
            _lastCpuTime = _currentProcess.TotalProcessorTime;
            _lastCpuSampleUtc = DateTime.UtcNow;
            _hasCpuBaseline = true;
        }
        catch (Exception ex)
        {
            GD.Print($"Footer:_Ready - Failed to open current process for resource tracking: {ex.Message}");
            _hasCpuBaseline = false;
        }
        
        _updateTimer = new Timer();
        AddChild(_updateTimer);
        _updateTimer.WaitTime = 1.0; // Match OBS-style ~1s resource refresh
        _updateTimer.Start();
        _updateTimer.Timeout += UpdateResourceUsage;

        UpdateDevicesFooterTooltip(); // initial status
        UpdateConnectionsFooterTooltip();
        UpdateResourceUsage(); // initial CPU/MEM read

        LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;

        // After LocalizeTree so the formatted count is not overwritten by the scene placeholder.
        int initialTotal = _globalData?.Cuelist?.TotalCueCount ?? _globalData?.CueTotal ?? 0;
        UpdateTotalCuesLabel(initialTotal);
    }

    /// <summary>
    /// Re-localizes static footer chrome and refreshes dynamic labels.
    /// </summary>
    /// <param name="localeCode">New locale code.</param>
    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        LocalizeTree(this);
        int total = _globalData?.Cuelist?.TotalCueCount ?? _globalData?.CueTotal ?? 0;
        UpdateTotalCuesLabel(total);
        UpdateResourceUsage();
        _syncHotkeys();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_devicesFooterButton != null)
            _devicesFooterButton.MouseEntered -= UpdateDevicesFooterTooltip;
        if (_connectionsFooterButton != null)
            _connectionsFooterButton.MouseEntered -= UpdateConnectionsFooterTooltip;

        if (_globalSignals != null)
        {
            _globalSignals.AudioDevicesChanged -= UpdateDevicesFooterTooltip;
            _globalSignals.DisplaysChanged -= UpdateDevicesFooterTooltip;
            _globalSignals.LogUpdated -= _updateLog;
            _globalSignals.ToggleLogWindow -= ToggleLogWindow;
            _globalSignals.MediaBackupProgress -= OnMediaBackupProgress;
            _globalSignals.MediaBackupCompleted -= OnMediaBackupCompleted;
            _globalSignals.BackgroundProcessProgress -= OnBackgroundProcessProgress;
            _globalSignals.BackgroundProcessCompleted -= OnBackgroundProcessCompleted;
            _globalSignals.UpdateUiStateChanged -= OnUpdateUiStateChanged;
            _globalSignals.TotalCuesChanged -= OnTotalCuesChanged;
            _globalSignals.LocaleChanged -= OnLocaleChanged;
        }

        if (_processProgressHost != null)
            _processProgressHost.GuiInput -= OnProcessProgressGuiInput;
        if (_bkgProcessStatusBar != null)
            _bkgProcessStatusBar.GuiInput -= OnProcessProgressGuiInput;

        if (_oscConnections != null)
            _oscConnections.OscConnectionsStateChanged -= UpdateConnectionsFooterTooltip;
        if (_oscListen != null)
            _oscListen.OscStateChanged -= UpdateConnectionsFooterTooltip;
        if (_midiManager != null)
            _midiManager.MidiStateChanged -= UpdateConnectionsFooterTooltip;

        base._ExitTree();
    }

    /// <summary>
    /// Handles <see cref="GlobalSignals.TotalCuesChanged"/> and refreshes the footer readout.
    /// </summary>
    /// <param name="total">New total cue count for the show.</param>
    private void OnTotalCuesChanged(int total)
    {
        UpdateTotalCuesLabel(total);
    }

    /// <summary>
    /// Sets the footer total-cues label text (e.g. "42 total cues").
    /// </summary>
    /// <param name="total">Number of cues currently in the show.</param>
    private void UpdateTotalCuesLabel(int total)
    {
        if (_totalCuesLabel == null)
            return;

        int safeTotal = Math.Max(0, total);
        _totalCuesLabel.Text = Tf("{0} total cues", safeTotal);
        _totalCuesLabel.TooltipText =
            safeTotal == 1
                ? T("1 cue in the show")
                : Tf("{0} total cues in the show", safeTotal);
    }

    /// <summary>
    /// Updates the process progress bar overlay and tooltip during media backup.
    /// </summary>
    private void OnMediaBackupProgress(float percent, bool busy, string statusText, string originPath, string destPath, int completedCount, int totalCount)
    {
        string origin = string.IsNullOrEmpty(originPath) ? "…" : originPath;
        string dest = string.IsNullOrEmpty(destPath) ? "…" : destPath;
        string detail = $"{origin} → {dest}";
        ApplyProcessProgress(percent, busy, statusText, detail, completedCount, totalCount);
    }

    private void OnMediaBackupCompleted()
    {
        CompleteProcessProgress("Copying 100%");
    }

    /// <summary>
    /// Updates the process progress bar for generic background work (e.g. bulk cuelist ops).
    /// </summary>
    private void OnBackgroundProcessProgress(float percent, bool busy, string statusText, string detail, int completedCount, int totalCount)
    {
        ApplyProcessProgress(percent, busy, statusText, detail ?? string.Empty, completedCount, totalCount);
    }

    private void OnBackgroundProcessCompleted()
    {
        if (_updateAvailableClickable)
            return;

        string finalLabel = _processStatusLabel?.Text;
        if (string.IsNullOrEmpty(finalLabel) || finalLabel.EndsWith("0%", StringComparison.Ordinal))
            finalLabel = "Done";
        // Prefer a 100% style label when possible
        if (_bkgProcessStatusBar != null && _bkgProcessStatusBar.Value < 100.0)
            _bkgProcessStatusBar.Value = 100.0;
        CompleteProcessProgress(finalLabel);
    }

    private void OnUpdateUiStateChanged(int state, string message)
    {
        var ui = (UpdateUiState)state;
        _updateAvailableClickable = ui is UpdateUiState.Available or UpdateUiState.ReadyToInstall;
        if (_processProgressHost != null)
        {
            _processProgressHost.MouseDefaultCursorShape = _updateAvailableClickable
                ? CursorShape.PointingHand
                : CursorShape.Arrow;
        }

        if (ui is UpdateUiState.Idle or UpdateUiState.UpToDate)
        {
            _updateAvailableClickable = false;
            if (_processHideTimer == null || _processHideTimer.IsStopped())
                SetProcessProgressVisible(false);
        }
    }

    private void OnProcessProgressGuiInput(InputEvent @event)
    {
        if (!_updateAvailableClickable)
            return;
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            _globalSignals?.EmitSignal(GlobalSignals.SignalName.OpenSettingsMenu, "Updates");
            GetViewport()?.SetInputAsHandled();
        }
    }

    /// <summary>
    /// Shared footer progress UI for media backup and generic background tasks.
    /// </summary>
    private void ApplyProcessProgress(float percent, bool busy, string statusText, string detail, int completedCount, int totalCount)
    {
        if (_bkgProcessStatusBar == null)
            return;

        // Stay visible for the whole batch; completed handler hides after a short delay
        if (busy || totalCount > 0)
            SetProcessProgressVisible(true);

        // Cancel a pending hide if a new process starts (or continues)
        if (busy)
            _processHideTimer?.Stop();

        _bkgProcessStatusBar.MaxValue = 100.0;
        _bkgProcessStatusBar.Value = percent;

        string label = string.IsNullOrEmpty(statusText) ? $"{percent:F0}%" : statusText;
        if (_processStatusLabel != null)
            _processStatusLabel.Text = label;

        if (_globalData != null && busy)
            _globalData.FooterBackgroundTaskStatus = label;

        string tip = $"{percent:F0}%";
        if (!string.IsNullOrEmpty(detail))
            tip += $"\n{detail}";
        if (totalCount > 0)
            tip += $"\n{completedCount}/{totalCount}";

        _bkgProcessStatusBar.TooltipText = tip;
        if (_processProgressHost != null)
            _processProgressHost.TooltipText = tip;
        if (_processStatusLabel != null)
            _processStatusLabel.TooltipText = tip;
    }

    private void CompleteProcessProgress(string finalStatusText)
    {
        if (_bkgProcessStatusBar != null)
            _bkgProcessStatusBar.Value = 100.0;
        if (_processStatusLabel != null && !string.IsNullOrEmpty(finalStatusText))
            _processStatusLabel.Text = finalStatusText;

        // Reuse a single one-shot timer so rapid batches don't stack hide callbacks
        if (_processHideTimer == null)
        {
            _processHideTimer = new Timer { WaitTime = 0.6, OneShot = true };
            _processHideTimer.Timeout += OnProcessHideTimeout;
            AddChild(_processHideTimer);
        }
        else
        {
            _processHideTimer.Stop();
        }

        _processHideTimer.Start();
    }

    private void OnProcessHideTimeout()
    {
        SetProcessProgressVisible(false);
        if (_bkgProcessStatusBar != null)
        {
            _bkgProcessStatusBar.Value = 0;
            _bkgProcessStatusBar.TooltipText = UiLocalizer.T("Background process");
        }
        if (_processStatusLabel != null)
            _processStatusLabel.Text = string.Empty;
    }

    private void SetProcessProgressVisible(bool visible)
    {
        if (_processProgressHost != null)
            _processProgressHost.Visible = visible;
        if (_processSeparator != null)
            _processSeparator.Visible = visible;
    }

    private void _updateLog(String @printout, int @type)
    {
        var logPrintout = GetNode<Button>("%LogPrintout");
        // Footer is a single row: collapse newlines so multiline logs cannot grow its height.
        logPrintout.Text = ToSingleLine(printout);
        if (type == 0) logPrintout.RemoveThemeColorOverride("font_color");
        if (type == 1) logPrintout.AddThemeColorOverride("font_color", GlobalStyles.Warning);
        if (type == 2) logPrintout.AddThemeColorOverride("font_color", GlobalStyles.Danger);
        if (type == 3) logPrintout.AddThemeColorOverride("font_color", GlobalStyles.Danger);
        _logCountButton.Text = Tf("Log {0}", EventLogger.GetLogCount());
        
        _last5Logs.Add(@printout);
        if (_last5Logs.Count > 5)
        {
            _last5Logs.RemoveAt(0);
        }
        
        //Update log tooltip to show last 5 logs
        logPrintout.TooltipText = _logPrintoutBaseTooltip + "\n\n" + T("Last 5 log messages:") + "\n";
        foreach (var log in _last5Logs)
        {    
            logPrintout.TooltipText += log + "\n";
        }
    }

    /// <summary>
    /// Updates footer CPU and memory readouts for the Cue2 process (OBS-style status).
    /// CPU is process usage as a percent of total machine capacity (all logical processors).
    /// Memory is the process working set.
    /// </summary>
    private void UpdateResourceUsage()
    {
        if (_cpuUsageLabel == null || _memoryUsageLabel == null)
            return;

        if (_currentProcess == null)
        {
            _cpuUsageLabel.Text = T("CPU --.-%");
            _memoryUsageLabel.Text = T("MEM -- MB");
            return;
        }

        try
        {
            _currentProcess.Refresh();

            // Memory: working set (physical RAM used by this process)
            double memMb = _currentProcess.WorkingSet64 / (1024.0 * 1024.0);
            double memPrivateMb = _currentProcess.PrivateMemorySize64 / (1024.0 * 1024.0);
            _memoryUsageLabel.Text = Tf("MEM {0} MB", memMb.ToString("F0"));
            _memoryUsageLabel.TooltipText =
                T("Memory usage (this process)") + "\n" +
                Tf("Working set: {0} MB", memMb.ToString("F1")) + "\n" +
                Tf("Private: {0} MB", memPrivateMb.ToString("F1"));

            // CPU: delta of TotalProcessorTime over wall-clock interval, normalized by core count
            TimeSpan currentCpu = _currentProcess.TotalProcessorTime;
            DateTime nowUtc = DateTime.UtcNow;

            if (!_hasCpuBaseline)
            {
                _lastCpuTime = currentCpu;
                _lastCpuSampleUtc = nowUtc;
                _hasCpuBaseline = true;
                _cpuUsageLabel.Text = T("CPU --.-%");
                _cpuUsageLabel.TooltipText = T("CPU usage (this process)") + "\n" + T("Sampling…");
                return;
            }

            double cpuDeltaMs = (currentCpu - _lastCpuTime).TotalMilliseconds;
            double wallDeltaMs = (nowUtc - _lastCpuSampleUtc).TotalMilliseconds;

            _lastCpuTime = currentCpu;
            _lastCpuSampleUtc = nowUtc;

            if (wallDeltaMs <= 0.0)
            {
                _cpuUsageLabel.Text = T("CPU --.-%");
                return;
            }

            // Normalize by logical processor count so 100% ≈ full machine (Task Manager / OBS style)
            int coreCount = Math.Max(1, System.Environment.ProcessorCount);
            double cpuPercent = Math.Clamp((cpuDeltaMs / wallDeltaMs) * 100.0 / coreCount, 0.0, 100.0);

            _cpuUsageLabel.Text = Tf("CPU {0}%", cpuPercent.ToString("F1"));
            _cpuUsageLabel.TooltipText =
                T("CPU usage (this process)") + "\n" +
                Tf("{0}% of total system capacity", cpuPercent.ToString("F1")) + "\n" +
                Tf("Logical processors: {0}", coreCount);
        }
        catch (Exception ex)
        {
            GD.Print($"Footer:UpdateResourceUsage - {ex.Message}");
            _cpuUsageLabel.Text = T("CPU --.-%");
            _memoryUsageLabel.Text = T("MEM -- MB");
        }
    }
    
    
    private void OnLogCountToggled(Boolean @toggle)
    {
        if (@toggle == true){
            if (_logWindow == null)
            {
                GD.Print("Loading settings window scene");
                _logWindow = SceneLoader.LoadScene("uid://cg8mrxu40hjf", out string error); // Loads settings window
                _logWindow.TreeExiting += OnLogWindowClosed;
                AddChild(_logWindow);
            }
            else {
                _logWindow.GetWindow().Show();
            }
        }
        if (@toggle == false)
        {
            _logWindow?.QueueFree();
        }
    }

    private void OnLogWindowClosed()
    {
        GD.Print($"Footer:OnLogWindowClosed");
        _logWindow = null;
        _logCountButton.ButtonPressed = false;
    }

    private void ToggleLogWindow()
    {
        _logCountButton.ButtonPressed = !_logCountButton.ButtonPressed;
    }

    private void _syncHotkeys()
    {
        string logHotkey = GlobalData.ParseHotkey("ToggleLog");
        _logCountButton.TooltipText = T("Log, click to open full log") +
            (!string.IsNullOrEmpty(logHotkey) ? "\n" + Tf("Hotkey: {0}", logHotkey) : "");

        var logPrintout = GetNode<Button>("%LogPrintout");
        _logPrintoutBaseTooltip = T("Log");
        if (!string.IsNullOrEmpty(logHotkey))
            _logPrintoutBaseTooltip += "\n" + Tf("Hotkey: {0}", logHotkey);
        logPrintout.TooltipText = _logPrintoutBaseTooltip;
    }

    /// <summary>
    /// Collapses CR/LF in a log line so the footer readout stays one row (ellipsis clips the rest).
    /// </summary>
    private static string ToSingleLine(string printout)
    {
        if (string.IsNullOrEmpty(printout))
            return string.Empty;
        return printout.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\n', ' ').Trim();
    }

    /// <summary>
    /// Updates the DevicesFooterButton tooltip and color with the status of used audio devices and video outputs.
    /// Green (🟢) = connected/available.
    /// Red (🔴) = configured/used but target not currently available.
    /// Tints the button green (Success) when all are OK, red (Danger) if any problems.
    /// </summary>
    private void UpdateDevicesFooterTooltip()
    {
        if (_devicesFooterButton == null) return;

        var audioStatuses = _audioDevices?.GetUsedAudioDeviceStatuses() ?? new Dictionary<string, bool>();
        var videoStatuses = _displaysManager?.GetVideoOutputStatuses() ?? new Dictionary<string, bool>();

        if (audioStatuses.Count == 0 && videoStatuses.Count == 0)
        {
            _devicesFooterButton.TooltipText = UiLocalizer.T("Devices\n\nNo audio or video devices are currently used or configured.");
            _devicesFooterButton.AddThemeColorOverride("font_color", GlobalStyles.Success);
            return;
        }

        bool hasProblem = false;
        string tooltip = T("Devices:") + "\n";

        if (audioStatuses.Count > 0)
        {
            tooltip += "\n" + T("Audio Devices:") + "\n";
            foreach (var entry in audioStatuses.OrderBy(e => e.Key))
            {
                bool connected = entry.Value;
                if (!connected) hasProblem = true;

                string indicator = connected ? "🟢 " : "🔴 ";
                string status = connected ? T("connected") : T("being used but not connected");
                tooltip += $"{indicator}{entry.Key} ({status})\n";
            }
        }

        if (videoStatuses.Count > 0)
        {
            tooltip += "\n" + T("Video Outputs:") + "\n";
            foreach (var entry in videoStatuses.OrderBy(e => e.Key))
            {
                bool connected = entry.Value;
                if (!connected) hasProblem = true;

                string indicator = connected ? "🟢 " : "🔴 ";
                string status = connected ? T("connected") : T("target monitor unavailable");
                tooltip += $"{indicator}{entry.Key} ({status})\n";
            }
        }

        _devicesFooterButton.TooltipText = tooltip.TrimEnd('\n', '\r');

        if (hasProblem)
        {
            _devicesFooterButton.AddThemeColorOverride("font_color", GlobalStyles.Danger);
        }
        else
        {
            _devicesFooterButton.AddThemeColorOverride("font_color", GlobalStyles.Success);
        }
    }

    /// <summary>
    /// Updates the ConnectionsFooterButton tooltip and color with OSC send/listen and MIDI session status.
    /// Green (🟢) = ready/open/listening.
    /// Yellow (🟡) = connecting (OSC TCP in flight) — not treated as a fault.
    /// Red (🔴) = configured but not available (or listen enabled but failed).
    /// Gray (⚪) = intentionally inactive (listen off / MIDI disabled).
    /// Button tints Success when all configured links are healthy, Danger if any hard failure.
    /// </summary>
    private void UpdateConnectionsFooterTooltip()
    {
        if (_connectionsFooterButton == null)
            return;

        var oscSends = OscConnections.GetSendConnectionStatuses();
        var midiInputs = _midiManager?.GetSessionInputStatuses()
                         ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var midiOutputs = _midiManager?.GetSessionOutputStatuses()
                          ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool midiEnabled = _midiManager?.MidiEnabled ?? false;
        bool midiNativeReady = _midiManager?.IsNativeReady ?? false;

        bool hasOscListen = _oscListen != null;
        bool hasAnyConfigured =
            oscSends.Count > 0 ||
            midiInputs.Count > 0 ||
            midiOutputs.Count > 0 ||
            hasOscListen;

        if (!hasAnyConfigured)
        {
            _connectionsFooterButton.TooltipText =
                UiLocalizer.T("Connections\n\nNo OSC or MIDI connections are currently configured.");
            _connectionsFooterButton.AddThemeColorOverride("font_color", GlobalStyles.Success);
            return;
        }

        bool hasProblem = false;
        var sb = new StringBuilder();
        sb.AppendLine(T("Connections:"));

        // ── OSC send destinations ───────────────────────────────────────────
        if (oscSends.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(T("OSC Send:"));
            foreach (var row in oscSends.OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase))
            {
                if (!row.Ok)
                    hasProblem = true;

                string indicator = row.IsConnecting ? "🟡 " : row.Ok ? "🟢 " : "🔴 ";
                sb.AppendLine($"{indicator}{row.Label} ({row.Detail})");
            }
        }

        // ── OSC listen ──────────────────────────────────────────────────────
        if (_oscListen != null)
        {
            sb.AppendLine();
            sb.AppendLine(T("OSC Listen:"));

            bool enabled = _oscListen.OscListenEnabled;
            bool listening = _oscListen.IsListening;
            int port = _oscListen.Port;
            bool tcp = _oscListen.TcpEnabled;
            int tcpPort = _oscListen.TcpPort;
            string session = _oscListen.SessionName;

            string portPart = tcp
                ? $"UDP :{port}, TCP :{tcpPort}"
                : $"UDP :{port}";
            if (!string.IsNullOrEmpty(session))
                portPart += $"  prefix /{session}/";

            if (!enabled)
            {
                sb.AppendLine($"⚪ {Tf("Disabled ({0})", portPart)}");
            }
            else if (listening)
            {
                sb.AppendLine($"🟢 {Tf("Listening ({0})", portPart)}");
            }
            else
            {
                hasProblem = true;
                sb.AppendLine($"🔴 {Tf("Enabled but not listening ({0})", portPart)}");
            }
        }

        // ── MIDI ────────────────────────────────────────────────────────────
        if (midiInputs.Count > 0 || midiOutputs.Count > 0 || !midiNativeReady)
        {
            sb.AppendLine();
            if (!midiNativeReady)
            {
                hasProblem = true;
                sb.AppendLine(T("MIDI:"));
                sb.AppendLine("🔴 " + T("Native MIDI library unavailable"));
            }
            else if (!midiEnabled)
            {
                sb.AppendLine(T("MIDI (disabled):"));
                AppendMidiSessionLines(sb, "Inputs", midiInputs, midiEnabled: false, ref hasProblem);
                AppendMidiSessionLines(sb, "Outputs", midiOutputs, midiEnabled: false, ref hasProblem);
            }
            else
            {
                sb.AppendLine(T("MIDI:"));
                AppendMidiSessionLines(sb, "Inputs", midiInputs, midiEnabled: true, ref hasProblem);
                AppendMidiSessionLines(sb, "Outputs", midiOutputs, midiEnabled: true, ref hasProblem);
            }
        }

        _connectionsFooterButton.TooltipText = sb.ToString().TrimEnd('\n', '\r');
        _connectionsFooterButton.AddThemeColorOverride(
            "font_color",
            hasProblem ? GlobalStyles.Danger : GlobalStyles.Success);
    }

    /// <summary>
    /// Appends a MIDI input or output subsection to the connections tooltip.
    /// </summary>
    private static void AppendMidiSessionLines(
        StringBuilder sb,
        string heading,
        Dictionary<string, bool> statuses,
        bool midiEnabled,
        ref bool hasProblem)
    {
        if (statuses == null || statuses.Count == 0)
            return;

        sb.AppendLine($"  {T(heading)}:");
        foreach (var entry in statuses.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!midiEnabled)
            {
                // Intentionally closed while MIDI is off — not a fault.
                sb.AppendLine($"  ⚪ {entry.Key} ({T("session — MIDI off")})");
                continue;
            }

            bool open = entry.Value;
            if (!open)
                hasProblem = true;

            string indicator = open ? "🟢 " : "🔴 ";
            string status = open ? T("open") : T("session device offline");
            sb.AppendLine($"  {indicator}{entry.Key} ({status})");
        }
    }
}
