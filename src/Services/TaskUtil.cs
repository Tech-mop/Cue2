// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Helpers for running async work without <c>async void</c> (exceptions are logged, not lost).
/// </summary>
/// <remarks>
/// Prefer <c>async Task</c> methods and either <c>await</c> them or pass them here when the
/// call site cannot be async (Godot signals, C# event handlers, void public API).
/// Does not use <c>ConfigureAwait(false)</c> so Godot main-thread continuations stay intact.
/// </remarks>
public static class TaskUtil
{
	/// <summary>
	/// Observes <paramref name="task"/> and logs any fault without rethrowing on the caller.
	/// </summary>
	/// <param name="task">Task to observe (may be null or already completed).</param>
	/// <param name="context">Short label for log lines (e.g. class.method).</param>
	public static void FireAndForget(Task task, string context = null)
	{
		if (task == null)
			return;

		if (task.IsCompleted)
		{
			if (task.IsFaulted)
				LogException(task.Exception, context);
			return;
		}

		_ = ObserveAsync(task, context);
	}

	/// <summary>
	/// Starts <paramref name="work"/> and observes the resulting task (logs faults).
	/// Synchronous throw from <paramref name="work"/> is also logged.
	/// </summary>
	/// <param name="work">Async work factory.</param>
	/// <param name="context">Short label for log lines.</param>
	public static void Run(Func<Task> work, string context = null)
	{
		if (work == null)
			return;

		try
		{
			FireAndForget(work(), context);
		}
		catch (Exception ex)
		{
			LogException(ex, context);
		}
	}

	private static async Task ObserveAsync(Task task, string context)
	{
		try
		{
			await task;
		}
		catch (Exception ex)
		{
			LogException(ex, context);
		}
	}

	private static void LogException(Exception ex, string context)
	{
		if (ex == null)
			return;

		Exception flat = ex is AggregateException ae
			? ae.Flatten().InnerException ?? ae
			: ex;

		string where = string.IsNullOrEmpty(context) ? "TaskUtil" : $"TaskUtil:{context}";
		GD.PrintErr($"{where} - {flat.GetType().Name}: {flat.Message}");
	}
}
