// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;

namespace Cue2.Domain.Cues;

public interface ICue
{
    int Id { get; }
    string Name { get; set; }
    string CueNum { get; set; }
    ShellBar ShellBar { get; set; }
    
}