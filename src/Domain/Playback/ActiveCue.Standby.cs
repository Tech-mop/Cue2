// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace Cue2.Domain.Playback;

/// <summary>
/// Partial: the automatic standby, the playhead cue opened ahead of GO with its row hidden.
/// </summary>
public partial class ActiveCue
{
    /// <summary>True for the automatic standby made for the playhead cue.</summary>
    private bool _isAutoStandby;

    /// <summary>True once <see cref="StartAsync"/> has been called (GO, or the parent started it).</summary>
    private bool _startRequested;

    /// <summary>Preload state for the start timing log ("preloaded", "not preloaded", ...). Empty = not logged.</summary>
    private string _preloadNote = string.Empty;

    /// <summary>True when the media components have finished setting up.</summary>
    public bool ComponentsReady => _componentsSetup && !_isCleaned;

    /// <summary>Sets the preload state shown in this cue's start timing line.</summary>
    public void SetPreloadNote(string note) => _preloadNote = note ?? string.Empty;

    /// <summary>
    /// True while this cue, or the group it is preloaded under, is an automatic standby that
    /// has not been started. Such a cue ignores Pause All and Resume All, so a GO after a
    /// Pause All does not start it paused, and nothing plays before GO.
    /// </summary>
    private bool IsWaitingAutoStandby
    {
        get
        {
            ActiveCue node = this;
            for (int i = 0; i < 64 && node != null; i++)
            {
                if (node._startRequested)
                    return false;
                if (node._isAutoStandby)
                    return true;
                node = node.ParentActive;
            }
            return false;
        }
    }

    /// <summary>
    /// Opens this cue's media (and its children's) without starting it, with its row hidden
    /// until GO. Nothing is heard or seen: video stays transparent and sound is not bound.
    /// </summary>
    /// <returns>True when every component finished setting up and the cue is still waiting.</returns>
    public async Task<bool> PreloadAsAutoStandbyAsync()
    {
        if (_isCleaned || _contentStarted || _startRequested)
            return false;

        _isAutoStandby = true;
        if (!_uiPrepared)
            PrepareUiInOrder();
        if (_activeCueBar != null && IsInstanceValid(_activeCueBar))
        {
            _activeCueBar.Visible = false;
            _hiddenUntilStart = true;
        }
        _standbyPreload = true;

        if (_cue == null || !_cue.Armed)
            return false;

        try
        {
            await EnsureComponentsSetupAsync();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:PreloadAsAutoStandbyAsync - {_cue?.Name}: {ex.Message}");
            return false;
        }

        return IsInstanceValid(this) && !_isCleaned && !_contentStarted && !_startRequested && _componentsSetup;
    }

    /// <summary>
    /// Called when GO takes over the automatic standby: its row moves to the end of the active
    /// list (where a new row would go) and its pictures and text, and those of its preloaded
    /// children, are restacked on top as if they had just been created.
    /// </summary>
    public void PromoteAutoStandby()
    {
        if (_isCleaned)
            return;

        if (_activeCueBar != null && IsInstanceValid(_activeCueBar))
        {
            var parent = _activeCueBar.GetParent();
            if (parent != null && IsInstanceValid(parent))
                parent.MoveChild(_activeCueBar, parent.GetChildCount() - 1);
        }

        var tree = new List<ActiveCue>();
        CollectPreloadTree(tree, 0);
        foreach (var active in tree.OrderBy(a => a._layerStackOrder))
            active.RenewLayerStackOrder();

        // Layer and screen layout edits made while it waited reached its layer hosts as they
        // happened. Apply the current layout once more so it never starts with an old one.
        var outputs = Cue2.Services.DisplaysManager.Outputs;
        if (outputs != null)
        {
            foreach (var output in outputs)
            {
                if (output != null && IsInstanceValid(output))
                    output.UpdateAllLayerDisplayRects();
            }
        }
    }

    private void CollectPreloadTree(List<ActiveCue> into, int depth)
    {
        if (depth > 64 || _isCleaned)
            return;
        into.Add(this);
        foreach (var child in _preloadedChildCues.Values)
        {
            if (child != null && IsInstanceValid(child))
                child.CollectPreloadTree(into, depth + 1);
        }
    }

    private void RenewLayerStackOrder()
    {
        _layerStackOrder = Cue2.Domain.Devices.VideoOutputDevice.ReserveLayerStackOrder();
        foreach (var playback in _activeVideoComponents.Values)
        {
            if (playback != null && IsInstanceValid(playback))
                playback.Restack(_layerStackOrder);
        }
        foreach (var playback in _activeTextComponents.Values)
        {
            if (playback != null && IsInstanceValid(playback))
                playback.Restack(_layerStackOrder);
        }
    }
}
