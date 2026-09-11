// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using Cue2.Domain.Cues;
using Cue2.Services;
using Godot;

namespace Cue2.UI.Preview;

/// <summary>
/// Inspector preview for text overlays: scaled canvas with the target layer rect and live typography.
/// </summary>
/// <remarks>
/// Mirrors <see cref="VideoPreviewer"/> layout (view area + canvas panel + layer-sized content host)
/// without transport controls — text is static preview of component properties.
/// </remarks>
public partial class TextPreviewer : Control
{
    private Control _viewArea;
    private Panel _canvasArea;
    private Control _layerHost;
    private ColorRect _background;
    private RichTextLabel _label;

    private TextComponent _component;
    private int _layerId = -1;
    private float _previewScale = 1f;
    private bool _areasDirty = true;

    /// <inheritdoc />
    public override void _Ready()
    {
        _viewArea = GetNode<Control>("%ViewArea");
        _canvasArea = GetNode<Panel>("%CanvasArea");
        _layerHost = GetNode<Control>("%LayerHost");
        _background = GetNodeOrNull<ColorRect>("%TextBackground");
        _label = GetNodeOrNull<RichTextLabel>("%TextDisplay");

        if (_background == null)
        {
            _background = new ColorRect
            {
                Name = "TextBackground",
                MouseFilter = MouseFilterEnum.Ignore,
                Visible = false
            };
            _background.UniqueNameInOwner = true;
            _layerHost.AddChild(_background);
        }

        if (_label == null)
        {
            _label = new RichTextLabel
            {
                Name = "TextDisplay",
                MouseFilter = MouseFilterEnum.Ignore,
                ScrollActive = false,
                FitContent = false,
                ClipContents = true
            };
            _label.UniqueNameInOwner = true;
            _layerHost.AddChild(_label);
        }

        _viewArea.Resized += OnViewResized;
        _layerHost.ClipContents = true;
        _layerHost.MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_viewArea != null && IsInstanceValid(_viewArea))
            _viewArea.Resized -= OnViewResized;
    }

    /// <inheritdoc />
    public override void _Notification(int what)
    {
        if (what == NotificationVisibilityChanged && IsVisibleInTree() && _areasDirty)
            CallDeferred(MethodName.RefreshLayout);
    }

    /// <summary>
    /// Binds a text component for preview (does not take ownership).
    /// </summary>
    /// <param name="component">Component to mirror, or null to clear.</param>
    public void SetComponent(TextComponent component)
    {
        _component = component;
        if (component != null)
            _layerId = component.TargetLayerId;
        RefreshAll();
    }

    /// <summary>
    /// Updates the preview layer geometry for the given target layer id.
    /// </summary>
    /// <param name="layerId">Target layer id, or -1 for no layer.</param>
    public void SetAreasDeferred(int layerId)
    {
        _layerId = layerId;
        _areasDirty = true;
        CallDeferred(MethodName.RefreshLayout);
    }

    /// <summary>
    /// Re-applies typography and layout from the bound component.
    /// </summary>
    public void RefreshAll()
    {
        if (!IsInsideTree())
            return;

        RefreshLayout();
        ApplyComponentVisuals();
    }

    /// <summary>
    /// Re-applies only text style (content, colour, size, etc.) using the last layout scale.
    /// </summary>
    public void RefreshVisuals()
    {
        ApplyComponentVisuals();
    }

    private void OnViewResized()
    {
        _areasDirty = true;
        if (IsVisibleInTree())
            CallDeferred(MethodName.RefreshLayout);
    }

    private void RefreshLayout()
    {
        _areasDirty = false;

        if (_viewArea == null || _canvasArea == null || _layerHost == null)
            return;

        var canvas = DisplaysManager.Canvas;
        if (canvas == null)
        {
            _layerHost.Visible = false;
            return;
        }

        var viewSize = _viewArea.Size;
        if (viewSize.X < 1f || viewSize.Y < 1f)
        {
            _areasDirty = true;
            return;
        }

        var canvasSize = new Vector2(canvas.CanvasSize.X, canvas.CanvasSize.Y);
        if (canvasSize.X < 1f || canvasSize.Y < 1f)
            canvasSize = new Vector2(1920, 1080);

        _previewScale = Mathf.Min(viewSize.X / canvasSize.X, viewSize.Y / canvasSize.Y);
        if (_previewScale <= 0f)
            _previewScale = 1f;

        var scaledCanvas = canvasSize * _previewScale;
        _canvasArea.Size = scaledCanvas;
        // Center canvas in view for a cleaner inspector look.
        _canvasArea.Position = (viewSize - scaledCanvas) * 0.5f;

        var layer = DisplaysManager.GetLayerById(_layerId);
        if (layer == null || _layerId < 0)
        {
            _layerHost.Visible = false;
            ApplyComponentVisuals();
            return;
        }

        _layerHost.Visible = true;
        // Layer host is a sibling of canvas (same parent ViewArea), so offset by canvas position.
        var scaledLayerPos = new Vector2(layer.CanvasPosition.X, layer.CanvasPosition.Y) * _previewScale;
        var scaledLayerSize = new Vector2(layer.Size.X, layer.Size.Y) * _previewScale;
        _layerHost.Position = _canvasArea.Position + scaledLayerPos;
        _layerHost.Size = scaledLayerSize;

        ApplyComponentVisuals();
    }

    private void ApplyComponentVisuals()
    {
        if (_label == null || !IsInstanceValid(_label))
            return;

        if (_component == null)
        {
            _label.Text = string.Empty;
            if (_background != null && IsInstanceValid(_background))
                _background.Visible = false;
            if (_layerHost != null && IsInstanceValid(_layerHost))
                _layerHost.Modulate = Colors.White;
            return;
        }

        float margin = Mathf.Max(0, _component.Margins) * _previewScale;
        TextComponent.ApplyFillWithMargins(_label, margin);
        if (_background != null && IsInstanceValid(_background))
        {
            TextComponent.ApplyFillWithMargins(_background, margin);
            _background.Visible = _component.BackgroundEnabled;
            _background.Color = _component.BackgroundColor;
        }

        _component.ApplyToRichTextLabel(_label, _previewScale);

        float opacity = Mathf.Clamp(_component.Opacity, 0f, 1f);
        if (_layerHost != null && IsInstanceValid(_layerHost))
            _layerHost.Modulate = new Color(1f, 1f, 1f, opacity);
    }
}
