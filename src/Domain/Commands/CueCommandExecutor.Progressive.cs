// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.UI.Utilities;

namespace Cue2.Domain.Commands;

/// <summary>
/// Partial: GO on several top-level cues at once (for example Select All, then GO). Each
/// top-level cue starts with its whole group and sequence as one unit, as a single GO does, but
/// the units open a few at a time, in list order, so the interface keeps running and the first
/// cues play while later ones are still opening.
/// </summary>
public partial class CueCommandExecutor
{
    /// <summary>Top-level cues opening their media at the same time.</summary>
    private const int ProgressiveStartParallel = 3;

    /// <summary>
    /// A unit that has not started after this long stops holding its place; the next one opens.
    /// It keeps opening and starts when it is ready.
    /// </summary>
    private const double ProgressiveSlotTimeoutSeconds = 10.0;

    /// <summary>Changed by Stop All, show open and New show: queued units of older starts are dropped.</summary>
    private int _progressiveGeneration;

    /// <summary>True after Pause All (until Resume All) during a progressive start: queued units wait.</summary>
    private bool _progressiveHeld;

    /// <summary>Progressive starts still queueing or opening.</summary>
    private int _progressiveRunsActive;

    // Footer progress over all progressive starts still running.
    private int _progressiveRunsWithProgress;
    private int _progressiveTotal;
    private int _progressiveDone;

    private void SetupProgressiveStart()
    {
        _globalSignals.StopAll += CancelProgressiveStarts;
        _globalSignals.PauseAll += OnProgressivePauseAll;
        _globalSignals.ResumeAll += OnProgressiveResumeAll;
        _globalSignals.SessionLoadStarted += OnProgressiveSessionLoadStarted;
        _globalSignals.NewSession += CancelProgressiveStarts;
    }

    private void TearDownProgressiveStart()
    {
        if (_globalSignals == null)
            return;
        _globalSignals.StopAll -= CancelProgressiveStarts;
        _globalSignals.PauseAll -= OnProgressivePauseAll;
        _globalSignals.ResumeAll -= OnProgressiveResumeAll;
        _globalSignals.SessionLoadStarted -= OnProgressiveSessionLoadStarted;
        _globalSignals.NewSession -= CancelProgressiveStarts;
        _progressiveGeneration++;
    }

    /// <summary>
    /// Units already opening are ordinary active cues and get Stop All like any other; the ones
    /// still queued are dropped here.
    /// </summary>
    private void CancelProgressiveStarts()
    {
        _progressiveGeneration++;
        _progressiveHeld = false;
    }

    private void OnProgressiveSessionLoadStarted(string showName) => CancelProgressiveStarts();

    private void OnProgressivePauseAll()
    {
        if (_progressiveRunsActive > 0)
            _progressiveHeld = true;
    }

    private void OnProgressiveResumeAll() => _progressiveHeld = false;

    /// <summary>
    /// The selected cues with no selected ancestor (a selected group already starts its
    /// children), in selection order, each once.
    /// </summary>
    internal static List<Cue> GetSelectionRoots(IReadOnlyList<Cue> cues)
    {
        var roots = new List<Cue>();
        if (cues == null || cues.Count == 0)
            return roots;

        var ids = new HashSet<int>();
        foreach (var cue in cues)
        {
            if (cue != null)
                ids.Add(cue.Id);
        }

        var seen = new HashSet<int>();
        foreach (var cue in cues)
        {
            if (cue == null || !seen.Add(cue.Id))
                continue;
            if (HasSelectedAncestor(cue, ids))
                continue;
            roots.Add(cue);
        }
        return roots;
    }

    private static bool HasSelectedAncestor(Cue cue, HashSet<int> ids)
    {
        int parentId = cue.ParentId;
        for (int depth = 0; depth < 64 && parentId >= 0; depth++)
        {
            if (ids.Contains(parentId))
                return true;
            var parent = CueList.FetchCueFromId(parentId);
            if (parent == null)
                break;
            parentId = parent.ParentId;
        }
        return false;
    }

    /// <summary>
    /// Starts each top-level cue as its own unit: up to <see cref="ProgressiveStartParallel"/>
    /// open at once, the rest wait for a free place and then join one per frame. A unit's
    /// sequence stops before the next selected top-level cue, which starts on its own.
    /// </summary>
    /// <param name="roots">Top-level cues from <see cref="GetSelectionRoots"/>, at least two.</param>
    private async Task StartRootsProgressivelyAsync(List<Cue> roots)
    {
        int generation = _progressiveGeneration;
        if (_progressiveRunsActive == 0)
            _progressiveHeld = false;
        _progressiveRunsActive++;
        bool showProgress = roots.Count > ProgressiveStartParallel;
        var rootIds = new HashSet<int>();
        foreach (var root in roots)
            rootIds.Add(root.Id);

        if (showProgress)
        {
            _progressiveRunsWithProgress++;
            _progressiveTotal += roots.Count;
            EmitProgressiveProgress();
        }
        GD.Print($"CueCommandExecutor:StartRootsProgressivelyAsync - starting {roots.Count} top-level cues, " +
                 $"{ProgressiveStartParallel} opening at a time");

        var opening = new List<Task>();
        int launched = 0;
        try
        {
            foreach (var root in roots)
            {
                while (opening.Count >= ProgressiveStartParallel)
                {
                    var finished = await Task.WhenAny(opening);
                    opening.Remove(finished);
                }

                // The first places fill in the GO frame, like a single GO; after that, at most
                // one new unit per frame, so its row and set-up work are spread out.
                if (launched >= ProgressiveStartParallel && IsInstanceValid(this) && IsInsideTree())
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                while (_progressiveHeld && generation == _progressiveGeneration && IsInstanceValid(this) && IsInsideTree())
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (generation != _progressiveGeneration || !IsInstanceValid(this) || !IsInsideTree())
                    break;

                opening.Add(StartRootUnitAsync(root, rootIds, showProgress));
                launched++;
            }

            if (launched < roots.Count)
            {
                GD.Print($"CueCommandExecutor:StartRootsProgressivelyAsync - start cancelled; {roots.Count - launched} " +
                         $"of {roots.Count} top-level cues were not started.");
                if (showProgress)
                {
                    _progressiveTotal -= roots.Count - launched;
                    EmitProgressiveProgress();
                }
            }

            if (opening.Count > 0)
                await Task.WhenAll(opening);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueCommandExecutor:StartRootsProgressivelyAsync - {ex.Message}");
        }
        finally
        {
            _progressiveRunsActive = Math.Max(0, _progressiveRunsActive - 1);
            if (_progressiveRunsActive == 0)
                _progressiveHeld = false;
            if (showProgress)
                FinishProgressiveRun();
        }
    }

    /// <summary>
    /// Starts one top-level cue and its sequence (the single GO path) and completes when it has
    /// started, or after <see cref="ProgressiveSlotTimeoutSeconds"/> while it keeps opening.
    /// </summary>
    private async Task StartRootUnitAsync(Cue root, ISet<int> rootIds, bool counted)
    {
        try
        {
            Task start = ActivateSequenceFromAsync(root, chainStopIds: rootIds);
            Task timeout = Task.Delay(TimeSpan.FromSeconds(ProgressiveSlotTimeoutSeconds));
            if (await Task.WhenAny(start, timeout) == start)
            {
                await start;
            }
            else
            {
                GD.Print($"CueCommandExecutor:StartRootUnitAsync - \"{root.Name}\" still opening after " +
                         $"{ProgressiveSlotTimeoutSeconds:F0} s; the next cues open meanwhile.");
                TaskUtil.FireAndForget(start, "CueCommandExecutor.ProgressiveStart");
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueCommandExecutor:StartRootUnitAsync - {root?.Name}: {ex.Message}");
        }
        finally
        {
            if (counted && _progressiveRunsWithProgress > 0)
            {
                _progressiveDone++;
                EmitProgressiveProgress();
            }
        }
    }

    private void EmitProgressiveProgress()
    {
        if (_globalSignals == null || _progressiveTotal <= 0)
            return;
        int done = Math.Min(_progressiveDone, _progressiveTotal);
        float percent = Math.Clamp(done * 100f / _progressiveTotal, 0f, 100f);
        _globalSignals.EmitSignal(nameof(GlobalSignals.BackgroundProcessProgress),
            percent, true, UiLocalizer.Tf("Starting cues {0}/{1}", done, _progressiveTotal), string.Empty, done, _progressiveTotal);
    }

    private void FinishProgressiveRun()
    {
        _progressiveRunsWithProgress = Math.Max(0, _progressiveRunsWithProgress - 1);
        if (_progressiveRunsWithProgress > 0)
            return;
        _progressiveTotal = 0;
        _progressiveDone = 0;
        _globalSignals?.EmitSignal(nameof(GlobalSignals.BackgroundProcessCompleted));
    }
}
