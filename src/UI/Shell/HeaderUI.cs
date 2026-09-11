// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Threading.Tasks;
using System.Linq;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;
using AppSettings = Cue2.Domain.ShowSettings.Settings;
using static Cue2.UI.Utilities.UiLocalizer;

namespace Cue2.UI.Shell;

/// <summary>
/// Main header: GO button and standby cue name/notes for the next cue(s) to play.
/// </summary>
/// <remarks>
/// Standby reflects the current shell selection (what GO will trigger). A single
/// selected cue shows its number, name, and notes; multiple selection is summarized.
/// Go scale 0 ("No Go") hides this entire header; half scale hides the notes field only.
/// The GO hotkey still works when the header is hidden.
/// </remarks>
public partial class HeaderUI : Control
{
	private GlobalSignals _globalSignals;
	private GlobalData _globalData;

	private Node _settingsWindow;
	private Button _goButton;
	private LineEdit _standbyCueText;
	private LineEdit _standbyCueNote;

	/// <summary>Cue whose Name/CueNum/Notes events we are currently subscribed to (single-select only).</summary>
	private Cue _trackedStandbyCue;

	private double _baseGoSize;
	private GoCooldownOverlay _goCooldownOverlay;

	public override void _Ready()
	{
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
		_globalData = GetNodeOrNull<GlobalData>("/root/GlobalData");

		_goButton = GetNode<Button>("%GoButton");
		_standbyCueText = GetNodeOrNull<LineEdit>("%StandbyCueText");
		_standbyCueNote = GetNodeOrNull<LineEdit>("%StandbyCueNote");

		_baseGoSize = _goButton.GetSize().X;
		if (_baseGoSize <= 0)
			_baseGoSize = 50.0;

		_goCooldownOverlay = new GoCooldownOverlay();
		_goButton.AddChild(_goCooldownOverlay);
		_goCooldownOverlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

		_goButton.Pressed += () => _globalSignals.EmitSignal(nameof(GlobalSignals.Go));

		SyncHotkeys();

		_globalSignals.Go += GoButtonFeedback;
		_globalSignals.GoDisabled += OnGoDisabled;
		_globalSignals.GoEnabled += OnGoEnabled;
		_globalSignals.GoScaleChanged += GoScaleChange;
		_globalSignals.ShellFocused += OnShellFocused;
		_globalSignals.UpdateShellBar += OnUpdateShellBar;
		_globalSignals.SyncShellInspector += RefreshStandbyDisplay;

		// Apply saved / current scale (signal may have fired before this node was ready).
		float initialScale = _globalData?.Settings?.GoScale ?? 1.0f;
		GoScaleChange(initialScale);

		RefreshStandbyDisplay();
		LocalizeTree(this);
		if (_globalSignals != null)
			_globalSignals.LocaleChanged += OnLocaleChanged;

		ApplyGoGateVisual(_globalSignals != null && !_globalSignals.IsGoEnabled);
	}

	public override void _ExitTree()
	{
		DetachTrackedStandbyCue();

		if (_globalSignals != null)
		{
			_globalSignals.Go -= GoButtonFeedback;
			_globalSignals.GoDisabled -= OnGoDisabled;
			_globalSignals.GoEnabled -= OnGoEnabled;
			_globalSignals.GoScaleChanged -= GoScaleChange;
			_globalSignals.ShellFocused -= OnShellFocused;
			_globalSignals.UpdateShellBar -= OnUpdateShellBar;
			_globalSignals.SyncShellInspector -= RefreshStandbyDisplay;
			_globalSignals.LocaleChanged -= OnLocaleChanged;
		}

		base._ExitTree();
	}

	/// <summary>
	/// Re-localizes header chrome when the application language changes.
	/// </summary>
	/// <param name="localeCode">New locale code.</param>
	private void OnLocaleChanged(string localeCode)
	{
		if (!GodotObject.IsInstanceValid(this))
			return;
		LocalizeTree(this);
		SyncHotkeys();
		ApplyGoGateVisual(_globalSignals != null && !_globalSignals.IsGoEnabled);
	}

	private void SyncHotkeys()
	{
		_goButton.TooltipText = Tf("Hotkey: {0}", GlobalData.ParseHotkey("Go"));
	}

	private void OnGoDisabled(string reason, float durationSeconds)
	{
		ApplyGoGateVisual(disabled: true);
		SetProcess(durationSeconds > 0.0001f);
		if (_goCooldownOverlay != null)
		{
			_goCooldownOverlay.RemainingFraction = durationSeconds > 0.0001f ? 1f : 1f;
			_goCooldownOverlay.QueueRedraw();
		}
	}

	private void OnGoEnabled()
	{
		SetProcess(false);
		ApplyGoGateVisual(disabled: false);
	}

	/// <summary>
	/// Greys the GO button and shows a danger-coloured cooldown border while GO is gated.
	/// </summary>
	private void ApplyGoGateVisual(bool disabled)
	{
		if (_goButton == null || !IsInstanceValid(_goButton))
			return;

		_goButton.Disabled = disabled;
		if (_goCooldownOverlay != null)
		{
			_goCooldownOverlay.Active = disabled;
			_goCooldownOverlay.RemainingFraction = disabled ? 1f : 0f;
			_goCooldownOverlay.QueueRedraw();
		}

		if (disabled)
		{
			if (_globalSignals != null &&
			    _globalSignals.IsGoDisabledBy(GlobalSignals.GoDisableReasonSessionLoad))
			{
				_goButton.TooltipText = T("GO disabled — showfile is still loading.");
			}
			else
			{
				float sec = _globalSignals?.GoDisableDurationSeconds ?? 0f;
				_goButton.TooltipText = sec > 0.0001f
					? Tf("GO disabled — double-go protection ({0:0.#}s)", sec)
					: T("GO disabled");
			}
		}
		else
		{
			SyncHotkeys();
		}
	}

	public override void _Process(double delta)
	{
		if (_goCooldownOverlay == null || !_goCooldownOverlay.Active || _globalSignals == null)
			return;

		float total = _globalSignals.GoDisableDurationSeconds;
		if (total <= 0.0001f)
		{
			_goCooldownOverlay.RemainingFraction = 1f;
			_goCooldownOverlay.QueueRedraw();
			return;
		}

		_goCooldownOverlay.RemainingFraction = Mathf.Clamp(
			_globalSignals.GoDisableRemainingSeconds / total, 0f, 1f);
		_goCooldownOverlay.QueueRedraw();
	}

	private void GoButtonFeedback()
	{
		if (_globalSignals != null && !_globalSignals.IsGoEnabled)
			return;
		TaskUtil.Run(GoButtonFeedbackAsync, "HeaderUI.GoButtonFeedback");
	}

	private async Task GoButtonFeedbackAsync()
	{
		var pressed = _goButton.GetThemeStylebox("pressed");
		var normal = _goButton.GetThemeStylebox("normal");
		_goButton.AddThemeStyleboxOverride("normal", pressed);
		await ToSignal(GetTree().CreateTimer(0.2), "timeout");
		_goButton.AddThemeStyleboxOverride("normal", normal);
	}

	/// <summary>
	/// Applies Go button scale from settings.
	/// Scale 0 hides the whole header; 0.5 hides notes and uses a compact height.
	/// </summary>
	/// <param name="scale">Relative GO size (0 = No Go, 0.5 = Half Go, 1 = base, etc.).</param>
	private void GoScaleChange(float scale)
	{
		if (!IsInstanceValid(this))
			return;

		// No Go: hide the entire header (hotkey GO still works via InputMap).
		if (scale <= AppSettings.GoScaleNoGo || Mathf.IsEqualApprox(scale, AppSettings.GoScaleNoGo))
		{
			Visible = false;
			SetCustomMinimumSize(new Vector2(GetCustomMinimumSize().X, 0));
			return;
		}

		Visible = true;

		bool halfSize = Mathf.IsEqualApprox(scale, AppSettings.GoScaleHalf);
		if (_standbyCueNote != null)
			_standbyCueNote.Visible = !halfSize;

		var newGoScale = (float)(_baseGoSize * scale);
		if (newGoScale < 1f)
			newGoScale = 1f;

		_goButton.SetCustomMinimumSize(new Vector2(newGoScale, newGoScale));
		_goButton.Visible = true;

		// Header height: track GO button when large; compact when notes are hidden; else base 50.
		float headerHeight;
		if (newGoScale > 50f)
			headerHeight = newGoScale;
		else if (halfSize)
			headerHeight = Mathf.Max(newGoScale, 28f);
		else
			headerHeight = 50.0f;

		SetCustomMinimumSize(new Vector2(GetCustomMinimumSize().X, headerHeight));
	}

	private void OnShellFocused(int cueId)
	{
		RefreshStandbyDisplay();
	}

	/// <summary>
	/// Shell bar edits (name/number) may not re-fire ShellFocused; refresh when the
	/// updated bar belongs to the current standby selection.
	/// </summary>
	private void OnUpdateShellBar(int cueId)
	{
		var selected = ShellSelection.SelectedCues;
		if (selected == null || selected.Count == 0)
			return;
		if (selected.Any(c => c != null && c.Id == cueId))
			RefreshStandbyDisplay();
	}

	/// <summary>
	/// Updates standby name/notes from the current shell selection.
	/// </summary>
	private void RefreshStandbyDisplay()
	{
		if (!IsInstanceValid(this))
			return;

		var selected = ShellSelection.SelectedCues?
			.Where(c => c != null)
			.ToList();

		if (selected == null || selected.Count == 0)
		{
			DetachTrackedStandbyCue();
			SetStandbyFields(string.Empty, string.Empty, "No cue selected.", "Notes");
			return;
		}

		if (selected.Count > 1)
		{
			DetachTrackedStandbyCue();
			string multi = $"Multiple cues selected ({selected.Count})";
			SetStandbyFields(multi, string.Empty, multi, "Multiple cues selected — notes not shown.");
			return;
		}

		var cue = selected[0];
		AttachTrackedStandbyCue(cue);
		ApplySingleCueStandby(cue);
	}

	private void ApplySingleCueStandby(Cue cue)
	{
		if (cue == null)
		{
			SetStandbyFields(string.Empty, string.Empty, "No cue selected.", "Notes");
			return;
		}

		string num = cue.CueNum ?? string.Empty;
		string name = cue.Name ?? string.Empty;
		string display = string.IsNullOrWhiteSpace(num)
			? name
			: string.IsNullOrWhiteSpace(name)
				? num
				: $"{num}  {name}";

		string notes = FlattenNotes(cue.Notes);
		string tipName = string.IsNullOrEmpty(display)
			? "Standby cue (next to play)."
			: $"Standby: {display}";
		string tipNotes = string.IsNullOrEmpty(cue.Notes)
			? "No notes for this cue."
			: cue.Notes;

		SetStandbyFields(display, notes, tipName, tipNotes);
	}

	private void SetStandbyFields(string nameText, string notesText, string nameTooltip, string notesTooltip)
	{
		if (_standbyCueText != null)
		{
			_standbyCueText.Text = nameText ?? string.Empty;
			_standbyCueText.TooltipText = nameTooltip ?? string.Empty;
		}

		if (_standbyCueNote != null)
		{
			_standbyCueNote.Text = notesText ?? string.Empty;
			_standbyCueNote.TooltipText = notesTooltip ?? string.Empty;
		}
	}

	/// <summary>
	/// Collapses multi-line notes to a single line for the header LineEdit.
	/// </summary>
	private static string FlattenNotes(string notes)
	{
		if (string.IsNullOrEmpty(notes))
			return string.Empty;
		return notes
			.Replace("\r\n", " ")
			.Replace('\n', ' ')
			.Replace('\r', ' ')
			.Trim();
	}

	private void AttachTrackedStandbyCue(Cue cue)
	{
		if (ReferenceEquals(_trackedStandbyCue, cue))
			return;

		DetachTrackedStandbyCue();
		_trackedStandbyCue = cue;
		if (_trackedStandbyCue == null)
			return;

		_trackedStandbyCue.NameChanged += OnTrackedStandbyNameOrNumChanged;
		_trackedStandbyCue.CueNumChanged += OnTrackedStandbyNameOrNumChanged;
		_trackedStandbyCue.NotesChanged += OnTrackedStandbyNotesChanged;
	}

	private void DetachTrackedStandbyCue()
	{
		if (_trackedStandbyCue == null)
			return;

		_trackedStandbyCue.NameChanged -= OnTrackedStandbyNameOrNumChanged;
		_trackedStandbyCue.CueNumChanged -= OnTrackedStandbyNameOrNumChanged;
		_trackedStandbyCue.NotesChanged -= OnTrackedStandbyNotesChanged;
		_trackedStandbyCue = null;
	}

	private void OnTrackedStandbyNameOrNumChanged(string _)
	{
		if (_trackedStandbyCue != null)
			ApplySingleCueStandby(_trackedStandbyCue);
	}

	private void OnTrackedStandbyNotesChanged(string _)
	{
		if (_trackedStandbyCue != null)
			ApplySingleCueStandby(_trackedStandbyCue);
	}

	/// <summary>
	/// Danger-coloured border on the GO button that depletes like a cooldown ring.
	/// Stroke matches the GO theme (a few pixels, rounded corners when the button has them).
	/// </summary>
	private partial class GoCooldownOverlay : Control
	{
		/// <summary>Stroke width in pixels — matches the GO theme border.</summary>
		private const float BorderWidth = 2f;

		/// <summary>1 = full border (just disabled), 0 = empty (about to re-enable).</summary>
		public float RemainingFraction { get; set; } = 1f;

		/// <summary>When false, nothing is drawn.</summary>
		public bool Active { get; set; }

		public override void _Ready()
		{
			MouseFilter = MouseFilterEnum.Ignore;
			SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
		}

		public override void _Draw()
		{
			if (!Active)
				return;

			Vector2 size = Size;
			if (size.X < 4f || size.Y < 4f)
				return;

			Color danger = GlobalStyles.Danger;
			float inset = BorderWidth * 0.5f;
			var rect = new Rect2(inset, inset, size.X - BorderWidth, size.Y - BorderWidth);
			float radius = ReadCornerRadius();
			radius = Mathf.Min(radius, Mathf.Min(rect.Size.X, rect.Size.Y) * 0.5f);

			var track = new Color(danger.R, danger.G, danger.B, 0.22f);
			DrawRoundedPerimeter(rect, radius, track, 1f);

			float fraction = Mathf.Clamp(RemainingFraction, 0f, 1f);
			if (fraction <= 0.001f)
				return;

			DrawRoundedPerimeter(rect, radius, danger, fraction);
		}

		/// <summary>Reads the GO button StyleBox corner radius so the ring follows the theme.</summary>
		private float ReadCornerRadius()
		{
			if (GetParent() is not Button button)
				return 0f;
			if (button.GetThemeStylebox("normal") is not StyleBoxFlat flat)
				return 0f;

			float r = Mathf.Max(
				Mathf.Max(flat.CornerRadiusTopLeft, flat.CornerRadiusTopRight),
				Mathf.Max(flat.CornerRadiusBottomLeft, flat.CornerRadiusBottomRight));
			// Path is inset by half the stroke; keep the visual outer radius on the button edge.
			return Mathf.Max(0f, r - BorderWidth * 0.5f);
		}

		/// <summary>
		/// Clockwise from the top-left corner: draws <paramref name="fraction"/> of a rounded rect.
		/// </summary>
		private void DrawRoundedPerimeter(Rect2 rect, float radius, Color color, float fraction)
		{
			float w = rect.Size.X;
			float h = rect.Size.Y;
			if (w <= 0f || h <= 0f)
				return;

			float r = Mathf.Clamp(radius, 0f, Mathf.Min(w, h) * 0.5f);
			float straightW = Mathf.Max(0f, w - 2f * r);
			float straightH = Mathf.Max(0f, h - 2f * r);
			float arcLen = r > 0.01f ? Mathf.Tau * r * 0.25f : 0f;
			float perim = 2f * (straightW + straightH) + 4f * arcLen;
			if (perim <= 0f)
				return;

			float remaining = perim * Mathf.Clamp(fraction, 0f, 1f);
			float x = rect.Position.X;
			float y = rect.Position.Y;

			// Clockwise from top-left: TL arc → top → TR arc → right → BR arc → bottom → BL arc → left.
			if (r > 0.01f)
				ConsumeArc(new Vector2(x + r, y + r), r, Mathf.Pi, Mathf.Pi * 1.5f, ref remaining, color);
			ConsumeLine(new Vector2(x + r, y), new Vector2(x + w - r, y), straightW, ref remaining, color);
			if (r > 0.01f)
				ConsumeArc(new Vector2(x + w - r, y + r), r, Mathf.Pi * 1.5f, Mathf.Tau, ref remaining, color);
			ConsumeLine(new Vector2(x + w, y + r), new Vector2(x + w, y + h - r), straightH, ref remaining, color);
			if (r > 0.01f)
				ConsumeArc(new Vector2(x + w - r, y + h - r), r, 0f, Mathf.Pi * 0.5f, ref remaining, color);
			ConsumeLine(new Vector2(x + w - r, y + h), new Vector2(x + r, y + h), straightW, ref remaining, color);
			if (r > 0.01f)
				ConsumeArc(new Vector2(x + r, y + h - r), r, Mathf.Pi * 0.5f, Mathf.Pi, ref remaining, color);
			ConsumeLine(new Vector2(x, y + h - r), new Vector2(x, y + r), straightH, ref remaining, color);
		}

		private void ConsumeLine(Vector2 from, Vector2 to, float edgeLen, ref float remaining, Color color)
		{
			if (remaining <= 0f || edgeLen <= 0.01f)
				return;

			if (remaining >= edgeLen)
			{
				DrawLine(from, to, color, BorderWidth, antialiased: true);
				remaining -= edgeLen;
				return;
			}

			Vector2 dir = (to - from) / edgeLen;
			DrawLine(from, from + dir * remaining, color, BorderWidth, antialiased: true);
			remaining = 0f;
		}

		private void ConsumeArc(Vector2 center, float radius, float startAngle, float endAngle,
			ref float remaining, Color color)
		{
			if (remaining <= 0f || radius <= 0.01f)
				return;

			float sweep = endAngle - startAngle;
			if (sweep < 0f)
				sweep += Mathf.Tau;
			float arcLen = radius * sweep;
			if (arcLen <= 0.01f)
				return;

			float drawSweep = remaining >= arcLen ? sweep : remaining / radius;
			int points = Mathf.Max(4, (int)Mathf.Ceil(radius * drawSweep * 2f));
			DrawArc(center, radius, startAngle, startAngle + drawSweep, points, color,
				BorderWidth, antialiased: true);
			remaining = remaining >= arcLen ? remaining - arcLen : 0f;
		}
	}
}
