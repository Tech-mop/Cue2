// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Shell;

/// <summary>
/// Shared floating card + insert line for cue moves (cuelist reorder and button-board drag).
/// Parent to <c>/root/Cue2Base</c>, never a TabContainer.
/// </summary>
internal sealed class CueMovePreview
{
    private readonly Control _root;
    private readonly PanelContainer _card;
    private readonly Label _locationLabel;
    private readonly VBoxContainer _namesBox;
    private readonly Panel _dropLine;
    private readonly StyleBoxFlat _cardStyle;
    private readonly StyleBoxFlat _lineStyle;
    private bool _attached;

    /// <summary>True while a move preview is showing.</summary>
    public bool IsVisible => _root != null && GodotObject.IsInstanceValid(_root) && _root.Visible;

    /// <summary>Builds the overlay widgets. Call <see cref="Attach"/> before <see cref="Begin"/>.</summary>
    public CueMovePreview()
    {
        _cardStyle = new StyleBoxFlat();
        _cardStyle.BgColor = new Color(0.10f, 0.12f, 0.14f, 0.94f);
        _cardStyle.SetCornerRadiusAll(4);
        _cardStyle.SetBorderWidthAll(1);
        _cardStyle.BorderColor = GlobalStyles.HighColor1;
        _cardStyle.ContentMarginLeft = 8;
        _cardStyle.ContentMarginRight = 8;
        _cardStyle.ContentMarginTop = 6;
        _cardStyle.ContentMarginBottom = 6;

        _lineStyle = new StyleBoxFlat();
        _lineStyle.BgColor = new Color(0.59f, 0.29f, 0.03f, 1f);

        _root = new Control
        {
            Name = "CueMovePreview",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
            ZIndex = 128
        };

        _card = new PanelContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _card.AddThemeStyleboxOverride("panel", _cardStyle);
        _root.AddChild(_card);

        var box = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddThemeConstantOverride("separation", 4);
        _card.AddChild(box);

        _locationLabel = new Label
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            AutowrapMode = TextServer.AutowrapMode.Off
        };
        _locationLabel.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(_locationLabel);

        _namesBox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        box.AddChild(_namesBox);

        _dropLine = new Panel
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false,
            ZIndex = 127
        };
        _dropLine.AddThemeStyleboxOverride("panel", _lineStyle);
        _root.AddChild(_dropLine);
    }

    /// <summary>Parents the overlay under <paramref name="host"/> (Cue2Base).</summary>
    public void Attach(Node host)
    {
        if (_attached || host == null || !GodotObject.IsInstanceValid(host))
            return;
        if (_root.GetParent() == host)
            return;
        _root.GetParent()?.RemoveChild(_root);
        host.AddChild(_root);

        _attached = true;
    }

    /// <summary>Shows the card with the cues being moved.</summary>
    public void Begin(IEnumerable<Cue> cues)
    {
        ClearNames();
        if (cues != null)
        {
            foreach (var cue in cues)
            {
                if (cue == null)
                    continue;
                var label = new Label
                {
                    Text = string.IsNullOrWhiteSpace(cue.Name) ? cue.CueNum : cue.Name,
                    MouseFilter = Control.MouseFilterEnum.Ignore
                };
                label.AddThemeFontSizeOverride("font_size", 9);
                label.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.7f, 0.85f));
                _namesBox.AddChild(label);
            }
        }

        _locationLabel.Text = UiLocalizer.T("Move cue");
        _root.Visible = true;
        _dropLine.Visible = false;
        _root.MoveToFront();
    }

    /// <summary>Hides the card and insert line.</summary>
    public void End()
    {
        _root.Visible = false;
        _dropLine.Visible = false;
        ClearNames();
    }

    /// <summary>Keeps the card under the pointer.</summary>
    public void Follow(Vector2 globalPos)
    {
        if (!_root.Visible)
            return;
        _root.GlobalPosition = Vector2.Zero;
        _card.GlobalPosition = globalPos;
        _card.ResetSize();
    }

    /// <summary>Sets the drop-location sentence (same wording for list and board sources).</summary>
    public void SetLocation(string text)
    {
        if (_locationLabel != null)
            _locationLabel.Text = text ?? string.Empty;
    }

    /// <summary>Shows or hides the cuelist insert line at <paramref name="globalPos"/>.</summary>
    public void SetDropLine(bool visible, Vector2 globalPos = default, Vector2 size = default)
    {
        _dropLine.Visible = visible;
        if (!visible)
            return;
        _dropLine.GlobalPosition = globalPos;
        _dropLine.Size = new Vector2(Mathf.Max(2f, size.X), Mathf.Max(2f, size.Y));
    }

    /// <summary>Frees overlay resources. Call from the workspace <c>_ExitTree</c>.</summary>
    public void DisposeResources()
    {
        End();
        if (_card != null && GodotObject.IsInstanceValid(_card))
            _card.RemoveThemeStyleboxOverride("panel");
        if (_dropLine != null && GodotObject.IsInstanceValid(_dropLine))
            _dropLine.RemoveThemeStyleboxOverride("panel");
        UiUtilities.DisposeRefCounted(_cardStyle);
        UiUtilities.DisposeRefCounted(_lineStyle);
        if (_root != null && GodotObject.IsInstanceValid(_root))
        {
            _root.GetParent()?.RemoveChild(_root);
            _root.QueueFree();
        }
    }

    private void ClearNames()
    {
        foreach (Node child in _namesBox.GetChildren())
            child.QueueFree();
    }
}
