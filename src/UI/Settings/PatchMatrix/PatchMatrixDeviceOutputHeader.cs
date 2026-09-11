// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;

namespace Cue2.UI.Settings.PatchMatrix;
public partial class PatchMatrixDeviceOutputHeader : Panel
{
    [Export]
    public string DeviceId { get; set; }
    
    [Export]
    public string DeviceName { get; set; }
    
    [Export]
    public string ParentDevice { get; set; }    
    
    [Export]
    public string CurrentOutputName { get; set; }
    
    [Export]
    public int OutputIndex { get; set; }

    public override void _Ready()
    {
        if (HasNode("Label"))
        {

            GetNode<Label>("Label").Text = DeviceName;
        }
    }
}
