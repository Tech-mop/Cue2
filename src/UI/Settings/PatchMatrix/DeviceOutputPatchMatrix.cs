// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;


namespace Cue2.UI.Settings.PatchMatrix;
public partial class DeviceOutputPatchMatrix : Panel
{
    [Export]
    public string DeviceId { get; set; }
    [Export]
    public string DeviceName { get; set; }

    public override void _Ready()
    {
        if (HasNode("Label"))
        {

            GetNode<Label>("Label").Text = DeviceName;
        }
    }
}
