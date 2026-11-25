// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Services;
using NUnit.Framework;

namespace Cue2.Tests.Services;

/// <summary>
/// Cue-edit chords and document undo stay off while a LineEdit is focused; Save still runs.
/// </summary>
[TestFixture]
public class InputMapFocusGateTests
{
    /// <summary>Copy, paste, cut, and select-all must not also hit the cuelist while typing.</summary>
    [Test]
    public void CueEditShortcutsAreBlockedWhileTyping()
    {
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused("CopySelectedCues"), Is.False);
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused("PasteCues"), Is.False);
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused("CutSelectedCues"), Is.False);
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused("SelectAll"), Is.False);
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused("DuplicateSelectedCues"), Is.False);
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused("DeleteCue"), Is.False);
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused("CreateCue"), Is.False);
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused("Go"), Is.False);
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused(null), Is.False);
    }

    /// <summary>Godot LineEdit owns Cmd/Ctrl+Z while typing; show history must not also run.</summary>
    [Test]
    public void DocumentUndoRedoAreBlockedWhileTyping()
    {
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused("Undo"), Is.False);
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused("Redo"), Is.False);
    }

    /// <summary>File and window Cmd/Ctrl shortcuts still run from a focused field.</summary>
    [Test]
    public void FileAndWindowShortcutsStayAllowed()
    {
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused("SaveSession"), Is.True);
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused("SaveAsSession"), Is.True);
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused("OpenSession"), Is.True);
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused("NewSession"), Is.True);
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused("ToggleSettings"), Is.True);
        Assert.That(InputMapFocusGate.AllowWhileTextFieldFocused("ToggleLog"), Is.True);
    }
}
