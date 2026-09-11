// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Godot;
using System.Threading.Tasks;
using Cue2.Domain.Connections;
using Cue2.Services;
using Godot.Collections;

namespace Cue2.Domain.Cues;

/// <summary>
/// Enum for cue light actions.
/// </summary>
public enum CueLightAction
{
    Go,
    Standby,
    CountIn,
    IdentifyStart,
    IdentifyStop,
    Cancel
}

/// <summary>
/// ICueComponent for cue light actions in cues.
/// </summary>
public class CueLightComponent : ICueComponent 
{
    public string Type => "CueLight"; 
    public int CueLightId;
    public CueLightAction Action;
    public float CountInTime; // For CountIn
    public CueLight CueLight;
    
    [Signal] public delegate void CompletedEventHandler();

    /// <summary>
    /// Executes the action on the referenced cue light.
    /// </summary>
    public async Task ExecuteAsync(string cueNum = "") 
    {
        if (CueLight == null)
        {
            GD.Print($"CueLightComponent:ExecuteAsync - CueLight {CueLightId} not found", 2);
            return;
        }

        try
        {
            switch (Action)
            {
                case CueLightAction.Go: await CueLight.GoAsync(cueNum); break;
                case CueLightAction.Standby: await CueLight.StandbyAsync(cueNum); break;
                case CueLightAction.CountIn: await CueLight.CountInAsync(CountInTime, cueNum); break;
                case CueLightAction.IdentifyStart: await CueLight.IdentifyAsync(true); break;
                case CueLightAction.IdentifyStop: await CueLight.IdentifyAsync(false); break;
                case CueLightAction.Cancel: await CueLight.CancelAsync(); break;
            }
            
            GD.Print( 
                $"CueLightComponent:ExecuteAsync - Executed {Action} on {CueLightId}", 0);
        }
        catch (Exception ex)
        {
            GD.Print( 
                $"CueLightComponent:ExecuteAsync - Execution failed for {Action} on {CueLightId}: {ex.Message}", 2);
        }
    }

    public Dictionary GetData() 
    {
        return new Dictionary
        {
            { "CueLightId", CueLightId },
            { "Action", (int)Action },
            { "CountInTime", CountInTime }
        };
    }

    public void LoadFromData(Dictionary data) 
    {
        if (data.ContainsKey("CueLightId")) CueLightId = data["CueLightId"].AsInt32();
        if (data.ContainsKey("Action")) Action = (CueLightAction)data["Action"].AsInt32();
        if (data.ContainsKey("CountInTime")) CountInTime = data["CountInTime"].AsSingle();
    }
}