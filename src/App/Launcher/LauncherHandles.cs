using System.Threading.Tasks;
// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Services;
using Godot;

namespace Cue2.App.Launcher;
public partial class LauncherHandles : Control
{
	private GlobalData _globalData;
	//Variables
	private bool _dragging;
	private bool _resizing;
	private Vector2I _initialMouse;
	private Vector2I _initialWindow;

	private int _offsetX;
	private int _offsetY;
	private Control _resizeNode;
	private int _windowNumber;

	private Vector2I _dragOffset;

	private Vector2I _minWindowSize = new Vector2I(600, 370);
	
	private GlobalSignals _globalSignals;

	private Color _originalBorderColor;
	private bool _isFading;
	private float _fadeProgress;
	private Color _highlightColor;
	private StyleBoxFlat _boarderStylebox;


	// Called when the node enters the scene tree for the first time.
	public override void _Ready()
	{
		_globalData = GetNode<GlobalData>("/root/GlobalData");
		_globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
		_windowNumber = GetWindow().GetWindowId();

		_globalSignals.LogAlert += _alertReceived;
		
		// Border variables for event highlighting
		var border = GetNode<Panel>("%Border");
		_boarderStylebox = border.GetThemeStylebox("panel") as StyleBoxFlat;
		if (_boarderStylebox == null) return;
		_originalBorderColor = _boarderStylebox.BorderColor;
		_highlightColor = new Color(1,0,0,1);//GlobalStyles.Danger;
		SetProcess(false);
	}

	private void _alertReceived()
	{
		TaskUtil.Run(AlertReceivedAsync, "LauncherHandles._alertReceived");
	}

	private async Task AlertReceivedAsync()
	{
		if (_boarderStylebox == null) return;
		
		_boarderStylebox.BorderColor = _highlightColor;
		await ToSignal(GetTree().CreateTimer(0.5), "timeout");
		_fadeProgress = 0.0f;
		_isFading = true;
		UpdateIdleProcess();
	}

	private void _on_right_gui_input(InputEvent @event){
		if (@event is InputEventMouseButton)
		{
			_gui_input_handling(@event, GetNode<Control>("Right"));
		}
	}

	private void _on_bottom_gui_input(InputEvent @event){
		if (@event is InputEventMouseButton)
		{
			_gui_input_handling(@event, GetNode<Control>("Bottom"));
		}
	}

	private void _on_corner_gui_input(InputEvent @event){
		if (@event is InputEventMouseButton)
		{
			_gui_input_handling(@event, GetNode<Control>("Corner"));
		}
	}

	private void _gui_input_handling(InputEvent @event, Control @node){
		if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left })
		{
			if (_resizing == false)
			{
				_resizeNode = @node;
			}	
			_resizing = @event.IsPressed();
			UpdateIdleProcess();
			GD.Print(DisplayServer.GetWindowList()[0]);
		}
	}

	private void _on_drag_bar_gui_input(InputEvent @event){
		if (@event is InputEventMouseButton){
			_windowNumber = GetWindow().GetWindowId();
			if (DisplayServer.WindowGetMode() == DisplayServer.WindowMode.Fullscreen) {
				DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed, _windowNumber);
			}
			_initialMouse = DisplayServer.MouseGetPosition();
			_initialWindow = DisplayServer.WindowGetPosition(_windowNumber);
			_offsetX = _initialWindow[0] - _initialMouse[0];
			_offsetY = _initialWindow[1] - _initialMouse[1];
			_dragging = @event.IsPressed();
			UpdateIdleProcess();
		}
	}

	/// <summary>
	/// Per-frame work only while dragging, resizing, or fading the alert border.
	/// </summary>
	private void UpdateIdleProcess()
	{
		SetProcess(_dragging || _resizing || _isFading);
	}

	public override void _Process(double delta)
	{
		if (_resizing)
		{
			float uiScale = _globalData.UserDataManager?.UiScale ?? UserDataManager.DefaultUiScale;
			if (_resizeNode == GetNode<Node>("Right")){
				DisplayServer.WindowSetSize(new Vector2I((int)(GetLocalMousePosition()[0]*uiScale), DisplayServer.WindowGetSize(_windowNumber)[1]), _windowNumber);
			}
			if (_resizeNode == GetNode<Node>("Bottom")){
				DisplayServer.WindowSetSize(new Vector2I(DisplayServer.WindowGetSize(_windowNumber)[0], (int)(GetLocalMousePosition()[1]*uiScale)), _windowNumber);
			}
			if (_resizeNode == GetNode<Node>("Corner")){
				DisplayServer.WindowSetSize(new Vector2I((int)(GetLocalMousePosition()[0] *
				                                               uiScale), (int)
					(GetLocalMousePosition()[1] * uiScale)), _windowNumber);
			}
			if (DisplayServer.WindowGetSize()[0] < _minWindowSize[0]){
				DisplayServer.WindowSetSize(new Vector2I(_minWindowSize[0], DisplayServer.WindowGetSize(_windowNumber)[1]), _windowNumber);
			}
			if (DisplayServer.WindowGetSize()[1] < _minWindowSize[1]){
				DisplayServer.WindowSetSize(new Vector2I(DisplayServer.WindowGetSize(_windowNumber)[0], _minWindowSize[1]), _windowNumber);
			}
		}

		if (_dragging)
		{
			DisplayServer.WindowSetPosition(new Vector2I(DisplayServer.MouseGetPosition()[0] + _offsetX, DisplayServer.MouseGetPosition()[1] + _offsetY), _windowNumber);
		}
		
		if (_isFading)
		{
			_fadeProgress += (float)delta / 1.0f; // 1-second fade duration
			if (_fadeProgress >= 1.0f)
			{
				_fadeProgress = 1.0f;
				_isFading = false; // Stop fading
			}


			// Interpolate between highlight color and original color
			Color lerpedColor = _highlightColor.Lerp(_originalBorderColor, _fadeProgress);

			_boarderStylebox.BorderColor = lerpedColor;
		}

		if (!_dragging && !_resizing && !_isFading)
			SetProcess(false);
	}
}
