// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;

namespace Cue2.UI.Settings.PatchMatrix;

public partial class AudioMatrixCheckBox : CheckBox
{
    [Export]
    int DeviceChannel { get; set; }
    [Export]
    int DeviceId { get; set; }
    [Export]
    int Channel { get; set; }
    
    
    private CheckBox _checkBox;

    public override void _Ready()
    {
    }

    public void SetDisabled()
    {
        Disabled = true;
    }
    
    public void SetEnabled()
    {
        Disabled = false;
    }
}