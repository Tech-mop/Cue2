// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Cues;

namespace Cue2.Tests.Cues;

/// <summary>
/// Builds small cue trees in the static cue index and restores the index afterwards.
/// </summary>
internal sealed class CueTreeFixture : System.IDisposable
{
    private readonly Dictionary<int, Cue> _previous;

    /// <summary>Top-level order, as a cue list keeps it.</summary>
    public List<int> RootOrder { get; } = new();

    public CueTreeFixture()
    {
        _previous = CueList.CueIndex;
        CueList.CueIndex = new Dictionary<int, Cue>();
    }

    /// <summary>Adds a top-level cue.</summary>
    public Cue Root(FollowType follow = FollowType.None, double postWait = 0)
    {
        var cue = Make(follow, postWait);
        RootOrder.Add(cue.Id);
        return cue;
    }

    /// <summary>Adds a child at the end of <paramref name="parent"/>.</summary>
    public Cue Child(Cue parent, FollowType follow = FollowType.None, double postWait = 0)
    {
        var cue = Make(follow, postWait);
        cue.ParentId = parent.Id;
        parent.ChildCues.Add(cue.Id);
        return cue;
    }

    /// <summary>Next top-level cue, the way the owning cue list finds it.</summary>
    public Cue NextRoot(Cue cue)
    {
        if (cue.ParentId >= 0)
            return cue.GetNextSiblingCue();
        int index = RootOrder.IndexOf(cue.Id);
        return index < 0 || index + 1 >= RootOrder.Count ? null : CueList.FetchCueFromId(RootOrder[index + 1]);
    }

    private static Cue Make(FollowType follow, double postWait)
    {
        var cue = new Cue { Follow = follow, PostWait = postWait };
        CueList.CueIndex[cue.Id] = cue;
        return cue;
    }

    public void Dispose() => CueList.CueIndex = _previous;
}
