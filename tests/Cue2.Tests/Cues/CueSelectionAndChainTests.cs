// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Cue2.Domain.Commands;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Cues;

namespace Cue2.Tests.Cues;

/// <summary>
/// Which selected cues GO starts, and which cues each GO chains into.
/// </summary>
[TestFixture]
public class CueSelectionAndChainTests
{
    /// <summary>A selected group already starts its children, so they are not started twice.</summary>
    [Test]
    public void SelectedParentWinsOverItsChildren()
    {
        using var tree = new CueTreeFixture();
        var group = tree.Root();
        var child = tree.Child(group);
        var grandChild = tree.Child(child);
        var other = tree.Root();

        var roots = CueCommandExecutor.GetSelectionRoots(new List<Cue> { grandChild, child, group, other, group });
        Assert.That(roots, Is.EqualTo(new List<Cue> { group, other }));
    }

    /// <summary>Children of different groups are each roots; order and duplicates follow the selection.</summary>
    [Test]
    public void MixedLevelsKeepSelectionOrder()
    {
        using var tree = new CueTreeFixture();
        var a = tree.Root();
        var b = tree.Root();
        var a1 = tree.Child(a);
        var b1 = tree.Child(b);

        var roots = CueCommandExecutor.GetSelectionRoots(new List<Cue> { b1, null, a1, b1 });
        Assert.That(roots, Is.EqualTo(new List<Cue> { b1, a1 }));
        Assert.That(CueCommandExecutor.GetSelectionRoots(new List<Cue>()), Is.Empty);
        Assert.That(CueCommandExecutor.GetSelectionRoots(null), Is.Empty);
    }

    /// <summary>Auto-continue and auto-follow chain inside a group, each link remembering its mode and post-wait.</summary>
    [Test]
    public void ChildChainFollowsLinks()
    {
        using var tree = new CueTreeFixture();
        var group = tree.Root();
        var c1 = tree.Child(group, FollowType.Continue, postWait: 1.5);
        var c2 = tree.Child(group, FollowType.Follow, postWait: -2);
        var c3 = tree.Child(group);
        tree.Child(group);

        var chain = CueSequencePlanner.BuildChain(c1);
        Assert.That(chain.Select(m => m.Cue), Is.EqualTo(new[] { c1, c2, c3 }));
        Assert.That(chain.Select(m => m.IncomingMode), Is.EqualTo(new[] { FollowType.None, FollowType.Continue, FollowType.Follow }));
        Assert.That(chain.Select(m => m.IncomingPostWait), Is.EqualTo(new[] { 0.0, 1.5, 0.0 }), "negative post-wait counts as zero");
    }

    /// <summary>Top-level chains work through the sibling lookup.</summary>
    [Test]
    public void TopLevelChain()
    {
        using var tree = new CueTreeFixture();
        var r1 = tree.Root(FollowType.Continue);
        var r2 = tree.Root(FollowType.Continue);
        var r3 = tree.Root();
        tree.Root();

        var chain = CueSequencePlanner.BuildChain(r1, null, tree.NextRoot);
        Assert.That(chain.Select(m => m.Cue), Is.EqualTo(new[] { r1, r2, r3 }));
    }

    /// <summary>A chain ends at the last cue of its level, even when that cue continues.</summary>
    [Test]
    public void ChainStopsAtEndOfLevel()
    {
        using var tree = new CueTreeFixture();
        var group = tree.Root();
        var c1 = tree.Child(group, FollowType.Continue);
        var c2 = tree.Child(group, FollowType.Continue);
        tree.Root();

        Assert.That(CueSequencePlanner.BuildChain(c1).Select(m => m.Cue), Is.EqualTo(new[] { c1, c2 }), "does not leave the group");
    }

    /// <summary>Multi-select GO: a unit's chain stops before the next selected cue, which starts on its own.</summary>
    [Test]
    public void ChainStopsBeforeOtherSelectedCues()
    {
        using var tree = new CueTreeFixture();
        var r1 = tree.Root(FollowType.Continue);
        var r2 = tree.Root(FollowType.Continue);
        var r3 = tree.Root(FollowType.Continue);
        tree.Root();

        var stop = new HashSet<int> { r1.Id, r3.Id };
        Assert.That(CueSequencePlanner.BuildChain(r1, stop, tree.NextRoot).Select(m => m.Cue), Is.EqualTo(new[] { r1, r2 }));
        Assert.That(CueSequencePlanner.BuildChain(r3, stop, tree.NextRoot).Count, Is.EqualTo(2), "the head itself is never a stop");
    }

    /// <summary>A disarmed cue in the middle stays in the chain; skipping it is decided when it plays.</summary>
    [Test]
    public void DisarmedCueStaysInChain()
    {
        using var tree = new CueTreeFixture();
        var r1 = tree.Root(FollowType.Continue);
        var r2 = tree.Root(FollowType.Continue);
        r2.Armed = false;
        var r3 = tree.Root();

        Assert.That(CueSequencePlanner.BuildChain(r1, null, tree.NextRoot).Select(m => m.Cue), Is.EqualTo(new[] { r1, r2, r3 }));
    }

    /// <summary>A sibling lookup that loops back is cut off instead of hanging.</summary>
    [Test]
    public void LoopingLookupTerminates()
    {
        using var tree = new CueTreeFixture();
        var a = tree.Root(FollowType.Continue);
        var b = tree.Root(FollowType.Continue);
        var chain = CueSequencePlanner.BuildChain(a, null, c => c == a ? b : a);
        Assert.That(chain.Count, Is.LessThanOrEqualTo(10001));
        Assert.That(CueSequencePlanner.BuildChain(null), Is.Empty);
    }

    /// <summary>A group whose child list loops back to itself does not hang standby counting.</summary>
    [Test]
    public void TreeCountIsBoundedForLoopedGroups()
    {
        using var tree = new CueTreeFixture();
        var group = tree.Root();
        tree.Child(group);
        tree.Child(group);
        Assert.That(CueCommandExecutor.CountTreeCues(group, 100), Is.EqualTo(3));

        group.ChildCues.Add(group.Id);
        Assert.That(CueCommandExecutor.CountTreeCues(group, 50), Is.EqualTo(50), "stops at the limit");
    }

    /// <summary>Standby keys match on the state id line only.</summary>
    [Test]
    public void StandbyStateIdCompare()
    {
        Assert.That(CueCommandExecutor.SameStateId("abc\ncue 1", "abc\ncue 2"), Is.True);
        Assert.That(CueCommandExecutor.SameStateId("abc\ncue 1", "abd\ncue 1"), Is.False);
        Assert.That(CueCommandExecutor.SameStateId("abc", "abc"), Is.False, "no state line");
        Assert.That(CueCommandExecutor.SameStateId(null, "abc\n"), Is.False);
        Assert.That(CueCommandExecutor.SameStateId("\nx", "\nx"), Is.False, "empty state id");
    }

    /// <summary>The playhead skips disarmed cues marked to skip, walking out of groups.</summary>
    [Test]
    public void PlayheadSkipsDisarmedCues()
    {
        using var tree = new CueTreeFixture();
        var group = tree.Root();
        var c1 = tree.Child(group);
        var c2 = tree.Child(group);
        c1.Armed = false;
        c1.SkipIfDisarmed = true;
        c2.Armed = false;
        c2.SkipIfDisarmed = false;

        Assert.That(Cue.ResolvePlayheadTarget(c1), Is.SameAs(c2));
        c2.SkipIfDisarmed = true;
        Assert.That(Cue.ResolvePlayheadTarget(c1), Is.Null, "nothing left in the group and no owning list in tests");
    }

    /// <summary>New cues get unique ids, and the allocator restarts for a new session.</summary>
    [Test]
    public void CueIdsAreUnique()
    {
        var ids = new HashSet<int>();
        for (int i = 0; i < 100; i++)
            Assert.That(ids.Add(new Cue().Id), Is.True);

        Cue.ResetIdAllocator();
        Assert.That(new Cue().Id, Is.EqualTo(0));
        Assert.That(new Cue().Id, Is.EqualTo(1));
    }
}
