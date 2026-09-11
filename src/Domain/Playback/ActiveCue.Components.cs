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
            PanelContainer componentPanel = CreateStyledComponentProgressBar();
            _componentContainer.AddChild(componentPanel);
            componentPanel.GetNode<Label>("%ComponentLabel").Text = Path.GetFileName(audioComponent.AudioFile);
            var typeIcon = componentPanel.GetNode<TextureRect>("%ComponentIcon");
            var pauseButton = componentPanel.GetNode<Button>("%ComponentPause");
            var stopButton = componentPanel.GetNode<Button>("%ComponentStop");
            var timeLabel = componentPanel.GetNode<Label>("%ComponentTime");
            timeLabel.Text = UiUtilities.FormatTime(audioComponent.TotalDuration);
            
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
            var progressBar = componentPanel.GetNode<ProgressBar>("ComponentProgress");
            double pendingContentSeekSec = 0;
            double audioSeekSpan = audioComponent.Loop || audioComponent.TotalDuration < 0
                ? Math.Max(0, audioComponent.Duration)
                : Math.Max(0, audioComponent.TotalDuration);
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
                        progressBar.Value = percent * 100; // Preview
                        timeLabel.Text = UiUtilities.FormatTime(pendingContentSeekSec);
                        // Live head preview while scrubbing component.
                        SyncHeadTimelineFromComponentSeek(pendingContentSeekSec);
                    }
                    else
                    {
                        // Release: perform the seek across playcount timeline
                        if (!_activeAudioComponents.TryGetValue(componentPanel, out var seekPb) || seekPb == null)
                            return;
                        if (seekPb.IsSeeking)
                        {
                            seekPb.SeekToTotalContentSeconds(pendingContentSeekSec);
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
                    SyncHeadTimelineFromComponentSeek(pendingContentSeekSec);
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
            PanelContainer componentPanel = CreateStyledComponentProgressBar();
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
            var progressBar = componentPanel.GetNode<ProgressBar>("ComponentProgress");
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
                        progressBar.Value = percent * 100; // Preview
                        timeLabel.Text = UiUtilities.FormatTime(pendingContentSeekSec);
                        SyncHeadTimelineFromComponentSeek(pendingContentSeekSec);
                    }
                    else
                    {
                        if (seekPb.IsSeeking)
                        {
                            seekPb.SeekToTotalContentSeconds(pendingContentSeekSec);
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
                    SyncHeadTimelineFromComponentSeek(pendingContentSeekSec);
                }
            };
            ulong videoPanelId = componentPanel.GetInstanceId();
            registeredPlayback.TimeUpdated += time =>
            {
                // Capture id only — panel may be freed before deferred UI runs.
                double t = time;
                Callable.From(() => UpdateVideoUiByPanelId(t, videoPanelId)).CallDeferred();
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
            registeredPlayback.TimeUpdated += time =>
            {
                double t = time;
                Callable.From(() => UpdateTextUiByPanelId(t, textPanelId)).CallDeferred();
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

        await playback.Stop(_settings.StopFadeDuration);
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

        var progressBar = componentPanel.GetNodeOrNull<ProgressBar>("ComponentProgress");
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

        var timeLabel = componentPanel.GetNodeOrNull<Label>("ComponentProgress/MarginContainer/HBoxContainer/ComponentTime");
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

            // Relink if the live connection was dropped after load / settings restore.
            if (oscComponent.OscConnection == null && oscComponent.OscConnectionId != 0)
            {
                oscComponent.OscConnection = OscConnections.GetCueOscConnection(oscComponent.OscConnectionId);
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
                var progressBar = componentPanel.GetNodeOrNull<ProgressBar>("ComponentProgress");
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

    /// <summary>
    /// Updates progress bar + elapsed time for a timed control component (fade / translate / stop fade).
    /// </summary>
    private void UpdateControlComponentUiState(PanelContainer componentPanel, ControlComponent controlComponent)
    {
        if (!IsInstanceValid(componentPanel) || controlComponent == null) return;
        if (!_controlTimedProgress.TryGetValue(componentPanel, out var state)) return;
        if (state.DurationSec <= 1e-9) return;

        var progressBar = componentPanel.GetNodeOrNull<ProgressBar>("ComponentProgress");
        var timeLabel = componentPanel.GetNodeOrNull<Label>(
            "ComponentProgress/MarginContainer/HBoxContainer/ComponentTime");
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
        if (!_activeAudioComponents.TryGetValue(componentPanel, out var playback))
            return;
        await playback.Stop(_settings.StopFadeDuration);
    }

    private async Task StopVideoComponent(PanelContainer componentPanel)
    {
        if (!_activeVideoComponents.TryGetValue(componentPanel, out var playback))
            return;
        StopSubtitleSlaveTextComponents();
        await playback.Stop(_settings.StopFadeDuration);
    }
    
    private void UpdateComponentUiState(PanelContainer componentPanel, AudioComponent audioComponent)
    {
        if (!IsInstanceValid(componentPanel) || audioComponent == null) return;
        if (!_activeAudioComponents.TryGetValue(componentPanel, out var audioPlayback) || audioPlayback == null)
            return;

        var progressBar = componentPanel.GetNodeOrNull<ProgressBar>("ComponentProgress");
        if (progressBar == null) return;
        if (audioPlayback.IsStopped) return;

        // Fade overlay updates even while paused (e.g. mid fade-in/out).
        UpdateComponentFadeProgress(componentPanel, audioPlayback.IsFadingIn, audioPlayback.IsFadingOut,
            audioPlayback.CurrentVolume);

        if (audioPlayback.IsPaused) return;

        // Content elapsed includes completed playcount iterations (does not reset on replay).
        double contentElapsed = audioPlayback.GetTotalElapsedContentSeconds();
        double progressSpan = audioComponent.Loop || audioComponent.TotalDuration < 0
            ? audioComponent.Duration
            : audioComponent.TotalDuration;
        float progressPercentage = progressSpan > 1e-9
            ? (float)(contentElapsed / progressSpan * 100.0)
            : 0f;
        var timeLabel = componentPanel.GetNode<Label>("ComponentProgress/MarginContainer/HBoxContainer/ComponentTime");
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

        var progressBar = componentPanel.GetNodeOrNull<ProgressBar>("ComponentProgress");
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
        var timeLabel = componentPanel.GetNode<Label>("ComponentProgress/MarginContainer/HBoxContainer/ComponentTime");
        // Hold bar while user scrubs or async decoder seek is still in flight.
        if (!videoPlayback.IsSeeking && !videoPlayback.IsDecoderSeeking)
        {
            timeLabel.Text = UiUtilities.FormatTime(contentElapsed);
            progressBar.Value = progressPercentage;
        }
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
