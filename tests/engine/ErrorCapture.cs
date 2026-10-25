// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Godot;

namespace Cue2.EngineTests;

/// <summary>
/// Collects engine errors (the <c>ERROR:</c> lines) so tests can fail on new ones, and the
/// printed log lines so tests can wait for one.
/// </summary>
internal partial class ErrorCapture : Logger
{
    private readonly object _lock = new();
    private const int MaxMessages = 50000;

    private readonly List<string> _errors = new();
    private readonly List<string> _messages = new();
    private int _droppedMessages;

    /// <summary>Creates a capture and registers it with the engine.</summary>
    internal static ErrorCapture Install()
    {
        var capture = new ErrorCapture();
        OS.AddLogger(capture);
        return capture;
    }

    /// <summary>Number of errors seen so far.</summary>
    internal int Count
    {
        get
        {
            lock (_lock)
                return _errors.Count;
        }
    }

    /// <summary>Errors seen after the first <paramref name="mark"/> entries.</summary>
    internal string[] Since(int mark)
    {
        lock (_lock)
        {
            if (mark >= _errors.Count)
                return System.Array.Empty<string>();
            return _errors.GetRange(mark, _errors.Count - mark).ToArray();
        }
    }

    /// <summary>Total number of log lines seen so far (including any no longer kept).</summary>
    internal int MessageCount
    {
        get
        {
            lock (_lock)
                return _droppedMessages + _messages.Count;
        }
    }

    /// <summary>Log lines seen after the first <paramref name="mark"/> lines.</summary>
    internal string[] MessagesSince(int mark)
    {
        lock (_lock)
        {
            int start = System.Math.Max(0, mark - _droppedMessages);
            if (start >= _messages.Count)
                return System.Array.Empty<string>();
            return _messages.GetRange(start, _messages.Count - start).ToArray();
        }
    }

    /// <inheritdoc />
    public override void _LogMessage(string message, bool error)
    {
        lock (_lock)
        {
            if (_messages.Count >= MaxMessages)
            {
                _messages.RemoveRange(0, MaxMessages / 2);
                _droppedMessages += MaxMessages / 2;
            }
            _messages.Add(message);
        }
    }

    /// <inheritdoc />
    public override void _LogError(
        string function,
        string file,
        int line,
        string code,
        string rationale,
        bool editorNotify,
        int errorType,
        Godot.Collections.Array<ScriptBacktrace> scriptBacktraces)
    {
        if (errorType == (int)ErrorType.Warning)
            return;
        string text = string.IsNullOrEmpty(rationale) ? code : rationale;
        lock (_lock)
            _errors.Add($"{text} ({function} {file}:{line})");
    }
}
