// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Domain.Connections;
using Cue2.Domain.Cues;
using Cue2.Services;
using Godot;
using Rug.Osc;

namespace Cue2.UI.Inspectors;

/// <summary>
/// Inspector card for a cue <see cref="OscComponent"/>: path, args, test send, delete.
/// </summary>
public partial class InspectorOscConnectionCard : PanelContainer
{
    private Label _nameLabel;
    private LineEdit _commandLineEdit;
    private LineEdit _argsLineEdit;
    private Button _testButton;
    private Button _deleteButton;
    private Label _oscConnectionLabel;
    private OscComponent _oscComponent;
    private ConnectionInspector _connectionInspector;
    private GlobalSignals _globalSignals;

    private bool _commandEditing;
    private bool _argsEditing;

    public override void _Ready()
    {
        _globalSignals = GetNodeOrNull<GlobalSignals>("/root/GlobalSignals");
        _nameLabel = GetNodeOrNull<Label>("%NameLabel");
        _commandLineEdit = GetNodeOrNull<LineEdit>("%CommandTextEdit");
        _argsLineEdit = GetNodeOrNull<LineEdit>("%ArgsTextEdit");
        _testButton = GetNodeOrNull<Button>("%TestSendButton");
        _deleteButton = GetNodeOrNull<Button>("%DeleteButton");
        _oscConnectionLabel = GetNodeOrNull<Label>("%OscConnectionLabel");

        if (_commandLineEdit != null)
        {
            _commandLineEdit.EditingToggled += OnCommandEditing;
            _commandLineEdit.TextSubmitted += OnCommandTextSubmitted;
        }
        if (_argsLineEdit != null)
        {
            _argsLineEdit.EditingToggled += OnArgsEditing;
            _argsLineEdit.TextSubmitted += OnArgsTextSubmitted;
        }
        if (_testButton != null)
            _testButton.Pressed += OnTestSendPressed;
        if (_deleteButton != null)
        {
            _deleteButton.Pressed += RemoveComponent;
            try
            {
                _deleteButton.Icon = GetThemeIcon("DeleteBin", "AtlasIcons");
            }
            catch { /* optional */ }
        }
    }

    public void SetComponent(OscComponent component, ConnectionInspector inspector)
    {
        _oscComponent = component;
        _connectionInspector = inspector;
        if (_nameLabel != null)
            _nameLabel.Text = component?.OscConnection?.Name ?? "OSC";
        if (_commandLineEdit != null)
            _commandLineEdit.Text = component?.OscMessage ?? string.Empty;
        if (_argsLineEdit != null)
            _argsLineEdit.Text = component?.ArgsText ?? string.Empty;
        if (_oscConnectionLabel != null && component?.OscConnection != null)
            _oscConnectionLabel.Text = $"{component.OscConnection.Address}:{component.OscConnection.Port}";
    }

    private void OnCommandEditing(bool editing)
    {
        if (editing) _commandEditing = true;
        else
        {
            _commandEditing = false;
            OnCommandTextSubmitted(_commandLineEdit?.Text ?? string.Empty);
        }
    }

    private void OnCommandTextSubmitted(string text)
    {
        _commandEditing = false;
        _commandLineEdit?.ReleaseFocus();
        if (_oscComponent == null) return;
        if (IsHistoryRestoring()) return;

        // Split QLab-style "/jump 2" into path + args so Rug.Osc accepts the address.
        if (!OscMessageUtil.SplitPathAndArgs(text, _argsLineEdit?.Text ?? _oscComponent.ArgsText,
                out string path, out string args))
            path = text ?? string.Empty;

        bool pathChanged = _oscComponent.OscMessage != path;
        bool argsChanged = (_oscComponent.ArgsText ?? string.Empty) != (args ?? string.Empty);
        if (!pathChanged && !argsChanged) return;

        RecordCueHistory("Edit OSC command");
        _oscComponent.OscMessage = path;
        if (argsChanged)
            _oscComponent.ArgsText = args ?? string.Empty;

        if (_commandLineEdit != null && _commandLineEdit.Text != path)
            _commandLineEdit.Text = path;
        if (argsChanged && _argsLineEdit != null && _argsLineEdit.Text != args)
            _argsLineEdit.Text = args ?? string.Empty;
    }

    private void OnArgsEditing(bool editing)
    {
        if (editing) _argsEditing = true;
        else
        {
            _argsEditing = false;
            OnArgsTextSubmitted(_argsLineEdit?.Text ?? string.Empty);
        }
    }

    private void OnArgsTextSubmitted(string text)
    {
        _argsEditing = false;
        _argsLineEdit?.ReleaseFocus();
        if (_oscComponent == null) return;
        if (_oscComponent.ArgsText == text) return;
        if (IsHistoryRestoring()) return;

        RecordCueHistory("Edit OSC args");
        _oscComponent.ArgsText = text ?? string.Empty;
    }

    private void OnTestSendPressed()
    {
        if (_oscComponent?.OscConnection == null)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "OSC test: no connection", (int)LogType.Warning);
            return;
        }

        try
        {
            // Commit + normalize fields first (handles "/jump 2" in the path box).
            string rawPath = _commandLineEdit?.Text ?? _oscComponent.OscMessage ?? string.Empty;
            string rawArgs = _argsLineEdit?.Text ?? _oscComponent.ArgsText ?? string.Empty;
            if (!OscMessageUtil.SplitPathAndArgs(rawPath, rawArgs, out string path, out string args)
                || string.IsNullOrEmpty(path) || path == "/")
            {
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                    "OSC test: path must start with /", (int)LogType.Warning);
                return;
            }

            _oscComponent.OscMessage = path;
            _oscComponent.ArgsText = args ?? string.Empty;
            if (_commandLineEdit != null && _commandLineEdit.Text != path)
                _commandLineEdit.Text = path;
            if (_argsLineEdit != null && _argsLineEdit.Text != args)
                _argsLineEdit.Text = args ?? string.Empty;

            OscMessage msg = string.IsNullOrWhiteSpace(args)
                ? new OscMessage(path)
                : OscMessageUtil.BuildMessage(path, args);
            _oscComponent.OscConnection.SendMessage(msg);
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"OSC test: {path} {OscMessageUtil.FormatArgs(msg)}", (int)LogType.Info);
        }
        catch (Exception ex)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"OSC test failed: {ex.Message}", (int)LogType.Error);
        }
    }

    /// <summary>
    /// True while HistoryManager is applying an undo/redo restore (do not re-record).
    /// </summary>
    private bool IsHistoryRestoring()
    {
        var gd = GetNodeOrNull<GlobalData>("/root/GlobalData");
        return gd?.HistoryManager?.IsRestoring == true;
    }

    private void RecordCueHistory(string description)
    {
        var gd = GetNodeOrNull<GlobalData>("/root/GlobalData");
        if (gd == null)
            return;
        InspectorMultiEditSupport.RecordBeforeEditById(
            gd, multiHistory: false, gd.FocusedCue, description);
    }

    private void RemoveComponent()
    {
        _connectionInspector?.RemoveComponent(_oscComponent);
        QueueFree();
    }
}
