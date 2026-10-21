// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.UI.Utilities;

namespace Cue2.Domain.Playback;

/// <summary>
/// Partial: Component setup/trigger, media wiring, component UI updates.
/// </summary>
public partial class ActiveCue
{
    
    private async Task SetupComponents()
    {
        if (!IsSetupStillValid())
            return;

        var tasks = new List<Task>();
        foreach (var component in _cue.Components)
        {
            if (component is AudioComponent audioComponent)
            {
                tasks.Add(SetupAudioComponent(audioComponent));
            }
            else if (component is AudioInputComponent audioInputComponent)
            {
                tasks.Add(SetupAudioInputComponent(audioInputComponent));
            }
            else if (component is VideoComponent videoComponent)
            {
                tasks.Add(SetupVideoComponent(videoComponent));
            }
            else if (component is TextComponent textComponent)
            {
                tasks.Add(SetupTextComponent(textComponent));
            }
            else if (component is CueLightComponent cueLightComponent)
            {
                tasks.Add(SetupCueLightComponent(cueLightComponent));
                //var cueLightComp = component as CueLightComponent;
                //tasks.Add(cueLightComp.ExecuteAsync(_cue.CueNum));
            }
            else if (component is OscComponent oscComponent)
            {
                tasks.Add(SetupOscComponent(oscComponent));
            }
            else if (component is MidiOutputComponent midiOutComponent)
            {
                tasks.Add(SetupMidiOutputComponent(midiOutComponent));
            }
            else if (component is ControlComponent controlComponent)
            {
                tasks.Add(SetupControlComponent(controlComponent));
            }
            else if (component is TimecodeControlComponent timecodeControl)
            {
                tasks.Add(SetupTimecodeControlComponent(timecodeControl));
            }
        }
        await Task.WhenAll(tasks);

        // Stop/cancel during preload may complete awaits after Cleanup — never mark ready or link CC.
        if (!IsSetupStillValid())
        {
            GD.Print($"ActiveCue:SetupComponents - {_cue?.Name}: aborted after setup tasks (cleaned/invalid)");
            return;
        }

        LinkVideoSubtitlesToText();
        _componentsSetup = true;
    }

    /// <summary>
    /// True when this ActiveCue may still attach playback nodes / component UI after an await.
    /// </summary>
    /// <remarks>
    /// Call after every await in component setup. If false, discard any half-built playback
    /// and return — do not touch freed bars or increment component counts.
    /// </remarks>
    private bool IsSetupStillValid()
    {
        if (_isCleaned)
            return false;
        // Stop (fade path) during setup: late components must not register.
        if (_stopRequestedDuringSetup)
            return false;
        if (!IsInstanceValid(this))
            return false;
        if (_activeCueBar == null || !IsInstanceValid(_activeCueBar))
            return false;
        if (_componentContainer != null && !IsInstanceValid(_componentContainer))
            return false;
        return true;
    }

    /// <summary>
    /// Tears down an audio playback that was never registered on this ActiveCue (setup abort / Init failure).
    /// </summary>
    private void DiscardOrphanAudioPlayback(ActiveAudioPlayback playback)
    {
        if (playback == null)
            return;
        try
        {
            playback.Clean();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:DiscardOrphanAudioPlayback - {_cue?.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Tears down a video playback that was never registered (may already be parented under the bar).
    /// </summary>
    private void DiscardOrphanVideoPlayback(ActiveVideoPlayback playback)
    {
        if (playback == null)
            return;
        try
        {
            if (IsInstanceValid(playback))
            {
                playback.Clean();
                if (IsInstanceValid(playback) && playback.IsInsideTree())
                    playback.QueueFree();
                else if (IsInstanceValid(playback))
                    playback.Free();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:DiscardOrphanVideoPlayback - {_cue?.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Tears down a text playback that was never registered (may already be parented under the bar).
    /// </summary>
    private void DiscardOrphanTextPlayback(ActiveTextPlayback playback)
    {
        if (playback == null)
            return;
        try
        {
            if (IsInstanceValid(playback))
                playback.Clean();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:DiscardOrphanTextPlayback - {_cue?.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// When the video component has closed captions enabled, wires the active text
    /// playback so timed subtitles drive its live text.
    /// </summary>
    private void LinkVideoSubtitlesToText()
    {
        try
        {
            var videoComp = _cue?.GetVideoComponent();
            if (videoComp == null || !videoComp.UseSubtitles || videoComp.IsImage)
                return;

            ActiveVideoPlayback videoPlayback = null;
            foreach (var kv in _componentToVideo)
            {
                if (kv.Value == videoComp && _activeVideoComponents.TryGetValue(kv.Key, out var pb))
                {
                    videoPlayback = pb;
                    break;
                }
            }

            ActiveTextPlayback textPlayback = null;
            foreach (var pb in _activeTextComponents.Values)
            {
                if (pb != null && IsInstanceValid(pb))
                {
                    textPlayback = pb;
                    break;
                }
            }

            if (videoPlayback == null)
                return;

            if (textPlayback == null)
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"Video in {_cue.Name} has closed captions enabled but no text component is present.",
                    1);
                return;
            }

            // CC slave: hold until video ends (do not auto-complete on text duration alone).
            textPlayback.IsSubtitleSlave = true;
            videoPlayback.SetLinkedTextPlayback(textPlayback);
            // Start blank until the first cue.
            textPlayback.SetLiveTextOverride(string.Empty);
            GD.Print($"ActiveCue:LinkVideoSubtitlesToText - Linked CC to text on {_cue.Name}");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:LinkVideoSubtitlesToText - {ex.Message}");
        }
    }

    private async Task SetupAudioComponent(AudioComponent audioComponent)
    {
        ActiveAudioPlayback playback = null;
        try
        {
            if (!IsSetupStillValid())
                return;

            // Fail fast: no output means playback can never produce sound or complete.
            if (!audioComponent.HasOutputAssigned)
            {
                GD.PrintErr($"ActiveCue:SetupAudioComponent - No output assigned for {audioComponent.AudioFile}");
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"Cannot play audio in {_cue.Name}: no output assigned. Assign a patch or device in the inspector.", 2);
                return;
            }

            if (string.IsNullOrEmpty(audioComponent.AudioFile))
            {
                GD.PrintErr("ActiveCue:SetupAudioComponent - Audio file path is empty");
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"Cannot play audio in {_cue.Name}: no audio file set.", 2);
                return;
            }

            if (!MediaFileAvailable(audioComponent.AudioFile))
            {
                ReportMissingMedia(audioComponent.AudioFile);
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"Cannot play audio in {_cue.Name}: file missing ({audioComponent.AudioFile}).", 2);
                return;
            }

            // Preload metadata if not already (assuming done in inspector/load)
            if (audioComponent.Metadata == null)
            {
                audioComponent.Metadata = await _mediaEngine.GetAudioFileMetadataAsync(audioComponent.AudioFile);
                if (!IsSetupStillValid())
                    return;
            }

            playback = new ActiveAudioPlayback(audioComponent, _audioDevices);
            await playback.InitAsync();

            // Stop/cancel during open: do not attach UI or register streams on a freed ActiveCue.
            if (!IsSetupStillValid())
            {
                DiscardOrphanAudioPlayback(playback);
                playback = null;
                return;
            }

            // UI
            PanelContainer componentPanel = CreateStyledComponentProgressBar(showMeter: true);
            _componentContainer.AddChild(componentPanel);
            componentPanel.GetNode<Label>("%ComponentLabel").Text = Path.GetFileName(audioComponent.AudioFile);
            var typeIcon = componentPanel.GetNode<TextureRect>("%ComponentIcon");
            var pauseButton = componentPanel.GetNode<Button>("%ComponentPause");
            var stopButton = componentPanel.GetNode<Button>("%ComponentStop");
            var timeLabel = componentPanel.GetNode<Label>("%ComponentTime");
            timeLabel.Text = audioComponent.Loop || audioComponent.TotalDuration < 0
                ? UiUtilities.FormatTime(0)
                : UiUtilities.FormatTime(audioComponent.TotalDuration);
            
            typeIcon.Texture = _activeCueBar.GetThemeIcon("Audio2", "AtlasIcons");
            // If cue is already paused (global pause during setup), show resume icon.
            pauseButton.Icon = _activeCueBar.GetThemeIcon(_isPaused ? "Play" : "Pause", "AtlasIcons");
            stopButton.Icon = _activeCueBar.GetThemeIcon("Stop", "AtlasIcons");
            
            
            // Component Logic
            _activeAudioComponents.Add(componentPanel, playback);
            _componentToAudio.Add(componentPanel, audioComponent);
            playback = null; // ownership transferred to maps / Cleanup
            
            pauseButton.Pressed += () => 
            {
                if (_isCleaned || !IsInstanceValid(componentPanel)) return;
                if (!_activeAudioComponents.TryGetValue(componentPanel, out var pb) || pb == null)
                    return;

                if (!pb.IsPaused)
                    pb.Pause();
                else
                    pb.Resume();

                // Component-level toggle; refresh this button from playback state.
                if (IsInstanceValid(pauseButton) && IsInstanceValid(_activeCueBar))
                    pauseButton.Icon = _activeCueBar.GetThemeIcon(pb.IsPaused ? "Play" : "Pause", "AtlasIcons");
            };
            
            // Stop
            stopButton.Pressed += async () => await StopComponent(componentPanel);
            
            
            // Progress bar seeking (span includes playcount when not looping)
            var progressBar = componentPanel.GetNode<ProgressBar>(ComponentProgressPath);
            double pendingContentSeekSec = 0;
            double audioSeekSpan = audioComponent.GetProgressSpanSeconds();
            progressBar.GuiInput += (@event) =>
            {
                if (!_activeAudioComponents.ContainsKey(componentPanel)) return;
                if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
                {
                    if (mb.Pressed)
                    {
                        if (!_activeAudioComponents.TryGetValue(componentPanel, out var seekPb) || seekPb == null)
                            return;
                        seekPb.IsSeeking = true;
                        var localPos = progressBar.GetLocalMousePosition();
                        float percent = Mathf.Clamp(localPos.X / progressBar.Size.X, 0f, 1f);
                        pendingContentSeekSec = percent * audioSeekSpan;
                        progressBar.Value = percent * 100; // Preview this row only
                        timeLabel.Text = UiUtilities.FormatTime(pendingContentSeekSec);
                    }
                    else
                    {
                        // Release: perform the seek across playcount timeline
                        if (!_activeAudioComponents.TryGetValue(componentPanel, out var seekPb) || seekPb == null)
                            return;
                        if (seekPb.IsSeeking)
                        {
                            seekPb.SeekToTotalContentSeconds(pendingContentSeekSec);
                            // Head bar follows only once the seek is committed (mouse up).
                            SyncHeadTimelineFromComponentSeek(pendingContentSeekSec);
                            GD.Print($"ActiveCue:ProgressBar - Sought to content {pendingContentSeekSec:F3}s on release");
                        }
                        seekPb.IsSeeking = false;
                    }
                }
                else if (@event is InputEventMouseMotion)
                {
                    if (!_activeAudioComponents.TryGetValue(componentPanel, out var seekPb) || seekPb == null || !seekPb.IsSeeking)
                        return;
                    var localPos = progressBar.GetLocalMousePosition();
                    float percent = Mathf.Clamp(localPos.X / progressBar.Size.X, 0f, 1f);
                    pendingContentSeekSec = percent * audioSeekSpan;
                    progressBar.Value = percent * 100;
                    timeLabel.Text = UiUtilities.FormatTime(pendingContentSeekSec);
                }
            };

            _activeComponentCount++;
            WireAudioCompleted(_activeAudioComponents[componentPanel], componentPanel);
        }
        catch (Exception ex)
        {
            DiscardOrphanAudioPlayback(playback);
            GD.Print($"ActiveCue:SetupAudioComponent - Exception: {ex.Message}");
            if (IsMissingFileException(ex, audioComponent.AudioFile))
                ReportMissingMedia(audioComponent.AudioFile);
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Error activating audio component for cue {_cue.Name}: {ex.Message}", 2);
        }
    }

    private async Task SetupAudioInputComponent(AudioInputComponent component)
    {
        ActiveAudioInputPlayback playback = null;
        try
        {
            if (!IsSetupStillValid())
                return;
            if (component.InputPatch == null && component.InputPatchId < 0)
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"Cannot play audio input in {_cue.Name}: no input patch assigned.", 2);
                return;
            }
            if (component.Patch == null && string.IsNullOrEmpty(component.DirectOutput))
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"Cannot play audio input in {_cue.Name}: no output assigned.", 2);
                return;
            }

            playback = new ActiveAudioInputPlayback(component, _audioDevices);
            if (!IsSetupStillValid())
            {
                playback.Clean();
                playback = null;
                return;
            }

            PanelContainer componentPanel = CreateStyledComponentProgressBar(showMeter: true);
            _componentContainer.AddChild(componentPanel);
            string label = component.InputPatch?.Name;
            if (string.IsNullOrWhiteSpace(label))
                label = "Audio input";
            componentPanel.GetNode<Label>("%ComponentLabel").Text = label;
            componentPanel.GetNode<TextureRect>("%ComponentIcon").Texture =
                _activeCueBar.GetThemeIcon("Audio2", "AtlasIcons");
            var pauseButton = componentPanel.GetNode<Button>("%ComponentPause");
            var stopButton = componentPanel.GetNode<Button>("%ComponentStop");
            pauseButton.Icon = _activeCueBar.GetThemeIcon(_isPaused ? "Play" : "Pause", "AtlasIcons");
            stopButton.Icon = _activeCueBar.GetThemeIcon("Stop", "AtlasIcons");
            componentPanel.GetNode<Label>("%ComponentTime").Text = component.Duration <= 0
                ? "Until stopped"
                : UiUtilities.FormatTime(component.Duration);

            _activeAudioInputs.Add(componentPanel, playback);
            playback = null;
            var panel = componentPanel;
            pauseButton.Pressed += () =>
            {
                if (!_activeAudioInputs.TryGetValue(panel, out var pb) || pb == null)
                    return;
                if (pb.IsPaused)
                    pb.Resume();
                else
                    pb.Pause();
                if (IsInstanceValid(pauseButton) && IsInstanceValid(_activeCueBar))
                    pauseButton.Icon = _activeCueBar.GetThemeIcon(pb.IsPaused ? "Play" : "Pause", "AtlasIcons");
            };
            stopButton.Pressed += async () => await StopComponent(panel);
            ulong panelId = componentPanel.GetInstanceId();
            _activeAudioInputs[componentPanel].Completed += () =>
                Callable.From(() => CompleteAudioInputByPanelId(panelId)).CallDeferred();
            _activeComponentCount++;
        }
        catch (Exception ex)
        {
            try { playback?.Clean(); } catch { /* ignore */ }
            GD.PrintErr($"ActiveCue:SetupAudioInputComponent - {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Error activating audio input for cue {_cue.Name}: {ex.Message}", 2);
        }

        await Task.CompletedTask;
    }

    private void CompleteAudioInputByPanelId(ulong panelId)
    {
        if (!IsInstanceValid(this) || _isCleaned)
            return;
        PanelContainer panel = null;
        foreach (var key in _activeAudioInputs.Keys)
        {
            if (IsInstanceValid(key) && key.GetInstanceId() == panelId)
            {
                panel = key;
                break;
            }
        }
        if (panel == null)
            return;
        _activeAudioInputs.Remove(panel);
        panel.QueueFree();
        CheckForCueCompletion();
    }

    /// <summary>
    /// Subscribes to audio completion without capturing a freeable PanelContainer into CallDeferred args.
    /// </summary>
    private void WireAudioCompleted(ActiveAudioPlayback playback, PanelContainer componentPanel)
    {
        ulong panelId = componentPanel.GetInstanceId();
        ActiveAudioPlayback.CompletedEventHandler handler = () =>
        {
            // Never touch the PanelContainer here — it may already be disposed during Cleanup.
            Callable.From(() => CompleteAudioByPanelId(panelId)).CallDeferred();
        };
        playback.Completed += handler;
        _audioCompleteHandlers.Add((playback, handler));
    }

    private void CompleteAudioByPanelId(ulong panelId)
    {
        if (_isCleaned || !IsInstanceValid(this)) return;

        PanelContainer panel = FindLivePanelKey(_activeAudioComponents.Keys, panelId);
        if (panel == null)
        {
            CheckForCueCompletion();
            return;
        }

        _activeAudioComponents.Remove(panel);
        _componentToAudio.Remove(panel);
        if (IsInstanceValid(panel))
            panel.QueueFree();
        CheckForCueCompletion();
    }

    /// <summary>
    /// Creates Ui for VideoComponent and handles input.
    /// </summary>
    /// <param name="videoComponent"></param>
    private async Task SetupVideoComponent(VideoComponent videoComponent)
    {
        ActiveVideoPlayback playback = null;
        try
        {
            if (!IsSetupStillValid())
                return;

            if (string.IsNullOrEmpty(videoComponent.VideoFile))
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"Cannot play video in {_cue.Name}: no video file set.", 2);
                return;
            }

            if (!MediaFileAvailable(videoComponent.VideoFile))
            {
                ReportMissingMedia(videoComponent.VideoFile);
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"Cannot play video in {_cue.Name}: file missing ({videoComponent.VideoFile}).", 2);
                return;
            }

            videoComponent.RefreshIsImageFromPath();

            // Preload metadata if not already (assuming done in inspector/load)
            if (videoComponent.Metadata == null)
            {
                videoComponent.Metadata = await _mediaEngine.GetVideoFileMetadataAsync(videoComponent.VideoFile);
                if (!IsSetupStillValid())
                    return;
            }

            if (videoComponent.IsImage)
            {
                videoComponent.HasAudio = false;
                videoComponent.UseAudio = false;
                videoComponent.RecalculateDuration();
            }

            if (!IsSetupStillValid())
                return;

            playback = new ActiveVideoPlayback(videoComponent, _audioDevices);
            // Must be in the scene tree so _Process can present video frames.
            // ActiveCue is a GodotObject (not a Node); parent under the active cue bar.
            _activeCueBar.AddChild(playback);
            await playback.InitAsync();

            // Stop/cancel during open: drop parented orphan (not yet in component maps).
            if (!IsSetupStillValid())
            {
                DiscardOrphanVideoPlayback(playback);
                playback = null;
                return;
            }

            // UI
            PanelContainer componentPanel = CreateStyledComponentProgressBar(showMeter: playback.UseAudio);
            _componentContainer.AddChild(componentPanel);
            componentPanel.GetNode<Label>("%ComponentLabel").Text = Path.GetFileName(videoComponent.VideoFile);
            var typeIcon = componentPanel.GetNode<TextureRect>("%ComponentIcon");
            var pauseButton = componentPanel.GetNode<Button>("%ComponentPause");
            var stopButton = componentPanel.GetNode<Button>("%ComponentStop");
            var timeLabel = componentPanel.GetNode<Label>("%ComponentTime");
            if (videoComponent.IsImage && videoComponent.Duration <= 0)
                timeLabel.Text = "∞";
            else
                timeLabel.Text = UiUtilities.FormatTime(videoComponent.TotalDuration);

            typeIcon.Texture = _activeCueBar.GetThemeIcon(
                videoComponent.IsImage ? "Image" : "Video", "AtlasIcons");
            pauseButton.Icon = _activeCueBar.GetThemeIcon(_isPaused ? "Play" : "Pause", "AtlasIcons");
            stopButton.Icon = _activeCueBar.GetThemeIcon("Stop", "AtlasIcons");

            // Component Logic
            _activeVideoComponents.Add(componentPanel, playback);
            _componentToVideo.Add(componentPanel, videoComponent);
            var registeredPlayback = playback;
            playback = null; // ownership transferred to maps / Cleanup

            pauseButton.Pressed += () =>
            {
                if (_isCleaned || !IsInstanceValid(componentPanel)) return;
                if (!_activeVideoComponents.TryGetValue(componentPanel, out var pb) || pb == null)
                    return;
                if (!pb.IsPaused)
                    pb.Pause();
                else
                    pb.Resume();

                if (IsInstanceValid(pauseButton) && IsInstanceValid(_activeCueBar))
                    pauseButton.Icon = _activeCueBar.GetThemeIcon(pb.IsPaused ? "Play" : "Pause", "AtlasIcons");
            };

            // Stop
            stopButton.Pressed += async () => await StopVideoComponent(componentPanel);
            
            // Progress bar seeking (span includes playcount when not looping)
            var progressBar = componentPanel.GetNode<ProgressBar>(ComponentProgressPath);
            double pendingContentSeekSec = 0;
            double videoSeekSpan = videoComponent.Loop || videoComponent.TotalDuration < 0
                ? (videoComponent.Duration > 0 ? videoComponent.Duration : Math.Max(0, registeredPlayback.GetDuration()))
                : Math.Max(0, videoComponent.TotalDuration);
            progressBar.GuiInput += (@event) =>
            {
                if (!_activeVideoComponents.TryGetValue(componentPanel, out var seekPb) || seekPb == null)
                    return;
                if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
                {
                    if (mb.Pressed)
                    {
                        seekPb.IsSeeking = true;
                        var localPos = progressBar.GetLocalMousePosition();
                        // Still images held until stopped have no seekable timeline.
                        if (videoComponent.IsImage && videoSeekSpan <= 0)
                        {
                            seekPb.IsSeeking = false;
                            return;
                        }
                        float percent = Mathf.Clamp(localPos.X / progressBar.Size.X, 0f, 1f);
                        pendingContentSeekSec = percent * videoSeekSpan;
                        progressBar.Value = percent * 100; // Preview this row only
                        timeLabel.Text = UiUtilities.FormatTime(pendingContentSeekSec);
                    }
                    else
                    {
                        if (seekPb.IsSeeking)
                        {
                            seekPb.SeekToTotalContentSeconds(pendingContentSeekSec);
                            // Head bar follows only once the seek is committed (mouse up).
                            SyncHeadTimelineFromComponentSeek(pendingContentSeekSec);
                            GD.Print($"ActiveCue:ProgressBar - Sought to content {pendingContentSeekSec:F3}s on release");
                        }
                        seekPb.IsSeeking = false;
                    }
                }
                else if (@event is InputEventMouseMotion && seekPb.IsSeeking)
                {
                    var localPos = progressBar.GetLocalMousePosition();
                    float percent = Mathf.Clamp(localPos.X / progressBar.Size.X, 0f, 1f);
                    pendingContentSeekSec = percent * videoSeekSpan;
                    progressBar.Value = percent * 100;
                    timeLabel.Text = UiUtilities.FormatTime(pendingContentSeekSec);
                }
            };
            ulong videoPanelId = componentPanel.GetInstanceId();
            // Capture id only, the panel may be freed before deferred UI runs. One cached Callable,
            // queued at most once per frame with the latest time (no per-frame allocation).
            double latestVideoTime = 0;
            bool videoUiQueued = false;
            var videoUiUpdate = Callable.From(() =>
            {
                videoUiQueued = false;
                UpdateVideoUiByPanelId(latestVideoTime, videoPanelId);
            });
            registeredPlayback.TimeUpdated += time =>
            {
                latestVideoTime = time;
                if (videoUiQueued)
                    return;
                videoUiQueued = true;
                videoUiUpdate.CallDeferred();
            };
            
            _activeComponentCount++;
            WireVideoCompleted(registeredPlayback, componentPanel);
        }
        catch (Exception ex)
        {
            DiscardOrphanVideoPlayback(playback);
            GD.Print($"ActiveCue:SetupVideoComponent - Exception: {ex.Message}, Stack: {ex.StackTrace}, Target: {ex.TargetSite}, {ex.InnerException}, {ex.Source}");
            if (IsMissingFileException(ex, videoComponent.VideoFile))
                ReportMissingMedia(videoComponent.VideoFile);
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Error activating video component for cue {_cue.Name}: {ex.Message}", 2);
        }
    }

    /// <summary>
    /// Subscribes to video completion without capturing a freeable PanelContainer into CallDeferred args.
    /// </summary>
    private void WireVideoCompleted(ActiveVideoPlayback playback, PanelContainer componentPanel)
    {
        ulong panelId = componentPanel.GetInstanceId();
        ActiveVideoPlayback.CompletedEventHandler handler = () =>
        {
            Callable.From(() => CompleteVideoByPanelId(panelId)).CallDeferred();
        };
        playback.Completed += handler;
        _videoCompleteHandlers.Add((playback, handler));
    }

    private void CompleteVideoByPanelId(ulong panelId)
    {
        if (_isCleaned || !IsInstanceValid(this)) return;

        PanelContainer panel = FindLivePanelKey(_activeVideoComponents.Keys, panelId);
        if (panel == null)
        {
            CheckForCueCompletion();
            return;
        }

        _activeVideoComponents.Remove(panel);
        _componentToVideo.Remove(panel);
        if (IsInstanceValid(panel))
            panel.QueueFree();

        // Closed-caption text holds until stopped; end it with the video so the cue can finish.
        StopSubtitleSlaveTextComponents();
        CheckForCueCompletion();
    }

    /// <summary>
    /// Stops text playbooks that are slaves of video closed captions.
    /// </summary>
    private void StopSubtitleSlaveTextComponents()
    {
        foreach (var kv in _activeTextComponents.ToList())
        {
            var playback = kv.Value;
            if (playback == null || !IsInstanceValid(playback) || !playback.IsSubtitleSlave)
                continue;
            try
            {
                _ = playback.Stop(0);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveCue:StopSubtitleSlaveTextComponents - {ex.Message}");
            }
        }
    }

    private void UpdateVideoUiByPanelId(double time, ulong panelId)
    {
        if (_isCleaned || !IsInstanceValid(this)) return;
        PanelContainer panel = FindLivePanelKey(_activeVideoComponents.Keys, panelId);
        if (panel == null) return;
        UpdateVideoUi(time, panel);
    }

    /// <summary>
    /// Prepares a text overlay for presentation on the target layer.
    /// </summary>
    /// <param name="textComponent">Text component to set up.</param>
    private Task SetupTextComponent(TextComponent textComponent)
    {
        ActiveTextPlayback playback = null;
        try
        {
            // No await here, but still race with Cleanup running on another stack after WhenAll start.
            if (!IsSetupStillValid())
                return Task.CompletedTask;

            textComponent.RecalculateDuration();

            playback = new ActiveTextPlayback(textComponent);
            // Must be in the scene tree so _Process can drive the hold timer.
            _activeCueBar.AddChild(playback);
            playback.Init();

            if (!IsSetupStillValid())
            {
                DiscardOrphanTextPlayback(playback);
                playback = null;
                return Task.CompletedTask;
            }

            PanelContainer componentPanel = CreateStyledComponentProgressBar();
            _componentContainer.AddChild(componentPanel);
            componentPanel.GetNode<Label>("%ComponentLabel").Text = textComponent.GetDisplayLabel();
            var typeIcon = componentPanel.GetNode<TextureRect>("%ComponentIcon");
            var pauseButton = componentPanel.GetNode<Button>("%ComponentPause");
            var stopButton = componentPanel.GetNode<Button>("%ComponentStop");
            var timeLabel = componentPanel.GetNode<Label>("%ComponentTime");

            if (textComponent.Duration <= 0)
                timeLabel.Text = "∞";
            else
                timeLabel.Text = UiUtilities.FormatTime(textComponent.TotalDuration);

            typeIcon.Texture = _activeCueBar.GetThemeIcon("Text", "AtlasIcons");

            pauseButton.Icon = _activeCueBar.GetThemeIcon(_isPaused ? "Play" : "Pause", "AtlasIcons");
            stopButton.Icon = _activeCueBar.GetThemeIcon("Stop", "AtlasIcons");

            _activeTextComponents.Add(componentPanel, playback);
            _componentToText.Add(componentPanel, textComponent);
            var registeredPlayback = playback;
            playback = null;

            pauseButton.Pressed += () =>
            {
                if (_isCleaned || !IsInstanceValid(componentPanel)) return;
                if (!_activeTextComponents.TryGetValue(componentPanel, out var pb) || pb == null)
                    return;
                if (!pb.IsPaused)
                    pb.Pause();
                else
                    pb.Resume();

                if (IsInstanceValid(pauseButton) && IsInstanceValid(_activeCueBar))
                    pauseButton.Icon = _activeCueBar.GetThemeIcon(pb.IsPaused ? "Play" : "Pause", "AtlasIcons");
            };

            stopButton.Pressed += async () => await StopTextComponent(componentPanel);

            ulong textPanelId = componentPanel.GetInstanceId();
            // Cached Callable, queued at most once per frame with the latest time.
            double latestTextTime = 0;
            bool textUiQueued = false;
            var textUiUpdate = Callable.From(() =>
            {
                textUiQueued = false;
                UpdateTextUiByPanelId(latestTextTime, textPanelId);
            });
            registeredPlayback.TimeUpdated += time =>
            {
                latestTextTime = time;
                if (textUiQueued)
                    return;
                textUiQueued = true;
                textUiUpdate.CallDeferred();
            };

            _activeComponentCount++;
            WireTextCompleted(registeredPlayback, componentPanel);
        }
        catch (Exception ex)
        {
            DiscardOrphanTextPlayback(playback);
            GD.PrintErr($"ActiveCue:SetupTextComponent - Exception: {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Error activating text component for cue {_cue.Name}: {ex.Message}", 2);
        }

        return Task.CompletedTask;
    }

    private async Task TriggerTextComponent(TextComponent textComp)
    {
        PanelContainer panel = null;
        try
        {
            panel = _componentToText.FirstOrDefault(kv => kv.Value == textComp).Key;
            if (panel == null)
            {
                GD.PrintErr($"ActiveCue:TriggerTextComponent - No playback found for text on {_cue.Name}");
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"No playback for text component in cue {_cue.Name}", 2);
                return;
            }

            if (!_activeTextComponents.TryGetValue(panel, out var playback) || playback == null)
                return;

            double fadeIn = _controlFadeInDuration ?? textComp.FadeInDuration;
            if (fadeIn > 1e-9)
                await playback.FadeInAsync(fadeIn);
            else
                await playback.PlayAsync();

            if (_isPaused)
                playback.Pause();
            SyncPauseTransportUi();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveCue:TriggerTextComponent - Error: {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Trigger failed for text in {_cue.Name}: {ex.Message}", 2);
            if (panel != null && _activeTextComponents.TryGetValue(panel, out var failedPlayback))
            {
                try { _ = failedPlayback.Stop(0); } catch { /* best-effort */ }
            }
        }
    }

    private async Task StopTextComponent(PanelContainer componentPanel)
    {
        if (_isCleaned || componentPanel == null || !IsInstanceValid(componentPanel))
            return;
        if (!_activeTextComponents.TryGetValue(componentPanel, out var playback) || playback == null)
            return;

        await playback.Stop(_settings.StopFadeDuration, _settings.StopFadeCurve);
    }

    private void WireTextCompleted(ActiveTextPlayback playback, PanelContainer componentPanel)
    {
        ulong panelId = componentPanel.GetInstanceId();
        ActiveTextPlayback.CompletedEventHandler handler = () =>
        {
            Callable.From(() => CompleteTextByPanelId(panelId)).CallDeferred();
        };
        playback.Completed += handler;
        _textCompleteHandlers.Add((playback, handler));
    }

    private void CompleteTextByPanelId(ulong panelId)
    {
        if (_isCleaned || !IsInstanceValid(this)) return;

        PanelContainer panel = FindLivePanelKey(_activeTextComponents.Keys, panelId);
        if (panel == null)
        {
            CheckForCueCompletion();
            return;
        }

        _activeTextComponents.Remove(panel);
        _componentToText.Remove(panel);
        if (IsInstanceValid(panel))
            panel.QueueFree();
        CheckForCueCompletion();
    }

    private void UpdateTextUiByPanelId(double time, ulong panelId)
    {
        if (_isCleaned || !IsInstanceValid(this)) return;
        PanelContainer panel = FindLivePanelKey(_activeTextComponents.Keys, panelId);
        if (panel == null) return;
        UpdateTextUi(time, panel);
    }

    private void UpdateTextUi(double time, PanelContainer componentPanel)
    {
        if (_isCleaned || !IsInstanceValid(this)) return;
        if (!IsInstanceValid(componentPanel)) return;
        if (!_activeTextComponents.TryGetValue(componentPanel, out var textPlayback) || textPlayback == null)
            return;
        if (!_componentToText.TryGetValue(componentPanel, out var textComponent) || textComponent == null)
            return;

        var progressBar = componentPanel.GetNodeOrNull<ProgressBar>(ComponentProgressPath);
        if (progressBar == null) return;
        if (textPlayback.IsStopped) return;

        UpdateComponentFadeProgress(
            componentPanel,
            textPlayback.IsFadingIn,
            textPlayback.IsFadingOut,
            textPlayback.CurrentFadeLevel);

        if (textPlayback.IsPaused && !textPlayback.IsFadingOut)
            return;

        double span = textPlayback.GetDuration();
        float progressPercentage;
        if (span <= 0)
            progressPercentage = 0f;
        else
            progressPercentage = (float)(time / span * 100.0);

        var timeLabel = componentPanel.GetNodeOrNull<Label>(ComponentTimePath);
        if (timeLabel != null)
        {
            if (span <= 0)
                timeLabel.Text = "∞";
            else
                timeLabel.Text = UiUtilities.FormatTime(time);
        }

        progressBar.Value = progressPercentage;
    }

    private static PanelContainer FindLivePanelKey(IEnumerable<PanelContainer> keys, ulong panelId)
    {
        foreach (var key in keys)
        {
            if (key != null && IsInstanceValid(key) && key.GetInstanceId() == panelId)
                return key;
        }
        return null;
    }


    private Task SetupCueLightComponent(CueLightComponent cueLightComponent)
    {
        try
        {
            if (!IsSetupStillValid())
                return Task.CompletedTask;

            // Settings restore replaces cue light objects: use the live one for this id.
            var liveCueLight = _activeCueList.GetNodeOrNull<GlobalData>("/root/GlobalData")?
                .CueLightManager?.GetCueLight(cueLightComponent.CueLightId);
            if (liveCueLight != null)
                cueLightComponent.CueLight = liveCueLight;

            PanelContainer componentPanel = CreateStyledComponentProgressBar();
            _componentContainer.AddChild(componentPanel);
            var labelText = $"{cueLightComponent.CueLight.Name} : {cueLightComponent.Action.ToString()}";
            componentPanel.GetNode<Label>("%ComponentLabel").Text = labelText;
            var typeIcon = componentPanel.GetNode<TextureRect>("%ComponentIcon");
            componentPanel.GetNode<Button>("%ComponentPause").QueueFree(); // No pause implemented
            var stopButton = componentPanel.GetNode<Button>("%ComponentStop");
            var timeLabel = componentPanel.GetNode<Label>("%ComponentTime");
            timeLabel.Text = UiUtilities.FormatTime(cueLightComponent.CountInTime);
            
            typeIcon.Texture = _activeCueBar.GetThemeIcon("Connection", "AtlasIcons");
            stopButton.Icon = _activeCueBar.GetThemeIcon("Stop", "AtlasIcons");
            
            _activeCueLightComponents.Add(componentPanel, cueLightComponent);
            
            _activeComponentCount++;

            stopButton.Pressed += () => HandleCueLightComponentCompleted(componentPanel);
        }
        catch (Exception ex)
        {
            GD.Print($"ActiveCue:SetupCueLightComponent - Exception: {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Error setting up cuelight component for cue {_cue.Name}: {ex.Message}", 2);
        }

        return Task.CompletedTask;
    }
    
    private Task SetupOscComponent(OscComponent oscComponent)
    {
        try
        {
            if (!IsSetupStillValid() || oscComponent == null)
                return Task.CompletedTask;

            // Relink if the live connection was dropped or replaced (load, settings undo, redo or
            // recall rebuild the connections with the same ids and free the old objects).
            if (oscComponent.OscConnectionId != 0)
            {
                var liveConnection = OscConnections.GetCueOscConnection(oscComponent.OscConnectionId);
                if (liveConnection != null || !IsInstanceValid(oscComponent.OscConnection))
                    oscComponent.OscConnection = liveConnection;
            }

            if (oscComponent.OscConnection == null)
            {
                // Do not abort the rest of component setup — show a missing-connection shell and continue.
                string missingName = oscComponent.OscConnectionId != 0
                    ? $"missing OSC id {oscComponent.OscConnectionId}"
                    : "no OSC connection";
                GD.PrintErr(
                    $"ActiveCue:SetupOscComponent - {_cue?.Name}: {missingName}; skipping live send UI.");
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    $"OSC component on \"{_cue?.Name}\" has no connection ({missingName}).", 1);

                PanelContainer missingPanel = CreateStyledComponentProgressBar();
                _componentContainer.AddChild(missingPanel);
                var missingLabel = missingPanel.GetNodeOrNull<Label>("%ComponentLabel");
                if (missingLabel != null)
                {
                    string msg = string.IsNullOrEmpty(oscComponent.OscMessage)
                        ? "(no path)"
                        : oscComponent.OscMessage;
                    missingLabel.Text = UiLocalizer.Tf("[Missing OSC] {0}", msg);
                }
                var typeIconMissing = missingPanel.GetNodeOrNull<TextureRect>("%ComponentIcon");
                if (typeIconMissing != null && _activeCueBar != null)
                    typeIconMissing.Texture = _activeCueBar.GetThemeIcon("Connection", "AtlasIcons");
                missingPanel.GetNodeOrNull<Button>("%ComponentPause")?.QueueFree();
                var stopMissing = missingPanel.GetNodeOrNull<Button>("%ComponentStop");
                missingPanel.GetNodeOrNull<Label>("%ComponentTime")?.QueueFree();
                if (stopMissing != null && _activeCueBar != null)
                {
                    stopMissing.Icon = _activeCueBar.GetThemeIcon("Stop", "AtlasIcons");
                    stopMissing.Pressed += () => HandleOscComponentCompleted(missingPanel);
                }
                _activeOscComponents.Add(missingPanel, oscComponent);
                _activeComponentCount++;
                return Task.CompletedTask;
            }

            PanelContainer componentPanel = CreateStyledComponentProgressBar();
            _componentContainer.AddChild(componentPanel);
            string connName = oscComponent.OscConnection.Name ?? "OSC";
            string path = oscComponent.OscMessage ?? string.Empty;
            componentPanel.GetNode<Label>("%ComponentLabel").Text = $"{connName} : {path}";
            var typeIcon = componentPanel.GetNode<TextureRect>("%ComponentIcon");
            componentPanel.GetNode<Button>("%ComponentPause").QueueFree(); // No pause implemented
            var stopButton = componentPanel.GetNode<Button>("%ComponentStop");
            componentPanel.GetNode<Label>("%ComponentTime").QueueFree();
            typeIcon.Texture = _activeCueBar.GetThemeIcon("Connection", "AtlasIcons");
            stopButton.Icon = _activeCueBar.GetThemeIcon("Stop", "AtlasIcons");
            
            _activeOscComponents.Add(componentPanel, oscComponent);
            
            _activeComponentCount++;

            stopButton.Pressed += () => HandleOscComponentCompleted(componentPanel);
        }
        catch (Exception ex)
        {
            GD.Print($"ActiveCue:SetupOscComponent - Exception: {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Error setting up osc component for cue {_cue.Name}: {ex.Message}", 2);
        }

        return Task.CompletedTask;
    }

    private Task SetupMidiOutputComponent(MidiOutputComponent midiComponent)
    {
        try
        {
            if (!IsSetupStillValid())
                return Task.CompletedTask;

            PanelContainer componentPanel = CreateStyledComponentProgressBar();
            _componentContainer.AddChild(componentPanel);
            componentPanel.GetNode<Label>("%ComponentLabel").Text = midiComponent.GetDisplaySummary();
            var typeIcon = componentPanel.GetNode<TextureRect>("%ComponentIcon");
            componentPanel.GetNode<Button>("%ComponentPause").QueueFree();
            var stopButton = componentPanel.GetNode<Button>("%ComponentStop");
            componentPanel.GetNode<Label>("%ComponentTime").QueueFree();
            try
            {
                typeIcon.Texture = _activeCueBar.GetThemeIcon("Connection", "AtlasIcons");
            }
            catch { /* optional */ }
            stopButton.Icon = _activeCueBar.GetThemeIcon("Stop", "AtlasIcons");

            _activeMidiOutputComponents.Add(componentPanel, midiComponent);
            _activeComponentCount++;
            stopButton.Pressed += () => HandleMidiOutputComponentCompleted(componentPanel);
        }
        catch (Exception ex)
        {
            GD.Print($"ActiveCue:SetupMidiOutputComponent - Exception: {ex.Message}");
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Error setting up MIDI output for cue {_cue.Name}: {ex.Message}", 2);
        }

        return Task.CompletedTask;
    }

    private Task SetupControlComponent(ControlComponent controlComponent)
    {
        try
        {
            if (!IsSetupStillValid())
                return Task.CompletedTask;

            PanelContainer componentPanel = CreateStyledComponentProgressBar();
            _componentContainer.AddChild(componentPanel);

            controlComponent.ResolveTargetIfNeeded();
            string targetLabel = controlComponent.Action == ControlAction.TranslateLayer
                ? (controlComponent.TargetLayerId >= 0
                    ? $"layer {controlComponent.TargetLayerId}"
                    : "(no layer)")
                : (controlComponent.TargetCueId >= 0
                    ? $"#{controlComponent.TargetCueNum} (id {controlComponent.TargetCueId})"
                    : "(no target)");
            componentPanel.GetNode<Label>("%ComponentLabel").Text =
                $"{ControlComponent.GetActionDisplayName(controlComponent.Action)} → {targetLabel}";

            var typeIcon = componentPanel.GetNode<TextureRect>("%ComponentIcon");
            componentPanel.GetNode<Button>("%ComponentPause").QueueFree();
            var stopButton = componentPanel.GetNode<Button>("%ComponentStop");
            var timeLabel = componentPanel.GetNodeOrNull<Label>("%ComponentTime");

            float sessionStop = _settings?.StopFadeDuration ?? 0f;
            double timedDur = controlComponent.GetContentDurationSeconds(sessionStop);
            bool timed = timedDur > 1e-9;

            if (!timed)
            {
                timeLabel?.QueueFree();
            }
            else
            {
                if (timeLabel != null)
                    timeLabel.Text = "0.0";
                var progressBar = componentPanel.GetNodeOrNull<ProgressBar>(ComponentProgressPath);
                if (progressBar != null)
                    progressBar.Value = 0;
                _controlTimedProgress[componentPanel] = new ControlTimedProgress
                {
                    DurationSec = timedDur,
                    StartMsec = 0,
                    Started = false
                };
            }

            typeIcon.Texture = _activeCueBar.GetThemeIcon("Target", "AtlasIcons");
            stopButton.Icon = _activeCueBar.GetThemeIcon("Stop", "AtlasIcons");

            _activeControlComponents.Add(componentPanel, controlComponent);
            _activeComponentCount++;

            stopButton.Pressed += () => HandleControlComponentCompleted(componentPanel);
        }
        catch (Exception ex)
        {
            GD.Print($"ActiveCue:SetupControlComponent - Exception: {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Error setting up control component for cue {_cue.Name}: {ex.Message}", 2);
        }

        return Task.CompletedTask;
    }

    private Task SetupTimecodeControlComponent(TimecodeControlComponent timecodeComponent)
    {
        try
        {
            if (!IsSetupStillValid())
                return Task.CompletedTask;

            PanelContainer componentPanel = CreateStyledComponentProgressBar();
            _componentContainer.AddChild(componentPanel);
            componentPanel.GetNode<Label>("%ComponentLabel").Text = timecodeComponent.GetDisplaySummary();
            var typeIcon = componentPanel.GetNode<TextureRect>("%ComponentIcon");
            componentPanel.GetNode<Button>("%ComponentPause").QueueFree();
            var stopButton = componentPanel.GetNode<Button>("%ComponentStop");
            var timeLabel = componentPanel.GetNodeOrNull<Label>("%ComponentTime");

            double timedDur = timecodeComponent.GetContentDurationSeconds();
            bool timed = timedDur > 1e-9;
            if (!timed)
            {
                timeLabel?.QueueFree();
            }
            else if (timeLabel != null)
            {
                timeLabel.Text = "0.0";
                var progressBar = componentPanel.GetNodeOrNull<ProgressBar>(ComponentProgressPath);
                if (progressBar != null)
                    progressBar.Value = 0;
                _controlTimedProgress[componentPanel] = new ControlTimedProgress
                {
                    DurationSec = timedDur,
                    StartMsec = 0,
                    Started = false
                };
                WireTimecodeProgressSeek(componentPanel, timecodeComponent, progressBar, timeLabel, timedDur);
            }

            try
            {
                typeIcon.Texture = _activeCueBar.GetThemeIcon("Connection", "AtlasIcons");
            }
            catch { /* optional */ }
            stopButton.Icon = _activeCueBar.GetThemeIcon("Stop", "AtlasIcons");

            _activeTimecodeControlComponents.Add(componentPanel, timecodeComponent);
            _activeComponentCount++;
            stopButton.Pressed += () => HandleTimecodeControlComponentCompleted(componentPanel);
        }
        catch (Exception ex)
        {
            GD.Print($"ActiveCue:SetupTimecodeControlComponent - Exception: {ex.Message}");
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Error setting up timecode control for cue {_cue.Name}: {ex.Message}", 2);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Scrubs a Play Duration row: preview on drag, commit generator time on release.
    /// </summary>
    private void WireTimecodeProgressSeek(
        PanelContainer componentPanel,
        TimecodeControlComponent timecodeComponent,
        ProgressBar progressBar,
        Label timeLabel,
        double durationSec)
    {
        if (progressBar == null || timecodeComponent == null || durationSec <= 1e-9)
            return;

        double pendingSec = 0;
        progressBar.GuiInput += (@event) =>
        {
            if (!_activeTimecodeControlComponents.ContainsKey(componentPanel))
                return;
            if (!_controlTimedProgress.TryGetValue(componentPanel, out var state) || !state.Started)
                return;

            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    state.IsSeeking = true;
                    _controlTimedProgress[componentPanel] = state;
                    var localPos = progressBar.GetLocalMousePosition();
                    float percent = Mathf.Clamp(localPos.X / Math.Max(1f, progressBar.Size.X), 0f, 1f);
                    pendingSec = percent * durationSec;
                    PreviewTimecodeProgressSeek(progressBar, timeLabel, pendingSec, durationSec);
                }
                else if (state.IsSeeking)
                {
                    ApplyTimecodePlaySeek(
                        componentPanel, timecodeComponent, pendingSec, durationSec, syncHead: true);
                }
            }
            else if (@event is InputEventMouseMotion motion && state.IsSeeking)
            {
                var localPos = progressBar.GetLocalMousePosition();
                float percent = Mathf.Clamp(localPos.X / Math.Max(1f, progressBar.Size.X), 0f, 1f);
                pendingSec = percent * durationSec;
                PreviewTimecodeProgressSeek(progressBar, timeLabel, pendingSec, durationSec);
                if ((motion.ButtonMask & MouseButtonMask.Left) == 0)
                {
                    ApplyTimecodePlaySeek(
                        componentPanel, timecodeComponent, pendingSec, durationSec, syncHead: true);
                }
            }
        };
    }

    /// <summary>
    /// Previews Play Duration on the component row and the parent head bar while dragging.
    /// </summary>
    private void PreviewTimecodeProgressSeek(
        ProgressBar progressBar,
        Label timeLabel,
        double pendingSec,
        double durationSec)
    {
        pendingSec = Math.Clamp(pendingSec, 0, durationSec);
        if (progressBar != null)
            progressBar.Value = durationSec > 1e-9 ? pendingSec / durationSec * 100.0 : 0;
        if (timeLabel != null)
            timeLabel.Text = UiUtilities.FormatTime(pendingSec);

        ApplyHeadProgressDisplay(
            _preWaitSecondsHonored + pendingSec,
            GetPlayableTimelineDuration());
    }

    /// <summary>
    /// Jumps the generator to origin + <paramref name="pendingSec"/> and rebases the progress clock.
    /// When <paramref name="syncHead"/> is true, the parent cue bar follows immediately.
    /// </summary>
    private void ApplyTimecodePlaySeek(
        PanelContainer componentPanel,
        TimecodeControlComponent timecodeComponent,
        double pendingSec,
        double durationSec,
        bool syncHead)
    {
        if (!_controlTimedProgress.TryGetValue(componentPanel, out var state))
            return;

        pendingSec = Math.Clamp(pendingSec, 0, durationSec);
        var manager = _activeCueList?.GetNodeOrNull<TimecodeManager>("/root/TimecodeManager");
        if (manager != null && timecodeComponent.EndpointId >= 0)
        {
            double origin = timecodeComponent.RuntimePlayOriginElapsed;
            if (origin < 0)
                origin = 0;
            manager.SetElapsedSeconds(timecodeComponent.EndpointId, origin + pendingSec);
            if (pendingSec + 1e-4 >= durationSec)
                manager.Pause(timecodeComponent.EndpointId);
        }

        state.IsSeeking = false;
        state.StartMsec = Time.GetTicksMsec() - (ulong)Math.Max(0, pendingSec * 1000.0);
        _controlTimedProgress[componentPanel] = state;

        if (syncHead)
            SyncHeadTimelineFromComponentSeek(pendingSec);

        GD.Print($"ActiveCue:ApplyTimecodePlaySeek - {pendingSec:F3}s / {durationSec:F3}s");
    }

    /// <summary>
    /// Content-local elapsed for a running Play Duration row (generator clock, wall-clock fallback).
    /// </summary>
    private double GetTimecodePlayElapsedSeconds(
        TimecodeControlComponent timecodeComponent,
        ControlTimedProgress state)
    {
        if (timecodeComponent == null || state.DurationSec <= 1e-9)
            return 0;
        if (!state.Started)
            return 0;

        double origin = timecodeComponent.RuntimePlayOriginElapsed;
        if (origin < 0)
            origin = 0;

        var manager = _activeCueList?.GetNodeOrNull<TimecodeManager>("/root/TimecodeManager");
        if (manager != null && timecodeComponent.EndpointId >= 0)
        {
            var endpoint = manager.FindEndpoint(timecodeComponent.EndpointId);
            if (endpoint != null)
                return Math.Clamp(endpoint.ElapsedSeconds - origin, 0, state.DurationSec);
        }

        double wall = (Time.GetTicksMsec() - state.StartMsec) / 1000.0;
        return Math.Clamp(wall, 0, state.DurationSec);
    }

    /// <summary>
    /// Seeks Play Duration generators to a content-local time (parent head scrub).
    /// </summary>
    private void SeekTimecodeComponentsToContentTime(double contentTimeSeconds)
    {
        if (contentTimeSeconds < 0)
            contentTimeSeconds = 0;

        foreach (var kv in _activeTimecodeControlComponents.ToList())
        {
            var component = kv.Value;
            if (component == null)
                continue;
            if (component.Action != TimecodeControlAction.PlayForDuration)
                continue;
            if (!_controlTimedProgress.TryGetValue(kv.Key, out var state) || !state.Started)
                continue;
            if (state.DurationSec <= 1e-9)
                continue;

            ApplyTimecodePlaySeek(
                kv.Key,
                component,
                Math.Clamp(contentTimeSeconds, 0, state.DurationSec),
                state.DurationSec,
                syncHead: false);
        }
    }

    /// <summary>
    /// True while the user is dragging a Play Duration component bar.
    /// </summary>
    private bool AnyTimecodeComponentSeeking()
    {
        foreach (var state in _controlTimedProgress.Values)
        {
            if (state.IsSeeking)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Updates progress bar + elapsed time for a Play Duration timecode row from the generator clock.
    /// </summary>
    private void UpdateTimecodeControlComponentUiState(
        PanelContainer componentPanel,
        TimecodeControlComponent timecodeComponent)
    {
        if (!IsInstanceValid(componentPanel) || timecodeComponent == null) return;
        if (!_controlTimedProgress.TryGetValue(componentPanel, out var state)) return;
        if (state.DurationSec <= 1e-9) return;
        if (state.IsSeeking) return;

        var progressBar = componentPanel.GetNodeOrNull<ProgressBar>(ComponentProgressPath);
        var timeLabel = componentPanel.GetNodeOrNull<Label>(ComponentTimePath);
        if (progressBar == null) return;

        if (!state.Started)
        {
            progressBar.Value = 0;
            if (timeLabel != null)
                timeLabel.Text = "0.0";
            return;
        }

        double elapsed = GetTimecodePlayElapsedSeconds(timecodeComponent, state);
        float pct = state.DurationSec > 1e-9
            ? (float)(elapsed / state.DurationSec * 100.0)
            : 100f;
        progressBar.Value = Math.Clamp(pct, 0f, 100f);
        if (timeLabel != null)
            timeLabel.Text = UiUtilities.FormatTime(elapsed);
    }

    /// <summary>
    /// Updates progress bar + elapsed time for a timed control component (fade / translate / stop fade).
    /// </summary>
    private void UpdateControlComponentUiState(PanelContainer componentPanel, ControlComponent controlComponent)
    {
        if (!IsInstanceValid(componentPanel)) return;
        if (!_controlTimedProgress.TryGetValue(componentPanel, out var state)) return;
        if (state.DurationSec <= 1e-9) return;
        if (state.IsSeeking) return;

        var progressBar = componentPanel.GetNodeOrNull<ProgressBar>(ComponentProgressPath);
        var timeLabel = componentPanel.GetNodeOrNull<Label>(
            ComponentTimePath);
        if (progressBar == null) return;

        if (!state.Started)
        {
            progressBar.Value = 0;
            if (timeLabel != null)
                timeLabel.Text = "0.0";
            return;
        }

        double elapsed = (Time.GetTicksMsec() - state.StartMsec) / 1000.0;
        elapsed = Math.Clamp(elapsed, 0.0, state.DurationSec);
        float pct = state.DurationSec > 1e-9
            ? (float)(elapsed / state.DurationSec * 100.0)
            : 100f;
        progressBar.Value = Math.Clamp(pct, 0f, 100f);
        if (timeLabel != null)
            timeLabel.Text = UiUtilities.FormatTime(elapsed);
    }

    private async Task StopComponent(PanelContainer componentPanel)
    {
        if (_activeAudioInputs.TryGetValue(componentPanel, out var inputPlayback))
        {
            await inputPlayback.Stop(_settings.StopFadeDuration, _settings.StopFadeCurve);
            return;
        }
        if (!_activeAudioComponents.TryGetValue(componentPanel, out var playback))
            return;
        await playback.Stop(_settings.StopFadeDuration, _settings.StopFadeCurve);
    }

    private async Task StopVideoComponent(PanelContainer componentPanel)
    {
        if (!_activeVideoComponents.TryGetValue(componentPanel, out var playback))
            return;
        StopSubtitleSlaveTextComponents();
        await playback.Stop(_settings.StopFadeDuration, _settings.StopFadeCurve);
    }
    
    private void UpdateComponentUiState(PanelContainer componentPanel, AudioComponent audioComponent)
    {
        if (!IsInstanceValid(componentPanel) || audioComponent == null) return;
        if (!_activeAudioComponents.TryGetValue(componentPanel, out var audioPlayback) || audioPlayback == null)
            return;

        UpdateComponentMeter(componentPanel, audioPlayback, true);

        var progressBar = componentPanel.GetNodeOrNull<ProgressBar>(ComponentProgressPath);
        if (progressBar == null) return;
        if (audioPlayback.IsStopped) return;

        // Fade overlay updates even while paused (e.g. mid fade-in/out).
        UpdateComponentFadeProgress(componentPanel, audioPlayback.IsFadingIn, audioPlayback.IsFadingOut,
            audioPlayback.CurrentVolume);

        if (audioPlayback.IsPaused) return;

        // Content elapsed includes completed playcount iterations (does not reset on replay).
        double contentElapsed = audioPlayback.GetTotalElapsedContentSeconds();
        double progressSpan = audioComponent.GetProgressSpanSeconds();
        float progressPercentage = progressSpan > 1e-9
            ? (float)(contentElapsed / progressSpan * 100.0)
            : 0f;
        var timeLabel = componentPanel.GetNode<Label>(ComponentTimePath);
        // Hold bar while user scrubs or async decoder seek is still in flight.
        if (!audioPlayback.IsSeeking && !audioPlayback.IsDecoderSeeking)
        {
            timeLabel.Text = UiUtilities.FormatTime(contentElapsed);
            progressBar.Value = progressPercentage;
        }
    }



    private void UpdateVideoUi(double time, PanelContainer componentPanel)
    {
        if (_isCleaned || !IsInstanceValid(this)) return;
        if (!IsInstanceValid(componentPanel)) return;
        if (!_activeVideoComponents.TryGetValue(componentPanel, out var videoPlayback) || videoPlayback == null)
            return;
        if (!_componentToVideo.TryGetValue(componentPanel, out var videoComponent) || videoComponent == null)
            return;

        var progressBar = componentPanel.GetNodeOrNull<ProgressBar>(ComponentProgressPath);
        if (progressBar == null) return;
        if (videoPlayback.IsStopped) return;

        UpdateComponentFadeProgress(componentPanel, videoPlayback.IsFadingIn, videoPlayback.IsFadingOut,
            videoPlayback.CurrentVolume);

        if (videoPlayback.IsPaused) return;

        double contentElapsed = videoPlayback.GetTotalElapsedContentSeconds();
        float progressPercentage;
        if (videoComponent.IsImage && videoComponent.Duration <= 0)
        {
            // Image held until stopped — no finite progress span.
            progressPercentage = 0f;
        }
        else
        {
            double progressSpan = videoComponent.Loop || videoComponent.TotalDuration < 0
                ? (videoComponent.Duration > 0 ? videoComponent.Duration : videoPlayback.GetDuration())
                : videoComponent.TotalDuration;
            progressPercentage = progressSpan > 1e-9
                ? (float)(contentElapsed / progressSpan * 100.0)
                : 0f;
        }
        var timeLabel = componentPanel.GetNode<Label>(ComponentTimePath);
        // Hold bar while user scrubs or async decoder seek is still in flight.
        if (!videoPlayback.IsSeeking && !videoPlayback.IsDecoderSeeking)
        {
            timeLabel.Text = UiUtilities.FormatTime(contentElapsed);
            progressBar.Value = progressPercentage;
        }
    }

    /// <summary>
    /// Shows or hides the 2px output-level strip and writes the latest peak.
    /// </summary>
    /// <param name="panel">Component progress row.</param>
    /// <param name="level">Playback that reports output peaks.</param>
    /// <param name="hasAudio">False hides the strip (silent video, text, OSC, …).</param>
    private void UpdateComponentMeter(Node panel, IComponentLevel level, bool hasAudio)
    {
        if (panel == null || !IsInstanceValid(panel) || level == null)
            return;
        var meter = panel.GetNodeOrNull<LevelMeter>("%ComponentMeter");
        if (meter == null)
            return;

        bool enabled = hasAudio && (_settings?.ShowPlaybackMeters ?? true);
        if (meter.Visible != enabled)
            meter.Visible = enabled;
        if (!enabled)
            return;
        meter.Level = level.ReadDisplayLevel(ComponentMeterTickSeconds);
    }

    /// <summary>
    /// Shows/hides the vertical component fade overlay and sets its fill from current volume.
    /// </summary>
    /// <param name="componentPanel">Component progress row.</param>
    /// <param name="fadingIn">True while fade-in is active.</param>
    /// <param name="fadingOut">True while fade-out is active.</param>
    /// <param name="currentVolume">Playback volume in [0, 1].</param>
    private static void UpdateComponentFadeProgress(
        PanelContainer componentPanel,
        bool fadingIn,
        bool fadingOut,
        float currentVolume)
    {
        if (componentPanel == null || !IsInstanceValid(componentPanel)) return;

        var fadeProgress = componentPanel.GetNodeOrNull<ProgressBar>("%ComponentFadeProgress");
        if (fadeProgress == null) return;

        if (fadingIn || fadingOut)
        {
            fadeProgress.Visible = true;
            // Remaining "unfaded" cover: full at silent, empty at full volume.
            fadeProgress.Value = (1f - Mathf.Clamp(currentVolume, 0f, 1f)) * 100f;
        }
        else
        {
            fadeProgress.Visible = false;
        }
    }
}
