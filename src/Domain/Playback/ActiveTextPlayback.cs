// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cue2.Domain.Cues;
using Cue2.Services;
using Godot;

namespace Cue2.Domain.Playback;

/// <summary>
/// Runtime presentation of a <see cref="TextComponent"/> on all video outputs.
/// </summary>
/// <remarks>
/// Creates a DisplayLayer host per output (same pattern as video), hides the TextureRect,
/// and parents a <see cref="RichTextLabel"/> (plus optional background) clipped to the layer rect.
/// Duration 0 holds until stop; finite duration completes via wall clock.
/// Stop uses the session stop-fade (and component fade-out) with main-thread opacity updates,
/// matching <see cref="ActiveVideoPlayback"/>.
/// </remarks>
public partial class ActiveTextPlayback : Node
{
    private const int FadeUpdateIntervalMs = 16;

    private readonly TextComponent _textComponent;
    private readonly List<Control> _layerHosts = new();
    private readonly List<RichTextLabel> _labels = new();
    private readonly List<ColorRect> _backgrounds = new();

    private readonly Stopwatch _wallClock = new();
    private double _holdDurationSec;
    private double _elapsedAtPause;
    private float _fadeAlpha = 1f;
    private bool _isPlaying;
    private bool _isExiting;
    private bool _completedEmitted;
    private bool _isFadingOut;
    private bool _isFadingIn;
    /// <summary>True once natural end-fade has been scheduled (prevents repeated arms).</summary>
    private bool _naturalEndFadeArmed;
    private CancellationTokenSource _fadeCts;

    /// <summary>
    /// When set, display this text instead of <see cref="TextComponent.Content"/>
    /// (used for closed captions linked from a video component).
    /// </summary>
    private string _liveTextOverride;

    /// <summary>
    /// When true, this playback is driven by video closed captions and should not
    /// keep the cue alive after the linked video ends (duration treated as slave).
    /// </summary>
    public bool IsSubtitleSlave { get; set; }

    /// <summary>True while a live text override (e.g. CC) is active.</summary>
    public bool HasLiveTextOverride => _liveTextOverride != null;

    /// <summary>True when playback has been stopped or cleaned up.</summary>
    public bool IsStopped { get; private set; }

    /// <summary>True while transport is paused.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>True while a fade-in is in progress.</summary>
    public bool IsFadingIn => _isFadingIn;

    /// <summary>True while a fade-out is in progress.</summary>
    public bool IsFadingOut => _isFadingOut;

    /// <summary>
    /// Current visual level in [0, 1] (opacity × fade alpha). Used by active-bar fade progress UI.
    /// </summary>
    public float CurrentFadeLevel =>
        Mathf.Clamp(_textComponent?.Opacity ?? 1f, 0f, 1f) * Mathf.Clamp(_fadeAlpha, 0f, 1f);

    /// <summary>Current fade alpha (0–1).</summary>
    public float CurrentFadeAlpha => _fadeAlpha;

    /// <summary>Raised when the hold ends or stop cleanup finishes.</summary>
    [Signal]
    public delegate void CompletedEventHandler();

    /// <summary>Raised each process tick with elapsed seconds while playing.</summary>
    [Signal]
    public delegate void TimeUpdatedEventHandler(double time);

    /// <summary>
    /// Creates a playback bound to the given text component.
    /// </summary>
    /// <param name="textComponent">Document model for this activation.</param>
    public ActiveTextPlayback(TextComponent textComponent)
    {
        _textComponent = textComponent ?? throw new ArgumentNullException(nameof(textComponent));
        _holdDurationSec = Math.Max(0, textComponent.Duration);
    }

    /// <summary>
    /// Whether this playback is driven by the given component instance.
    /// </summary>
    public bool UsesTextComponent(TextComponent component) =>
        component != null && ReferenceEquals(_textComponent, component);

    /// <summary>
    /// Builds layer hosts and RichTextLabels on every video output.
    /// </summary>
    public void Init()
    {
        if (_textComponent.TargetLayerId < 0)
        {
            GD.Print($"ActiveTextPlayback:Init - No output (TargetLayerId={_textComponent.TargetLayerId}).");
            return;
        }

        var layer = DisplaysManager.GetLayerById(_textComponent.TargetLayerId);
        if (layer == null)
        {
            GD.PrintErr(
                $"ActiveTextPlayback:Init - Target layer {_textComponent.TargetLayerId} not found.");
            return;
        }

        if (DisplaysManager.Outputs == null || DisplaysManager.Outputs.Count == 0)
        {
            GD.PrintErr("ActiveTextPlayback:Init - No video outputs available.");
            return;
        }

        foreach (var display in DisplaysManager.Outputs)
        {
            if (display == null || !IsInstanceValid(display))
                continue;

            try
            {
                var host = display.AddLayer(_textComponent.TargetLayerId);
                if (host == null || !IsInstanceValid(host))
                    continue;

                var textureRect = host.GetNodeOrNull<TextureRect>("%LayerOutput");
                if (textureRect != null && IsInstanceValid(textureRect))
                    textureRect.Visible = false;

                var background = new ColorRect
                {
                    Name = "TextBackground",
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    Visible = false
                };
                host.AddChild(background);

                var label = new RichTextLabel
                {
                    Name = "TextDisplay",
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                    ScrollActive = false,
                    FitContent = false,
                    ClipContents = true
                };
                host.AddChild(label);

                _layerHosts.Add(host);
                _backgrounds.Add(background);
                _labels.Add(label);

                host.TreeExited += () => OnHostExited(host);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"ActiveTextPlayback:Init - Failed on output {display.OutputName}: {ex.Message}");
            }
        }

        // Stay invisible until PlayAsync / FadeInAsync (content is applied at Init).
        _fadeAlpha = 0f;
        RefreshVisualProperties();
        SetProcess(false);

        GD.Print(
            $"ActiveTextPlayback:Init - hosts={_layerHosts.Count} layer={_textComponent.TargetLayerId}");
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (!_isPlaying || IsPaused || IsStopped || _isExiting)
            return;

        // Keep fade-out visuals pumping on the main thread (images do this in PresentCatchUpFrames).
        if (_isFadingOut || _isFadingIn)
            ApplyOpacityModulate();

        double elapsed = _elapsedAtPause + _wallClock.Elapsed.TotalSeconds;
        EmitSignal(SignalName.TimeUpdated, elapsed);

        // Do not auto-complete while stop-fading — let Stop/FadeOut finish the fade.
        if (_isFadingOut)
            return;

        // Finite hold only: arm end-fade when remaining time enters FadeOutDuration window
        // (e.g. 10s hold + 4s fade → fade begins at t=6s).
        if (_holdDurationSec > 1e-9)
        {
            TryArmNaturalEndFade(elapsed);
            if (_isFadingOut)
                return;

            if (elapsed >= _holdDurationSec)
                CallDeferred(nameof(CompleteFromEnd));
        }
    }

    /// <summary>
    /// Starts component end-fade when remaining hold time is within <see cref="TextComponent.FadeOutDuration"/>.
    /// </summary>
    /// <param name="elapsed">Elapsed presentation seconds.</param>
    private void TryArmNaturalEndFade(double elapsed)
    {
        if (IsStopped || IsPaused || _isExiting || _isFadingOut || _isFadingIn
            || _naturalEndFadeArmed || !_isPlaying)
            return;

        double configured = _textComponent?.FadeOutDuration ?? 0;
        if (configured <= 1e-9 || _holdDurationSec <= 1e-9)
            return;

        double remaining = _holdDurationSec - elapsed;
        if (remaining > configured)
            return;

        _naturalEndFadeArmed = true;
        double fadeDuration = Math.Max(remaining, 1e-3);
        fadeDuration = Math.Min(fadeDuration, configured);

        GD.Print($"ActiveTextPlayback:TryArmNaturalEndFade - Starting end fade ({fadeDuration:F3}s)");
        _ = FadeOutAsync(fadeDuration);
    }

    /// <summary>
    /// Starts presentation (and hold timer when duration is finite).
    /// </summary>
    public async Task PlayAsync()
    {
        await Task.Yield();
        if (IsStopped || _isExiting)
            return;

        if (_layerHosts.Count == 0)
        {
            EmitCompletedOnce();
            Clean();
            return;
        }

        // Reveal at full opacity only when not mid fade-in (FadeInAsync pre-arms alpha to 0).
        if (!_isFadingIn)
            _fadeAlpha = 1f;

        _isPlaying = true;
        IsPaused = false;
        _elapsedAtPause = 0;
        _wallClock.Restart();
        SetProcess(true);
        ApplyOpacityModulate();
        GD.Print($"ActiveTextPlayback:PlayAsync - duration={_holdDurationSec}");
    }

    /// <summary>
    /// Starts playback with an opacity fade-in.
    /// </summary>
    /// <param name="duration">Fade seconds; ≤0 falls back to <see cref="PlayAsync"/>.</param>
    public async Task FadeInAsync(double duration)
    {
        if (duration <= 1e-9)
        {
            await PlayAsync();
            return;
        }

        CancelFadeToken();
        _fadeCts = new CancellationTokenSource();
        var token = _fadeCts.Token;

        // Arm zero opacity before PlayAsync enables process / presents.
        _isFadingIn = true;
        _isFadingOut = false;
        _fadeAlpha = 0f;
        ApplyOpacityModulate();
        await PlayAsync();

        var timer = Stopwatch.StartNew();
        try
        {
            while (timer.Elapsed.TotalSeconds < duration && !token.IsCancellationRequested && !IsStopped && !_isExiting)
            {
                float t = (float)(timer.Elapsed.TotalSeconds / duration);
                _fadeAlpha = Mathf.Clamp(t, 0f, 1f);
                ApplyOpacityModulate();
                await Task.Delay(FadeUpdateIntervalMs, token);
            }

            if (!token.IsCancellationRequested && !IsStopped && !_isExiting)
            {
                _fadeAlpha = 1f;
                ApplyOpacityModulate();
            }
        }
        catch (OperationCanceledException)
        {
            // cancelled by stop / second fade
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveTextPlayback:FadeInAsync - {ex.Message}");
        }
        finally
        {
            _isFadingIn = false;
        }
    }

    /// <summary>
    /// Pauses the hold timer.
    /// </summary>
    public void Pause()
    {
        if (IsPaused || IsStopped || !_isPlaying)
            return;

        IsPaused = true;
        _elapsedAtPause += _wallClock.Elapsed.TotalSeconds;
        _wallClock.Stop();
    }

    /// <summary>
    /// Resumes the hold timer.
    /// </summary>
    public void Resume()
    {
        if (!IsPaused || IsStopped)
            return;

        IsPaused = false;
        _wallClock.Restart();
    }

    /// <summary>
    /// Elapsed hold time in seconds.
    /// </summary>
    public double GetPlaybackTimeSeconds()
    {
        if (!_isPlaying)
            return 0;
        if (IsPaused)
            return _elapsedAtPause;
        return _elapsedAtPause + _wallClock.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// Configured hold duration; 0 means until stopped.
    /// </summary>
    public double GetDuration() => _holdDurationSec;

    /// <summary>
    /// Re-applies text, style, and layout from the component model to live labels.
    /// </summary>
    public void RefreshVisualProperties()
    {
        if (_textComponent == null || _isExiting)
            return;

        for (int i = 0; i < _labels.Count; i++)
        {
            var label = _labels[i];
            var host = i < _layerHosts.Count ? _layerHosts[i] : null;
            var background = i < _backgrounds.Count ? _backgrounds[i] : null;
            if (label == null || !IsInstanceValid(label))
                continue;

            float margin = Mathf.Max(0, _textComponent.Margins);
            TextComponent.ApplyFillWithMargins(label, margin);
            if (background != null && IsInstanceValid(background))
            {
                TextComponent.ApplyFillWithMargins(background, margin);
                background.Visible = _textComponent.BackgroundEnabled;
                background.Color = _textComponent.BackgroundColor;
            }

            _textComponent.ApplyToRichTextLabel(label, fontScale: 1f);
            ApplyLiveTextToLabel(label);
        }

        ApplyOpacityModulate();
    }

    /// <summary>
    /// Sets or clears the live display text used for closed captions.
    /// Does not modify the document <see cref="TextComponent.Content"/>.
    /// </summary>
    /// <param name="text">
    /// Caption text to show. Pass <c>null</c> to restore component content;
    /// pass empty string to show a blank caption frame.
    /// </param>
    public void SetLiveTextOverride(string text)
    {
        if (_isExiting)
            return;

        // null = clear override; empty = blank caption
        if (_liveTextOverride == text
            || (_liveTextOverride != null && text != null
                && string.Equals(_liveTextOverride, text, StringComparison.Ordinal)))
            return;

        _liveTextOverride = text;
        if (text == null)
        {
            // Restore typography + document content.
            RefreshVisualProperties();
            return;
        }

        foreach (var label in _labels)
        {
            if (label != null && IsInstanceValid(label))
                ApplyLiveTextToLabel(label);
        }
    }

    /// <summary>
    /// Clears any closed-caption override so document content shows again.
    /// </summary>
    public void ClearLiveTextOverride()
    {
        SetLiveTextOverride(null);
    }

    private void ApplyLiveTextToLabel(RichTextLabel label)
    {
        if (label == null || !IsInstanceValid(label) || _liveTextOverride == null)
            return;

        // CC is plain text even when component BBCode is enabled for static content.
        label.BbcodeEnabled = false;
        label.Text = _liveTextOverride;
    }

    /// <summary>
    /// Stops playback. First call with a positive fade uses session stop-fade / component fade-out;
    /// a second call while fading hard-stops immediately (matches video).
    /// </summary>
    /// <param name="fadeTime">Session stop-fade seconds; 0 may still use component FadeOutDuration.</param>
    public async Task Stop(double fadeTime = 0.0)
    {
        if (IsStopped)
            return;

        bool wasFadingOut = _isFadingOut;
        CancelFadeToken();

        if (wasFadingOut)
        {
            HardStop();
            return;
        }

        double fadeDuration = fadeTime > 1e-9
            ? fadeTime
            : (_textComponent?.FadeOutDuration ?? 0);

        // Allow fade even if paused — user expects stop-fade from active cues.
        if (fadeDuration > 1e-9 && !_isExiting && _layerHosts.Count > 0)
        {
            await FadeOutAsync(fadeDuration);
            return;
        }

        HardStop();
    }

    /// <summary>
    /// Tears down hosts and frees this node.
    /// </summary>
    public void Clean()
    {
        if (_isExiting)
            return;

        _isExiting = true;
        IsStopped = true;
        _isPlaying = false;
        IsPaused = false;
        _isFadingIn = false;
        _isFadingOut = false;
        SetProcess(false);
        _wallClock.Stop();
        CancelFadeToken();

        ReleaseHosts();
        EmitCompletedOnce();

        if (IsInsideTree())
            CallDeferred(Node.MethodName.QueueFree);
        else
            CallDeferred(GodotObject.MethodName.Free);
    }

    private async Task FadeOutAsync(double duration)
    {
        if (duration <= 1e-9)
        {
            HardStop();
            return;
        }

        if (IsStopped)
            return;

        _isFadingIn = false;
        _isFadingOut = true;
        _fadeCts = new CancellationTokenSource();
        var token = _fadeCts.Token;

        // Ensure process keeps applying opacity if wall clock was paused.
        if (!_isPlaying)
        {
            _isPlaying = true;
            SetProcess(true);
        }

        // Unpause hold clock visual only — do not resume duration for complete-from-end during fade.
        // Duration complete is gated by _isFadingOut in _Process.

        float startAlpha = _fadeAlpha;
        var timer = Stopwatch.StartNew();

        try
        {
            while (timer.Elapsed.TotalSeconds < duration && !token.IsCancellationRequested && !IsStopped)
            {
                float t = (float)(timer.Elapsed.TotalSeconds / duration);
                _fadeAlpha = Mathf.Lerp(startAlpha, 0f, Mathf.Clamp(t, 0f, 1f));
                // Deferred so modulate always lands on the main thread after await (video pattern).
                CallDeferred(MethodName.ApplyOpacityModulate);
                EmitSignal(SignalName.TimeUpdated, GetPlaybackTimeSeconds());
                await Task.Delay(FadeUpdateIntervalMs, token);
            }

            if (!token.IsCancellationRequested && !IsStopped)
            {
                _fadeAlpha = 0f;
                CallDeferred(MethodName.ApplyOpacityModulate);
                HardStop();
            }
        }
        catch (OperationCanceledException)
        {
            // second stop hard-cuts
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveTextPlayback:FadeOutAsync - {ex.Message}");
            if (!IsStopped)
                HardStop();
        }
        finally
        {
            _isFadingOut = false;
            CancelFadeToken();
        }
    }

    private void HardStop()
    {
        if (IsStopped && _isExiting)
            return;

        CancelFadeToken();
        IsStopped = true;
        IsPaused = false;
        _isPlaying = false;
        _isFadingIn = false;
        _isFadingOut = false;
        SetProcess(false);
        _wallClock.Stop();
        Clean();
    }

    private void CompleteFromEnd()
    {
        if (IsStopped || _isExiting || _isFadingOut)
            return;

        // Safety net if hold ended without early arm (timing miss).
        double residual = Math.Max(0, _textComponent?.FadeOutDuration ?? 0);
        if (residual > 1e-9)
        {
            GD.Print($"ActiveTextPlayback:CompleteFromEnd - Residual end fade ({residual:F3}s)");
            _ = FadeOutAsync(residual);
            return;
        }

        // Natural end without fade: hard stop (session stop-fade is only for user Stop).
        HardStop();
    }

    private void EmitCompletedOnce()
    {
        if (_completedEmitted)
            return;
        _completedEmitted = true;
        try
        {
            EmitSignal(SignalName.Completed);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"ActiveTextPlayback:EmitCompletedOnce - {ex.Message}");
        }
    }

    /// <summary>
    /// Applies opacity × fade alpha to every layer host (covers label + background together).
    /// </summary>
    private void ApplyOpacityModulate()
    {
        if (_isExiting)
            return;

        float opacity = Mathf.Clamp(_textComponent?.Opacity ?? 1f, 0f, 1f);
        float alpha = opacity * Mathf.Clamp(_fadeAlpha, 0f, 1f);
        var modulate = new Color(1f, 1f, 1f, alpha);

        foreach (var host in _layerHosts)
        {
            if (host != null && IsInstanceValid(host))
                host.Modulate = modulate;
        }
    }

    private void CancelFadeToken()
    {
        if (_fadeCts == null)
            return;
        try
        {
            if (!_fadeCts.IsCancellationRequested)
                _fadeCts.Cancel();
        }
        catch
        {
            // ignore
        }

        try { _fadeCts.Dispose(); } catch { /* ignore */ }
        _fadeCts = null;
    }

    private void OnHostExited(Control host)
    {
        int index = _layerHosts.IndexOf(host);
        if (index < 0)
            return;

        _layerHosts.RemoveAt(index);
        if (index < _labels.Count)
            _labels.RemoveAt(index);
        if (index < _backgrounds.Count)
            _backgrounds.RemoveAt(index);
    }

    private void ReleaseHosts()
    {
        foreach (var host in _layerHosts.ToList())
        {
            try
            {
                if (host != null && IsInstanceValid(host))
                    host.QueueFree();
            }
            catch
            {
                // best-effort
            }
        }

        _layerHosts.Clear();
        _labels.Clear();
        _backgrounds.Clear();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        CancelFadeToken();
        ReleaseHosts();
        base._ExitTree();
    }
}
