// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Cue2.Domain.Cues;
using Cue2.Services;
using Cue2.Media.Audio;
using Cue2.Media.Decoders;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.UI.Preview;

/// <summary>
/// Lightweight video inspector preview using pull-based <see cref="VideoSourceDecoder"/>.
/// Presentation is driven by a wall-clock master on the main thread; seek/decode/prefetch
/// run on workers so scrubbing does not freeze the UI.
/// </summary>
public partial class VideoPreviewer : Control
{
    private GlobalData _globalData;
    private MediaEngine _mediaEngine;
    private GlobalSignals _globalSignals;

    private ImageTexture _godotTexture;
    private Image _godotImage;
    private byte[] _displayRgba;
    private byte[] _sourceRgba;
    private int _sourceWidth;
    private int _sourceHeight;

    private VideoSourceDecoder _decoder;
    private bool _isExiting;
    private bool _isPlaying;
    private bool _updatingFromDecoder;
    private readonly Stopwatch _clock = new Stopwatch();
    private long _mediaOriginUs;

    private Label _currentTimeLabel;
    private Button _playPauseButton;
    private ProgressBar _seekProgressBar;

    private Control _viewArea;
    private Panel _canvasArea;
    private Control _layerHost;
    private TextureRect _previewTextRect;

    private int _layerId = -1;
    private bool _areasDirty = true;
    private float _previewScale = 1f;
    private TextureRect.ExpandModeEnum _expandMode = TextureRect.ExpandModeEnum.IgnoreSize;
    private TextureRect.StretchModeEnum _stretchMode = TextureRect.StretchModeEnum.KeepAspectCentered;

    private bool _isDraggingProgress;

    /// <summary>Cancels in-flight scrub/loop seek workers when a newer seek supersedes them.</summary>
    private CancellationTokenSource _seekCts;
    /// <summary>True while a seek worker owns the decoder (present path must not touch it).</summary>
    private volatile bool _seekInProgress;
    /// <summary>Single-flight background prefetch so _Process never decodes on the main thread.</summary>
    private int _prefetchRunning;

    public override void _Ready()
    {
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _mediaEngine = GetNode<MediaEngine>("/root/MediaEngine");
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");

        _currentTimeLabel = GetNode<Label>("%CurrentTimeLabel");
        _playPauseButton = GetNode<Button>("%PlayPauseButton");
        _playPauseButton.Icon = GetThemeIcon("Play", "AtlasIcons");
        _playPauseButton.Pressed += OnPlayPausePressed;
        _seekProgressBar = GetNode<ProgressBar>("%SeekProgressBar");
        _seekProgressBar.MaxValue = 100;
        _seekProgressBar.GuiInput += OnProgressGuiInput;

        _viewArea = GetNode<Control>("%ViewArea");
        _canvasArea = GetNode<Panel>("%CanvasArea");
        _layerHost = GetNode<Control>("%LayerHost");
        _previewTextRect = GetNode<TextureRect>("%PreviewTextRect");

        _viewArea.ClipContents = true;
        _canvasArea.ClipContents = true;
        _canvasArea.MouseFilter = MouseFilterEnum.Ignore;
        _layerHost.ClipContents = true;
        _layerHost.MouseFilter = MouseFilterEnum.Ignore;
        _previewTextRect.MouseFilter = MouseFilterEnum.Ignore;

        _viewArea.Resized += OnViewResized;

        _godotImage = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        _godotTexture = ImageTexture.CreateFromImage(_godotImage);
        _previewTextRect.Texture = _godotTexture;

        SetProcess(false);
    }

    public void LoadDecoder(string file)
    {
        ClearDecoder();
        _decoder = new VideoSourceDecoder();
        _playPauseButton.Icon = GetThemeIcon("Play", "AtlasIcons");
        _isPlaying = false;
        string resolved = _globalData?.ResolveMediaPath(file) ?? file;
        OpenAsync(resolved);
    }

    private void OpenAsync(string file)
    {
    	TaskUtil.Run(() => OpenWorkAsync(file), "VideoPreviewer.OpenAsync");
    }

    private async Task OpenWorkAsync(string file)
    {
        try
        {
            await _decoder.OpenAsync(file);
            if (_isExiting || _decoder == null) return;
            // Prefetch + first frame off main (decode can be multi-ms).
            // Image / display buffer are created in UploadFromSource at canvas preview size.
            var decoder = _decoder;
            await Task.Run(() => decoder.Prefetch(4));
            if (_isExiting || _decoder != decoder) return;
            if (_decoder.TryTakeFrame(out var frame))
                PresentAndRelease(frame);
            _mediaOriginUs = 0;
            UpdateTimeLabel(0);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"VideoPreviewer:LoadDecoder - {ex.Message}");
            // Drop half-open decoder / texture so repeated failed opens do not leak natives.
            ClearDecoder();
        }
    }

    public void SetAreasDeferred(int layerId)
    {
        _layerId = layerId;
        _areasDirty = true;
        CallDeferred(nameof(RefreshLayout));
    }

    /// <summary>
    /// Applies TextureRect expand + stretch modes for the inspector preview.
    /// </summary>
    /// <remarks>
    /// The preview TextureRect is parented to a layer-sized host inside the canvas clip,
    /// matching house <see cref="VideoComponent.ApplyTextureLayout"/> (fill parent on IgnoreSize).
    /// Parent clip keeps Keep / Covered / KeepSize drawing inside the canvas outline.
    /// </remarks>
    public void ApplyTextureLayout(TextureRect.ExpandModeEnum expand, TextureRect.StretchModeEnum stretch)
    {
        _expandMode = expand;
        _stretchMode = stretch;
        ApplyStoredTextureLayout();
    }

    /// <summary>
    /// Applies this component's texture layout to the preview.
    /// </summary>
    public void ApplyTextureLayout(VideoComponent component)
    {
        if (component == null)
            return;
        ApplyTextureLayout(component.TextureExpandMode, component.TextureStretchMode);
    }

    /// <summary>
    /// Applies opacity (0–1) to the inspector preview.
    /// </summary>
    public void ApplyOpacity(float opacity)
    {
        if (_previewTextRect == null || !IsInstanceValid(_previewTextRect))
            return;
        float a = Mathf.Clamp(opacity, 0f, 1f);
        _previewTextRect.Modulate = new Color(1f, 1f, 1f, a);
    }

    /// <inheritdoc />
    public override void _Notification(int what)
    {
        if (what == NotificationVisibilityChanged && IsVisibleInTree() && _areasDirty)
            CallDeferred(nameof(RefreshLayout));
    }

    private void OnViewResized()
    {
        _areasDirty = true;
        if (IsVisibleInTree())
            CallDeferred(nameof(RefreshLayout));
    }

    /// <summary>
    /// Fits the scaled canvas and layer host into the view, then re-applies expand/stretch.
    /// </summary>
    private void RefreshLayout()
    {
        if (_isExiting || _viewArea == null || _canvasArea == null || _layerHost == null
            || !IsInstanceValid(_viewArea) || !IsInstanceValid(_canvasArea) || !IsInstanceValid(_layerHost))
            return;

        var viewSize = _viewArea.Size;
        if (viewSize.X < 1f || viewSize.Y < 1f)
        {
            _areasDirty = true;
            return;
        }

        var canvas = DisplaysManager.Canvas;
        if (canvas == null)
        {
            _layerHost.Visible = false;
            _areasDirty = false;
            return;
        }

        var canvasSize = new Vector2(canvas.CanvasSize.X, canvas.CanvasSize.Y);
        if (canvasSize.X < 1f || canvasSize.Y < 1f)
            canvasSize = new Vector2(1920, 1080);

        float scale = Mathf.Min(viewSize.X / canvasSize.X, viewSize.Y / canvasSize.Y);
        if (scale <= 0f)
            scale = 1f;

        _previewScale = scale;
        var scaledCanvas = canvasSize * scale;
        _canvasArea.Size = scaledCanvas;
        _canvasArea.Position = Vector2.Zero;
        _canvasArea.ClipContents = true;

        var layer = DisplaysManager.GetLayerById(_layerId);
        if (layer == null || _layerId < 0)
        {
            _layerHost.Visible = false;
            _areasDirty = false;
            UpdateSeekBarWidth(scaledCanvas.X);
            return;
        }

        _layerHost.Visible = true;
        _layerHost.ClipContents = true;
        // LayerHost is a child of CanvasArea, so position is canvas-local (layer * scale).
        _layerHost.Position = new Vector2(layer.CanvasPosition.X, layer.CanvasPosition.Y) * scale;
        _layerHost.Size = new Vector2(layer.Size.X, layer.Size.Y) * scale;

        // Texture pixels follow preview scale so Keep Size / Fit Width match the drawn canvas.
        UploadFromSource();
        ApplyStoredTextureLayout();
        UpdateSeekBarWidth(scaledCanvas.X);
        _areasDirty = false;
    }

    /// <summary>
    /// Applies stored expand/stretch. Ignore Size fills the layer host; Keep Size / Fit Width /
    /// Fit Height use the preview-scaled texture size (same ratio as house canvas pixels).
    /// </summary>
    private void ApplyStoredTextureLayout()
    {
        if (_previewTextRect == null || !IsInstanceValid(_previewTextRect))
            return;
        _previewTextRect.Position = Vector2.Zero;
        VideoComponent.ApplyTextureLayout(_previewTextRect, _expandMode, _stretchMode);
    }

    private void UpdateSeekBarWidth(float scaledCanvasWidth)
    {
        if (_seekProgressBar == null || !IsInstanceValid(_seekProgressBar))
            return;
        float barWidth = Mathf.Max(0f, scaledCanvasWidth - 93f);
        _seekProgressBar.CustomMinimumSize = new Vector2(barWidth, _seekProgressBar.CustomMinimumSize.Y);
    }

    public override void _Process(double delta)
    {
        if (_isExiting || !_isPlaying || _decoder == null) return;

        // During async seek, hold the scrub/target position — do not sample the old clock.
        if (_seekInProgress)
            return;

        long masterUs = _mediaOriginUs + _clock.ElapsedMilliseconds * 1000;
        UpdateTimeLabel(masterUs / 1_000_000.0);

        if (_decoder.Info != null && _decoder.Info.DurationUs > 0 && masterUs >= _decoder.Info.DurationUs)
        {
            // Loop: seek on a worker (GOP discard must not run on the main thread).
            QueueSeek(0, presentAfter: false, restartClock: true);
            return;
        }

        // Ring-only present: never DecodeMore / Prefetch on the main thread.
        int n = 0;
        int lateDrops = 0;
        const int maxPresent = 3;
        const int maxLateDrops = 8;
        while (n < maxPresent && _decoder.TryPeekPtsBuffered(out long pts))
        {
            if (pts > masterUs + 8000) break;
            if (!_decoder.TryTakeFrame(out var frame)) break;
            if (masterUs - pts > 80_000
                && lateDrops < maxLateDrops
                && _decoder.TryPeekPtsBuffered(out long p2)
                && p2 <= masterUs)
            {
                // Late drop: return buffer to pool without uploading.
                _decoder.ReleaseFrameBuffer(frame.Rgba);
                lateDrops++;
                n++;
                continue;
            }
            PresentAndRelease(frame);
            n++;
        }

        if (_decoder.BufferedFrames < 3 && !_decoder.EndOfStream)
            RequestPrefetch(6);
    }

    /// <summary>
    /// Fire-and-forget single-flight prefetch on a worker thread.
    /// </summary>
    private void RequestPrefetch(int targetFrames)
    {
        var decoder = _decoder;
        if (decoder == null || _seekInProgress) return;
        if (Interlocked.CompareExchange(ref _prefetchRunning, 1, 0) != 0) return;

        Task.Run(() =>
        {
            try
            {
                if (_isExiting || _seekInProgress || _decoder != decoder) return;
                decoder.Prefetch(targetFrames);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"VideoPreviewer:Prefetch - {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _prefetchRunning, 0);
            }
        });
    }

    /// <summary>
    /// Queues a cancellable seek+prefetch on a worker, then optionally presents the first frame.
    /// </summary>
    private void QueueSeek(long timestampUs, bool presentAfter, bool restartClock)
    {
        var decoder = _decoder;
        if (decoder == null || _isExiting) return;

        try { _seekCts?.Cancel(); } catch { /* ignore */ }
        try { _seekCts?.Dispose(); } catch { /* ignore */ }
        var cts = new CancellationTokenSource();
        _seekCts = cts;
        _seekInProgress = true;

        _mediaOriginUs = timestampUs;
        if (restartClock || _isPlaying)
            _clock.Restart();

        // Pin progress UI to the seek target immediately (worker may take many ms).
        double durSec = (decoder.Info?.DurationUs ?? 0) / 1_000_000.0;
        if (!_isDraggingProgress && IsInstanceValid(_seekProgressBar) && IsInstanceValid(_currentTimeLabel))
        {
            double t = timestampUs / 1_000_000.0;
            _currentTimeLabel.Text = UiUtilities.FormatTime(t);
            _seekProgressBar.Value = durSec > 0 ? t / durSec * 100 : 0;
        }

        Task.Run(() =>
        {
            try
            {
                cts.Token.ThrowIfCancellationRequested();
                decoder.Seek(timestampUs);
                cts.Token.ThrowIfCancellationRequested();
                decoder.Prefetch(4);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"VideoPreviewer:SeekWorker - {ex.Message}");
            }
            finally
            {
                if (!cts.IsCancellationRequested)
                {
                    bool present = presentAfter;
                    long us = timestampUs;
                    Callable.From(() => FinishPreviewSeek(cts, decoder, present, us)).CallDeferred();
                }
            }
        }, cts.Token);
    }

    private void FinishPreviewSeek(CancellationTokenSource cts, VideoSourceDecoder decoder, bool presentAfter, long timestampUs)
    {
        if (cts == null || cts.IsCancellationRequested) return;
        if (!ReferenceEquals(_seekCts, cts)) return;
        if (_isExiting || _decoder != decoder)
        {
            _seekInProgress = false;
            return;
        }

        try
        {
            if (presentAfter && decoder.TryTakeFrame(out var frame))
                PresentAndRelease(frame, decoder);
            // Re-assert target time after seek (in case a race updated labels).
            UpdateTimeLabel(timestampUs / 1_000_000.0);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"VideoPreviewer:FinishPreviewSeek - {ex.Message}");
        }
        finally
        {
            _seekInProgress = false;
        }
    }

    /// <summary>
    /// Copies the frame into the display texture, then always returns the decoder
    /// RGBA buffer to the pool (same contract as house <see cref="ActiveVideoPlayback"/>).
    /// </summary>
    /// <param name="frame">Frame taken from the decoder ring (ownership until release).</param>
    /// <param name="decoder">
    /// Decoder that owns the pool. Defaults to <see cref="_decoder"/>; pass explicitly
    /// when finishing an async seek so a swapped decoder cannot miss the release.
    /// </param>
    private void PresentAndRelease(VideoFrame frame, VideoSourceDecoder decoder = null)
    {
        var owner = decoder ?? _decoder;
        try
        {
            Present(frame);
        }
        finally
        {
            // Always return the ring buffer after copy (or on Present failure / exit).
            owner?.ReleaseFrameBuffer(frame?.Rgba);
        }
    }

    /// <summary>
    /// Copies frame pixels into the Godot texture. Does not free <see cref="VideoFrame.Rgba"/> —
    /// callers must use <see cref="PresentAndRelease"/> or <see cref="VideoSourceDecoder.ReleaseFrameBuffer"/>.
    /// </summary>
    private void Present(VideoFrame frame)
    {
        if (frame?.Rgba == null || _isExiting) return;

        int srcW = frame.Width;
        int srcH = frame.Height;
        if (srcW < 1 || srcH < 1)
            return;

        int needed = srcW * srcH * 4;
        if (_sourceRgba == null || _sourceRgba.Length < needed)
            _sourceRgba = new byte[needed];
        Buffer.BlockCopy(frame.Rgba, 0, _sourceRgba, 0, needed);
        _sourceWidth = srcW;
        _sourceHeight = srcH;

        UploadFromSource();
    }

    /// <summary>
    /// Uploads the last native frame at canvas preview scale so TextureRect expand/stretch
    /// min-size (Keep Size, Fit Width, Fit Height) matches the drawn canvas, not native pixels.
    /// </summary>
    private void UploadFromSource()
    {
        if (_sourceRgba == null || _sourceWidth < 1 || _sourceHeight < 1 || _isExiting)
            return;

        float scale = _previewScale > 0.0001f ? _previewScale : 1f;
        int srcW = _sourceWidth;
        int srcH = _sourceHeight;
        int dstW = Math.Max(1, (int)Math.Round(srcW * scale));
        int dstH = Math.Max(1, (int)Math.Round(srcH * scale));
        int neededDst = dstW * dstH * 4;
        // Image.SetData uses the array Length, so this buffer must be exactly dstW*dstH*4.
        EnsureDisplayBuffer(neededDst);

        if (dstW == srcW && dstH == srcH)
        {
            Buffer.BlockCopy(_sourceRgba, 0, _displayRgba, 0, neededDst);
        }
        else
        {
            byte[] src = _sourceRgba;
            for (int y = 0; y < dstH; y++)
            {
                int srcY = Math.Min(srcH - 1, (y * srcH) / dstH);
                int srcRow = srcY * srcW * 4;
                int dstRow = y * dstW * 4;
                for (int x = 0; x < dstW; x++)
                {
                    int srcX = Math.Min(srcW - 1, (x * srcW) / dstW);
                    int si = srcRow + srcX * 4;
                    int di = dstRow + x * 4;
                    _displayRgba[di] = src[si];
                    _displayRgba[di + 1] = src[si + 1];
                    _displayRgba[di + 2] = src[si + 2];
                    _displayRgba[di + 3] = src[si + 3];
                }
            }
        }

        bool sizeChanged = _godotImage == null || !IsInstanceValid(_godotImage)
            || _godotImage.GetWidth() != dstW || _godotImage.GetHeight() != dstH;
        if (sizeChanged)
        {
            _godotImage = Image.CreateEmpty(dstW, dstH, false, Image.Format.Rgba8);
            _godotTexture = ImageTexture.CreateFromImage(_godotImage);
            if (_previewTextRect != null && IsInstanceValid(_previewTextRect))
                _previewTextRect.Texture = _godotTexture;
        }

        _godotImage.SetData(dstW, dstH, false, Image.Format.Rgba8, _displayRgba);
        _godotTexture.Update(_godotImage);

        if (sizeChanged)
            ApplyStoredTextureLayout();
    }

    /// <summary>
    /// Ensures <see cref="_displayRgba"/> is exactly <paramref name="byteCount"/> long.
    /// Godot <see cref="Image.SetData"/> rejects a buffer that is larger than width×height×4.
    /// </summary>
    private void EnsureDisplayBuffer(int byteCount)
    {
        if (_displayRgba != null && _displayRgba.Length == byteCount)
            return;
        if (_displayRgba != null)
            MediaMemory.NoteReleased(MediaMemory.ByteBufferBytes(_displayRgba));
        _displayRgba = new byte[byteCount];
    }

    private void OnPlayPausePressed()
    {
        if (_decoder == null) return;
        if (_isPlaying)
        {
            _isPlaying = false;
            _clock.Stop();
            SetProcess(false);
            _playPauseButton.Icon = GetThemeIcon("Play", "AtlasIcons");
        }
        else
        {
            _isPlaying = true;
            _clock.Start();
            SetProcess(true);
            _playPauseButton.Icon = GetThemeIcon("Pause", "AtlasIcons");
        }
    }

    private void UpdateTimeLabel(double time)
    {
        if (_isExiting || _isDraggingProgress || !IsInstanceValid(_currentTimeLabel) || !IsInstanceValid(_seekProgressBar)) return;
        _updatingFromDecoder = true;
        _currentTimeLabel.Text = UiUtilities.FormatTime(time);
        double dur = (_decoder?.Info?.DurationUs ?? 0) / 1_000_000.0;
        _seekProgressBar.Value = dur > 0 ? time / dur * 100 : 0;
        _updatingFromDecoder = false;
    }

    private void OnProgressGuiInput(InputEvent @event)
    {
        if (_isExiting || _decoder?.Info == null || !IsInstanceValid(_seekProgressBar)) return;

        if (@event is InputEventMouseButton mouseButton)
        {
            if (mouseButton.ButtonIndex == MouseButton.Left)
            {
                if (mouseButton.Pressed)
                {
                    _isDraggingProgress = true;
                    UpdateProgressFromMouse();
                }
                else if (_isDraggingProgress)
                {
                    _isDraggingProgress = false;
                    double dur = _decoder.Info.DurationUs / 1_000_000.0;
                    double time = (_seekProgressBar.Value / 100) * dur;
                    long us = (long)(time * 1_000_000);
                    // Scrub release: seek+decode on worker so the inspector UI stays responsive.
                    QueueSeek(us, presentAfter: true, restartClock: true);
                }
            }
        }
        else if (@event is InputEventMouseMotion && _isDraggingProgress)
        {
            UpdateProgressFromMouse();
        }
    }

    private void UpdateProgressFromMouse()
    {
        var localPos = _seekProgressBar.GetLocalMousePosition();
        float percent = Mathf.Clamp(localPos.X / _seekProgressBar.Size.X, 0f, 1f);
        _seekProgressBar.Value = percent * 100;
        double dur = (_decoder?.Info?.DurationUs ?? 0) / 1_000_000.0;
        double time = percent * dur;
        _currentTimeLabel.Text = UiUtilities.FormatTime(time);
    }

    public void ClearDecoder()
    {
        SetProcess(false);
        _isPlaying = false;
        _clock.Reset();
        try { _seekCts?.Cancel(); } catch { /* ignore */ }
        try { _seekCts?.Dispose(); } catch { /* ignore */ }
        _seekCts = null;
        _seekInProgress = false;
        if (_decoder != null)
        {
            _decoder.Dispose();
            _decoder = null;
        }
        if (_displayRgba != null)
        {
            MediaMemory.NoteReleased(MediaMemory.ByteBufferBytes(_displayRgba));
            _displayRgba = null;
        }
        if (_sourceRgba != null)
        {
            MediaMemory.NoteReleased(MediaMemory.ByteBufferBytes(_sourceRgba));
            _sourceRgba = null;
        }
        _sourceWidth = 0;
        _sourceHeight = 0;
        if (_previewTextRect != null && IsInstanceValid(_previewTextRect))
            _previewTextRect.Texture = null;
        if (_godotTexture != null && IsInstanceValid(_godotTexture))
        {
            try { _godotTexture.Dispose(); } catch { /* ignore */ }
            _godotTexture = null;
        }
        if (_godotImage != null && IsInstanceValid(_godotImage))
        {
            try { _godotImage.Dispose(); } catch { /* ignore */ }
            _godotImage = null;
        }
        if (_seekProgressBar != null && IsInstanceValid(_seekProgressBar))
            _seekProgressBar.Value = 0;
        // Do not allocate a placeholder while the inspector is leaving the tree —
        // those Image / ImageTexture objects leaked as "resources still in use at exit".
        if (!_isExiting)
        {
            _godotImage = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
            _godotTexture = ImageTexture.CreateFromImage(_godotImage);
            if (_previewTextRect != null && IsInstanceValid(_previewTextRect))
                _previewTextRect.Texture = _godotTexture;
        }
        MediaMemory.ReclaimIfNeeded();
    }

    /// <summary>Stops decode and drops GPU resources without creating a placeholder frame.</summary>
    public void ShutdownPreview()
    {
        _isExiting = true;
        ClearDecoder();
    }

    public override void _ExitTree()
    {
        if (_viewArea != null && IsInstanceValid(_viewArea))
            _viewArea.Resized -= OnViewResized;
        ShutdownPreview();
        base._ExitTree();
    }
}
