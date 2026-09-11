// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Cue2.Domain.Cues;

namespace Cue2.Domain.Cuelist;

/// <summary>
/// One cue in a GO continue/follow chain (topology only — timing is event-driven at runtime).
/// </summary>
public sealed class CueChainMember
{
	/// <summary>Cue to show / play.</summary>
	public Cue Cue { get; init; }

	/// <summary>
	/// How this cue was linked from the previous member.
	/// <see cref="FollowType.None"/> for the GO head.
	/// </summary>
	public FollowType IncomingMode { get; init; }

	/// <summary>
	/// Post-wait of the previous cue (lead-in after arm). Zero for the head.
	/// </summary>
	public double IncomingPostWait { get; init; }
}

/// <summary>
/// Builds continue/follow chains so every upcoming cue can be pre-spawned at GO.
/// </summary>
public static class CueSequencePlanner
{
	/// <summary>
	/// Walks auto-continue / auto-follow links from <paramref name="head"/> (including the head).
	/// Does not bake absolute times — follow waits for real content completion at runtime.
	/// </summary>
	/// <param name="head">Cue that was GO'd.</param>
	/// <returns>Ordered chain members from head through sequence end.</returns>
	public static List<CueChainMember> BuildChain(Cue head)
	{
		var result = new List<CueChainMember>();
		if (head == null) return result;

		var current = head;
		var incomingMode = FollowType.None;
		double incomingPostWait = 0.0;
		var guard = 0;

		while (current != null && guard++ < 10000)
		{
			result.Add(new CueChainMember
			{
				Cue = current,
				IncomingMode = incomingMode,
				IncomingPostWait = incomingPostWait
			});

			if (current.Follow == FollowType.None)
				break;

			var next = current.GetNextSiblingCue();
			if (next == null)
				break;

			// Next member is armed by this cue's continue/follow rules; post-wait is this cue's.
			incomingMode = current.Follow;
			incomingPostWait = System.Math.Max(0.0, current.PostWait);
			current = next;
		}

		return result;
	}
}
