// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cue2.Domain.Connections;
using Cue2.Services;
using Cue2.UI.Utilities;
using AppSettings = Cue2.Domain.ShowSettings.Settings;

namespace Cue2.UI.Settings;

/// <summary>
/// SettingsCueLights is the UI parent for the settings window.
/// It is responsible for user setting of generral cuelight options.
/// Adding new cuelight instances
/// </summary>
public partial class SettingsCueLights : ScrollContainer
{
    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private CueLightManager _cueLightManager;
    private HistoryManager _historyManager;
    private AppSettings _settings;
    
    private Godot.Collections.Dictionary<CueLight, Callable> _cueLightHandlers = new Godot.Collections.Dictionary<CueLight, Callable>();
    
    
    //UI
    private PackedScene _cueLightInstanceScene;
    
    private VBoxContainer _cueLightsContainer;
    

    private ColorPickerButton _idleColour;
    private ColorPickerButton _goColour;
    private ColorPickerButton _standbyColour;
    private ColorPickerButton _countInColour;
    private LineEdit _brightnessLineEdit;

    private Button _testGoButton;
    private Button _testStandbyButton;
    private Button _testCountInButton;
    private Button _testIdentifyButton;
    private Button _testCancelButton;
    
    private Button _newCueLightButton;
    private Button _scanNetworkButton;
    
    // Ensure only one IP is used Cuelight view in main ui then components. 
    
    public override void _Ready()
    {
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _cueLightManager = _globalData.CueLightManager;
        _settings = _globalData.Settings;
        _historyManager = _globalData?.HistoryManager;

        _cueLightInstanceScene = SceneLoader.LoadPackedScene("uid://6vou7cplmgmo", out string _);

        VisibilityChanged += OnVisible;
        if (_globalSignals != null)
            _globalSignals.NewSession += OnNewSession;
        if (_historyManager != null)
            _historyManager.HistoryRestored += OnHistoryRestored;
        
        // UI
        _cueLightsContainer = GetNode<VBoxContainer>("%CueLightsContainer");
        
        _newCueLightButton = GetNode<Button>("%NewCueLightButton");
        _idleColour = GetNode<ColorPickerButton>("%IdleColour");
        _goColour = GetNode<ColorPickerButton>("%GoColour");
        _standbyColour = GetNode<ColorPickerButton>("%StandbyColour");
        _countInColour = GetNode<ColorPickerButton>("%CountInColour");
        _brightnessLineEdit = GetNode<LineEdit>("%BrightnessLineEdit");
        
        _testGoButton = GetNode<Button>("%TestGoButton");
        _testStandbyButton = GetNode<Button>("%TestStandbyButton");
        _testCountInButton = GetNode<Button>("%TestCountInButton");
        _testIdentifyButton = GetNode<Button>("%TestIdentifyButton");
        _testCancelButton = GetNode<Button>("%TestCancelButton");
        
        _scanNetworkButton = GetNode<Button>("%ScanNetworkButton");
        _scanNetworkButton.Pressed += OnScanNetworkPressed;
        
        
        
        OnVisible(); // Initial set data
        
        _idleColour.PopupClosed += () => 
        {
            _settings.CueLightIdleColour = _idleColour.Color;
            _ = UpdateAllCueLightSettingsAsync();
        };
        _goColour.PopupClosed += () => 
        {
            _settings.CueLightGoColour = _goColour.Color;
            _ = UpdateAllCueLightSettingsAsync();
        };
        _standbyColour.PopupClosed += () => 
        {
            _settings.CueLightStandbyColour = _standbyColour.Color;
            _ = UpdateAllCueLightSettingsAsync();
        };
        _countInColour.PopupClosed += () => 
        {
            _settings.CueLightCountInColour = _countInColour.Color;
            _ = UpdateAllCueLightSettingsAsync();
        };

        UiUtilities.BindLineEditCommit(_brightnessLineEdit, OnBrightnessSubmitted);

        _testGoButton.Pressed += () => _cueLightManager.AllGo("TEST");
        _testStandbyButton.Pressed += () => _cueLightManager.AllStandby("TEST");
        _testCountInButton.Pressed += () => _cueLightManager.AllCountIn(cueNum:"TEST");
        _testIdentifyButton.Toggled += state => _cueLightManager.AllIdentify(state);
        _testCancelButton.Pressed += () => _cueLightManager.AllCancel();
        
        
        _newCueLightButton.Pressed += NewCueLightButton;
        
        RebuildCueLightList();
    
        UiLocalizer.LocalizeTree(this);
        if (_globalSignals != null)
            _globalSignals.LocaleChanged += OnLocaleChanged;
}

    private void OnNewSession()
    {
        RebuildCueLightList();
        OnVisible();
    }

    /// <summary>
    /// Rebuilds the cue-light list and colour fields after undo/redo or settings-file load.
    /// </summary>
    private void OnHistoryRestored(int scope)
    {
        if (scope != (int)HistoryManager.HistoryScope.Settings)
            return;
        RebuildCueLightList();
        OnVisible();
    }

    /// <summary>
    /// Rebuilds cue-light instance cards from the live manager (New Session / full resync).
    /// </summary>
    private void RebuildCueLightList()
    {
        if (_cueLightsContainer == null || _cueLightInstanceScene == null)
            return;

        // Disconnect handlers before freeing UI
        foreach (var pair in _cueLightHandlers.ToList())
        {
            var cueLight = pair.Key;
            var handler = pair.Value;
            if (GodotObject.IsInstanceValid(cueLight))
            {
                try
                {
                    cueLight.Disconnect(CueLight.SignalName.ConnectionChanged, handler);
                }
                catch { /* already disconnected */ }
            }
        }
        _cueLightHandlers.Clear();

        foreach (var child in _cueLightsContainer.GetChildren())
        {
            _cueLightsContainer.RemoveChild(child);
            child.QueueFree();
        }

        if (_cueLightManager == null)
            return;

        foreach (var cueLight in _cueLightManager.GetCueLights())
        {
            PanelContainer instance = _cueLightInstanceScene.Instantiate<PanelContainer>();
            _cueLightsContainer.AddChild(instance);
            instance.Name = cueLight.Id.ToString();
            SetUpInstance(instance, cueLight);
        }
    }
    
    private void OnBrightnessSubmitted(string text)
    {
        if (int.TryParse(text, out int percent))
        {
            percent = Mathf.Clamp(percent, 0, 100);
            byte next = (byte)(percent * 255 / 100);
            if (_settings.CueLightBrightness == next)
            {
                _brightnessLineEdit.Text = percent.ToString();
                _brightnessLineEdit.ReleaseFocus();
                return;
            }
            _settings.CueLightBrightness = next;
            _brightnessLineEdit.Text = percent.ToString();
            _ = UpdateAllCueLightSettingsAsync();
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), 
                $"Updated cue light brightness to {percent}% ({_settings.CueLightBrightness}/255)", 0);
        }
        else
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), 
                $"Invalid brightness input: '{text}'. Must be an integer between 0 and 100.", 1);
            _brightnessLineEdit.Text = Math.Round(_settings.CueLightBrightness * 100f / 255f).ToString();
        }
        _brightnessLineEdit.ReleaseFocus();
    }

    private void NewCueLightButton()
    {
        var cueLight = _cueLightManager.CreateCueLight();
        PanelContainer instance = _cueLightInstanceScene.Instantiate<PanelContainer>();
        _cueLightsContainer.AddChild(instance);
        instance.Name = cueLight.Id.ToString();
        SetUpInstance(instance, cueLight);
    }

    private void SetUpInstance(PanelContainer instance, CueLight cueLight)
    {

        
        
        
        var nameLineEdit = instance.GetNode<LineEdit>("%NameLineEdit");
        nameLineEdit.Text = cueLight.Name;
        UiUtilities.BindLineEditCommit(nameLineEdit, text =>
        {
            if (cueLight.Name == text)
            {
                nameLineEdit.ReleaseFocus();
                return;
            }
            cueLight.Name = text;
            _ = UpdateAllCueLightSettingsAsync();
            nameLineEdit.ReleaseFocus();
        });
        
        var ipLineEdit = instance.GetNode<LineEdit>("%IpLineEdit");
        ipLineEdit.Text = cueLight.IpAddress;
        UiUtilities.BindLineEditCommit(ipLineEdit, text =>
        {
            string cleanedIp = UiUtilities.VerifyIpInput(text, _globalSignals);
            if (cleanedIp != null)
            {
                if (cueLight.IpAddress != cleanedIp)
                    cueLight.SetIpAddress(cleanedIp);
                ipLineEdit.Text = cleanedIp;
            }
            else
                ipLineEdit.Text = cueLight.IpAddress;
            ipLineEdit.ReleaseFocus();
        });
        
        var connectionStatusColourRect = instance.GetNode<ColorRect>("%ConnectionStatusColourRect");
        connectionStatusColourRect.Color = cueLight.CueLightIsConnected ? Colors.Green : Colors.Red;
        
        Callable handler = Callable.From((bool state) => {
            if (GodotObject.IsInstanceValid(connectionStatusColourRect))
            {
                connectionStatusColourRect.Color = state ? new Color(0, 1, 0) : new Color(1, 0, 0);
            }

        });

        cueLight.Connect(CueLight.SignalName.ConnectionChanged, handler);
        _cueLightHandlers[cueLight] = handler;
        
        
        var checkConnectionButton = instance.GetNode<Button>("%CheckConnectionButton");
        checkConnectionButton.Icon = GetThemeIcon("Refresh", "AtlasIcons");
        checkConnectionButton.Pressed += () =>
        {
            _ = cueLight.PingAsync();
            connectionStatusColourRect.Color = cueLight.CueLightIsConnected ? Colors.Green : Colors.Red;
            connectionStatusColourRect.TooltipText = cueLight.CueLightIsConnected
                ? UiLocalizer.T("Connected")
                : UiLocalizer.T("Disconnected");
        };
        
        var identifyButton = instance.GetNode<Button>("%IdentifyButton");
        identifyButton.Toggled += async state => await cueLight.IdentifyAsync(state);
        
        var enabledToggle = instance.GetNode<CheckButton>("%EnabledToggle"); 
        enabledToggle.ButtonPressed = cueLight.IsEnabled; 
        enabledToggle.Toggled += (bool pressed) => 
        { 
            cueLight.IsEnabled = pressed; 
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),  
                $"CueLight {cueLight.Id} ({cueLight.Name}) {(pressed ? "enabled" : "disabled")}", 0); 
        };
        
        var deleteButton = instance.GetNode<Button>("%DeleteButton");
        deleteButton.Icon = GetThemeIcon("DeleteBin", "AtlasIcons");
        deleteButton.Pressed += () => DeleteCueLight(instance, cueLight);
        
        var collapseButton = instance.GetNode<Button>("%CueLightCollapseButton");
        collapseButton.Icon = GetThemeIcon("Right", "AtlasIcons");
        
        var configAccordian = instance.GetNode<VBoxContainer>("%ConfigAccordian");
        configAccordian.Visible = false;

        collapseButton.Pressed += () =>
        {
            configAccordian.Visible = !configAccordian.Visible;
            collapseButton.Icon = GetThemeIcon(configAccordian.Visible ? "Down" : "Right", "AtlasIcons");
        };

    }
    

    

    private void DeleteCueLight(PanelContainer instance, CueLight cueLight)
    {
        try
        {
            // Disconnect the CueLight if connected
            if (cueLight.CueLightIsConnected)
            {
                GD.Print($"SettingsCueLights:DeleteCueLight - Disconnected CueLight {cueLight.Id} ({cueLight.Name})");
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"Disconnected CueLight {cueLight.Id} ({cueLight.Name})", 0);
            }
            // Remove UI instance
            instance.QueueFree(); // Marks the UI instance for deletion

            // Remove from CueLightManager
            _cueLightManager.DeleteCueLight(cueLight);

            GD.Print($"SettingsCueLights:DeleteCueLight - Deleted CueLight {cueLight.Id} ({cueLight.Name}) and its UI instance");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Deleted CueLight {cueLight.Id} ({cueLight.Name}) and its UI instance", 0);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"SettingsCueLights:DeleteCueLight - Error deleting CueLight {cueLight.Id}: {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Error deleting CueLight {cueLight.Id}: {ex.Message}", 2);
        }
    }
    
    private async Task UpdateAllCueLightSettingsAsync()
    {
        try
        {
            var cueLights = _cueLightManager.GetCueLights();
            foreach (var cueLight in cueLights)
            {
                if (cueLight.CueLightIsConnected&& cueLight.IsEnabled)
                {
                    await cueLight.ConfigureAsync(
                        _settings.CueLightIdleColour, 
                        _settings.CueLightGoColour, 
                        _settings.CueLightStandbyColour, 
                        _settings.CueLightCountInColour,
                        _settings.CueLightBrightness);
                    GD.Print($"SettingsCueLights:UpdateAllCueLightColorsAsync - Updated colors for CueLight {cueLight.Id} ({cueLight.Name})");
                    _globalSignals.EmitSignal(nameof(GlobalSignals.Log), 
                        $"Updated colors for CueLight {cueLight.Id} ({cueLight.Name})", 0);
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"SettingsCueLights:UpdateAllCueLightColorsAsync - Error updating colors: {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), 
                $"Error updating cue light colors: {ex.Message}", 2);
        }
    }
    
    // New method
    private async void OnScanNetworkPressed()
    {
        try
        {
            _scanNetworkButton.Disabled = true; // Prevent spam
            var discoveredIps = await _cueLightManager.DiscoverCueLightsAsync();
        
            var existingIps = new HashSet<string>(_cueLightManager.GetCueLights().Select(cl => cl.IpAddress));
        
            foreach (var ip in discoveredIps)
            {
                if (!existingIps.Contains(ip))
                {
                    var cueLight = _cueLightManager.CreateCueLightWithIp(ip);
                    var instance = _cueLightInstanceScene.Instantiate<PanelContainer>();
                    _cueLightsContainer.AddChild(instance);
                    SetUpInstance(instance, cueLight);
                    await cueLight.PingAsync();
                    _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Added discovered cue light at {ip}", 0);
                }
                else
                {
                    _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Skipped duplicate cue light at {ip}", 1);
                }
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"SettingsCueLights:OnScanNetworkPressed - Error scanning network: {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Error scanning for cue lights: {ex.Message}", 2);
        }
        finally
        {
            _scanNetworkButton.Disabled = false;
        }
    }
    
    
    private void OnVisible()
    {
        if (!Visible || _settings == null)
            return;

        _idleColour.Color = _settings.CueLightIdleColour;
        _goColour.Color = _settings.CueLightGoColour;
        _standbyColour.Color = _settings.CueLightStandbyColour;
        _countInColour.Color = _settings.CueLightCountInColour;
        if (_brightnessLineEdit != null)
            _brightnessLineEdit.Text = Math.Round(_settings.CueLightBrightness * 100f / 255f).ToString();
    }
    
    public override void _ExitTree()
    {
        if (_globalSignals != null)
            _globalSignals.LocaleChanged -= OnLocaleChanged;

        if (_globalSignals != null)
            _globalSignals.NewSession -= OnNewSession;
        if (_historyManager != null)
            _historyManager.HistoryRestored -= OnHistoryRestored;
        VisibilityChanged -= OnVisible;

        foreach (var pair in _cueLightHandlers)
        {
            var cueLight = pair.Key;
            var handler = pair.Value;
            if (GodotObject.IsInstanceValid(cueLight))
            {
                cueLight.Disconnect(CueLight.SignalName.ConnectionChanged, handler);
            }
        }
        _cueLightHandlers.Clear();
    }

    /// <summary>
    /// Re-localizes panel chrome when the UI language changes.
    /// </summary>
    /// <param name="localeCode">New locale code.</param>
    private void OnLocaleChanged(string localeCode)
    {
        if (!GodotObject.IsInstanceValid(this))
            return;
        UiLocalizer.LocalizeTree(this);
    }

}
