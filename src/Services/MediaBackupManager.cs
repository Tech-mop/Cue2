// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cue2.Domain.Cuelist;
using Cue2.Domain.Playback;
using Cue2.Domain.Devices;
using Cue2.Domain.ShowSettings;
using Cue2.Domain.Metadata;
using Cue2.Domain.Cues;
using Cue2.Domain.Connections;
using Cue2.Domain.Library;
using Cue2.Domain.Commands;
using Cue2.UI.Utilities;
using Godot;

namespace Cue2.Services;

/// <summary>
/// Media type used to choose the show-local destination folder.
/// </summary>
public enum MediaBackupKind
{
    Audio,
    Video,
    Image
}

/// <summary>
/// Result of a single media backup copy job.
/// </summary>
public sealed class MediaBackupResult
{
    /// <summary>Original source path requested.</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>Absolute destination path after copy (or existing show-local path).</summary>
    public string DestinationPath { get; init; } = string.Empty;

    /// <summary>Path relative to the show session directory (e.g. <c>Audio/song.wav</c>).</summary>
    public string RelativePath { get; init; } = string.Empty;

    /// <summary>True when a new file was written; false when skipped (already local / identical).</summary>
    public bool Copied { get; init; }

    /// <summary>True when the job completed without error (including intentional skip).</summary>
    public bool Success { get; init; }

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// Background service that copies used media files into the show folder (Audio/Video/Images)
/// and rewrites cue paths to show-relative URLs (e.g. <c>Audio/song.wav</c>).
/// Respects <see cref="Settings.MediaBackupEnabled"/>. Progress is reported via
/// <see cref="GlobalSignals.MediaBackupProgress"/> for footer UI.
/// </summary>
public partial class MediaBackupManager : Node
{
    private GlobalData _globalData;
    private GlobalSignals _globalSignals;
    private SaveManager _saveManager;

    private readonly object _queueLock = new object();
    private readonly Queue<BackupJob> _queue = new Queue<BackupJob>();
    private readonly HashSet<string> _queuedSourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private int _totalJobs;
    private int _completedJobs;
    private string _currentOriginPath = string.Empty;
    private string _currentDestPath = string.Empty;
    private bool _workerRunning;
    private CancellationTokenSource _cts;

    /// <summary>
    /// Bumped on New Session / open wipe so in-flight workers and deferred callbacks
    /// never rewrite paths or silent-resave into the wrong show.
    /// </summary>
    private int _sessionEpoch;

    /// <summary>Set when any cue path was rewritten to a relative path during the current batch.</summary>
    private bool _pathsChangedThisBatch;

    /// <summary>True while at least one copy job is queued or running.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_queueLock)
                return _workerRunning || _queue.Count > 0;
        }
    }

    /// <summary>
    /// Drops queued backup jobs and invalidates the current worker generation.
    /// Used on New Session / open wipe so deferred path rewrites and silent re-saves
    /// cannot target a different show. An in-flight <see cref="File.Copy"/> may still
    /// finish writing to the previous show folder (harmless); its deferred apply is ignored.
    /// </summary>
    public void ClearPendingJobs()
    {
        CancellationTokenSource toCancel = null;
        int epoch;
        lock (_queueLock)
        {
            _queue.Clear();
            _queuedSourceKeys.Clear();
            _totalJobs = 0;
            _completedJobs = 0;
            _currentOriginPath = string.Empty;
            _currentDestPath = string.Empty;
            _pathsChangedThisBatch = false;
            _sessionEpoch++;
            epoch = _sessionEpoch;
            toCancel = _cts;
            // Leave _workerRunning alone — the worker exits on cancel/epoch and may
            // restart itself if a later enqueue left work for the new epoch.
        }

        try
        {
            toCancel?.Cancel();
        }
        catch
        {
            // ignore
        }

        GD.Print($"MediaBackupManager:ClearPendingJobs - Queue cleared, sessionEpoch={epoch}.");
        EmitProgressDeferred();
    }

    /// <summary>0–100 overall progress for the current batch.</summary>
    public float ProgressPercent
    {
        get
        {
            int total = Math.Max(1, _totalJobs);
            return Mathf.Clamp(100f * _completedJobs / total, 0f, 100f);
        }
    }

    public override void _Ready()
    {
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _saveManager = GetNodeOrNull<SaveManager>("/root/SaveManager");
        GD.Print("MediaBackupManager:_Ready - Initialized.");
    }

    public override void _ExitTree()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Scans all cues for media paths, rewrites already-local files to relative paths,
    /// and enqueues external files for backup when media backup is enabled.
    /// </summary>
    /// <param name="force">
    /// When true, copies media regardless of the show's MediaBackupEnabled setting
    /// (manual File → Show Files → Copy media…).
    /// </param>
    public void EnqueueShowMediaBackup(bool force = false)
    {
        if (!force && (_globalData?.Settings == null || !_globalData.Settings.MediaBackupEnabled))
        {
            GD.Print("MediaBackupManager:EnqueueShowMediaBackup - Skipped (media backup disabled).");
            // Still rewrite paths that are already under the show folder for portability
            if (!string.IsNullOrEmpty(_globalData?.SessionDir))
            {
                int rewritten = RewriteLocalMediaPathsToRelative();
                if (rewritten > 0)
                    RequestSilentResaveIfNeeded();
            }
            return;
        }

        if (string.IsNullOrEmpty(_globalData?.SessionDir))
        {
            GD.Print("MediaBackupManager:EnqueueShowMediaBackup - Skipped (no session directory).");
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                "Cannot copy media: no show file is open. Save the show first.", 1);
            return;
        }

        if (CueList.CueIndex == null || CueList.CueIndex.Count == 0)
        {
            GD.Print("MediaBackupManager:EnqueueShowMediaBackup - No cues to scan.");
            if (force)
                _globalSignals?.EmitSignal(nameof(GlobalSignals.Log), "No cues with media to copy.", 0);
            return;
        }

        // Ensure type folders exist (manual force may run before a normal save cycle)
        EnsureSessionTypeFolders();

        _pathsChangedThisBatch = false;

        int enqueued = 0;
        int rewrittenLocal = 0;

        foreach (var cue in CueList.CueIndex.Values)
        {
            if (cue == null) continue;

            var audio = cue.GetAudioComponent();
            if (audio != null && !string.IsNullOrEmpty(audio.AudioFile))
            {
                if (TryRewriteOrEnqueue(audio.AudioFile, MediaBackupKind.Audio, out bool queued, force))
                {
                    if (queued) enqueued++;
                    else rewrittenLocal++;
                }
            }

            var video = cue.GetVideoComponent();
            if (video != null && !string.IsNullOrEmpty(video.VideoFile))
            {
                var kind = DetectKindFromPath(video.VideoFile);
                if (kind != MediaBackupKind.Image)
                    kind = MediaBackupKind.Video;

                if (TryRewriteOrEnqueue(video.VideoFile, kind, out bool queued, force))
                {
                    if (queued) enqueued++;
                    else rewrittenLocal++;
                }
            }
        }

        if (rewrittenLocal > 0)
        {
            GD.Print($"MediaBackupManager:EnqueueShowMediaBackup - Rewrote {rewrittenLocal} already-local path(s) to relative.");
        }

        if (enqueued > 0)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                force
                    ? $"Media copy: queued {enqueued} file(s) into show folder."
                    : $"Media backup: queued {enqueued} file(s) for copy into show folder.",
                0);
            GD.Print($"MediaBackupManager:EnqueueShowMediaBackup - Queued {enqueued} job(s). force={force}");
        }
        else if (rewrittenLocal > 0)
        {
            // No background work — persist relative paths now and refresh inspectors
            _globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
            RequestSilentResaveIfNeeded();
            GD.Print("MediaBackupManager:EnqueueShowMediaBackup - Nothing new to queue; relative paths updated.");
            if (force)
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    "Media copy: paths updated to show-relative (files already local).", 0);
        }
        else
        {
            GD.Print("MediaBackupManager:EnqueueShowMediaBackup - Nothing new to queue.");
            if (force)
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                    "Media copy: nothing to do (no external media found).", 0);
        }
    }

    /// <summary>
    /// Copies show-local media from a previous session folder into the current one.
    /// Used on Save As so relative URLs (e.g. <c>Audio/song.wav</c>) keep working in the new
    /// location. Independent of <see cref="Settings.MediaBackupEnabled"/> — this is relocating
    /// files that already belong to the show, not backing up external sources.
    /// </summary>
    /// <param name="previousSessionDir">Session directory before the save path changed.</param>
    /// <returns>Number of copy jobs newly queued.</returns>
    public int EnqueueCopyFromPreviousSession(string previousSessionDir)
    {
        if (string.IsNullOrWhiteSpace(previousSessionDir) || string.IsNullOrEmpty(_globalData?.SessionDir))
            return 0;

        string srcRoot;
        string destRoot;
        try
        {
            srcRoot = Path.GetFullPath(previousSessionDir);
            destRoot = Path.GetFullPath(_globalData.SessionDir);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"MediaBackupManager:EnqueueCopyFromPreviousSession - Path error: {ex.Message}");
            return 0;
        }

        if (string.Equals(srcRoot, destRoot, StringComparison.OrdinalIgnoreCase))
            return 0;

        if (!Directory.Exists(srcRoot))
        {
            GD.Print($"MediaBackupManager:EnqueueCopyFromPreviousSession - Previous session dir missing: {srcRoot}");
            return 0;
        }

        EnsureSessionTypeFolders();

        int enqueued = 0;
        enqueued += EnqueueDirectoryMirror(
            Path.Combine(srcRoot, DirectoryUtils.AudioFolderName),
            _globalData.SessionAudioPath,
            MediaBackupKind.Audio);
        enqueued += EnqueueDirectoryMirror(
            Path.Combine(srcRoot, DirectoryUtils.VideoFolderName),
            _globalData.SessionVideoPath,
            MediaBackupKind.Video);
        enqueued += EnqueueDirectoryMirror(
            Path.Combine(srcRoot, DirectoryUtils.ImagesFolderName),
            _globalData.SessionImagesPath,
            MediaBackupKind.Image);
        enqueued += EnqueueDirectoryMirror(
            Path.Combine(srcRoot, DirectoryUtils.WaveformsFolderName),
            _globalData.SessionWaveformsPath,
            MediaBackupKind.Audio);

        // Relative URLs that live outside the standard type folders still need to move.
        enqueued += EnqueueReferencedRelativeCopies(srcRoot, destRoot);

        if (enqueued > 0)
        {
            _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
                $"Save As: copying {enqueued} show-local media file(s) into the new folder.",
                0);
            GD.Print($"MediaBackupManager:EnqueueCopyFromPreviousSession - Queued {enqueued} file(s) {srcRoot} → {destRoot}");
        }
        else
        {
            GD.Print("MediaBackupManager:EnqueueCopyFromPreviousSession - Nothing to copy.");
        }

        return enqueued;
    }

    /// <summary>
    /// Queues a recursive copy of every file under <paramref name="srcDir"/> into
    /// <paramref name="destDir"/>, preserving relative structure. Skips files that already
    /// live inside the destination session tree (nested Save As).
    /// </summary>
    private int EnqueueDirectoryMirror(string srcDir, string destDir, MediaBackupKind kind)
    {
        if (string.IsNullOrEmpty(srcDir) || string.IsNullOrEmpty(destDir) || !Directory.Exists(srcDir))
            return 0;

        string srcFull;
        string destFull;
        try
        {
            srcFull = Path.GetFullPath(srcDir);
            destFull = Path.GetFullPath(destDir);
        }
        catch
        {
            return 0;
        }

        if (string.Equals(srcFull, destFull, StringComparison.OrdinalIgnoreCase))
            return 0;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(srcFull, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"MediaBackupManager:EnqueueDirectoryMirror - List '{srcFull}': {ex.Message}");
            return 0;
        }

        int count = 0;
        foreach (string file in files)
        {
            if (MediaPaths.IsUnderDirectory(file, _globalData.SessionDir))
                continue;

            string relInsideFolder;
            try
            {
                relInsideFolder = Path.GetRelativePath(srcFull, file);
            }
            catch
            {
                relInsideFolder = Path.GetFileName(file);
            }

            if (string.IsNullOrEmpty(relInsideFolder) || relInsideFolder.StartsWith("..", StringComparison.Ordinal))
                continue;

            string destPath = Path.Combine(destFull, relInsideFolder);
            if (EnqueueCopyJob(file, destPath, kind))
                count++;
        }

        return count;
    }

    /// <summary>
    /// Copies cue media stored as show-relative paths from the previous session root,
    /// covering files that are not inside Audio/Video/Images.
    /// </summary>
    private int EnqueueReferencedRelativeCopies(string srcRoot, string destRoot)
    {
        if (CueList.CueIndex == null || CueList.CueIndex.Count == 0)
            return 0;

        int count = 0;
        foreach (var cue in CueList.CueIndex.Values)
        {
            if (cue == null)
                continue;

            var audio = cue.GetAudioComponent();
            if (audio != null)
                TryEnqueueRelativeFile(audio.AudioFile, MediaBackupKind.Audio, srcRoot, destRoot, ref count);

            var video = cue.GetVideoComponent();
            if (video != null)
            {
                var kind = DetectKindFromPath(video.VideoFile);
                if (kind != MediaBackupKind.Image)
                    kind = MediaBackupKind.Video;
                TryEnqueueRelativeFile(video.VideoFile, kind, srcRoot, destRoot, ref count);
            }
        }

        return count;
    }

    private void TryEnqueueRelativeFile(
        string storedPath,
        MediaBackupKind kind,
        string srcRoot,
        string destRoot,
        ref int count)
    {
        if (string.IsNullOrWhiteSpace(storedPath) || !MediaPaths.IsShowRelative(storedPath))
            return;

        string src = MediaPaths.Resolve(storedPath, srcRoot);
        string dest = MediaPaths.Resolve(storedPath, destRoot);
        if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dest) || !File.Exists(src))
            return;
        if (MediaPaths.IsUnderDirectory(src, destRoot))
            return;
        if (string.Equals(MediaPaths.NormalizeKey(src), MediaPaths.NormalizeKey(dest), StringComparison.OrdinalIgnoreCase))
            return;

        if (EnqueueCopyJob(src, dest, kind))
            count++;
    }

    /// <summary>
    /// Ensures Audio/Video/Images/Waveforms folders exist under the current session directory.
    /// </summary>
    private void EnsureSessionTypeFolders()
    {
        if (string.IsNullOrEmpty(_globalData?.SessionDir))
            return;

        try
        {
            string sessionDir = _globalData.SessionDir;
            if (string.IsNullOrEmpty(_globalData.SessionAudioPath))
                _globalData.SessionAudioPath = sessionDir + "/" + UI.Utilities.DirectoryUtils.AudioFolderName;
            if (string.IsNullOrEmpty(_globalData.SessionVideoPath))
                _globalData.SessionVideoPath = sessionDir + "/" + UI.Utilities.DirectoryUtils.VideoFolderName;
            if (string.IsNullOrEmpty(_globalData.SessionImagesPath))
                _globalData.SessionImagesPath = sessionDir + "/" + UI.Utilities.DirectoryUtils.ImagesFolderName;
            if (string.IsNullOrEmpty(_globalData.SessionWaveformsPath))
                _globalData.SessionWaveformsPath = sessionDir + "/" + UI.Utilities.DirectoryUtils.WaveformsFolderName;

            foreach (string dir in new[]
                     {
                         _globalData.SessionAudioPath,
                         _globalData.SessionVideoPath,
                         _globalData.SessionImagesPath,
                         _globalData.SessionWaveformsPath
                     })
            {
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"MediaBackupManager:EnsureSessionTypeFolders - {ex.Message}");
        }
    }

    /// <summary>
    /// Ensures media is show-local (copy if needed) and rewrites the cue path to relative.
    /// </summary>
    private bool TryRewriteOrEnqueue(string storedPath, MediaBackupKind kind, out bool wasQueued, bool force = false)
    {
        wasQueued = false;

        string resolvedSource = MediaPaths.Resolve(storedPath, _globalData.SessionDir);
        if (string.IsNullOrEmpty(resolvedSource))
            return false;

        bool wasExternal = !IsPathUnderDirectory(resolvedSource, _globalData.SessionDir);

        string relative = EnsureMediaBackedUp(storedPath, kind, force);
        if (string.IsNullOrEmpty(relative))
            return false;

        if (ApplyRelativePathToCues(resolvedSource, storedPath, relative))
            _pathsChangedThisBatch = true;

        // External media that is not yet at dest (or still copying) counts as queued work
        if (wasExternal)
        {
            string destAbs = MediaPaths.Resolve(relative, _globalData.SessionDir);
            wasQueued = IsBusy || !File.Exists(destAbs);
        }

        return true;
    }

    /// <summary>
    /// Rewrites any media path under the session directory to a show-relative path (no copies).
    /// </summary>
    /// <returns>Number of cue components updated.</returns>
    public int RewriteLocalMediaPathsToRelative()
    {
        if (string.IsNullOrEmpty(_globalData?.SessionDir) || CueList.CueIndex == null)
            return 0;

        int count = 0;
        foreach (var cue in CueList.CueIndex.Values)
        {
            if (cue == null) continue;

            var audio = cue.GetAudioComponent();
            if (audio != null && !string.IsNullOrEmpty(audio.AudioFile))
            {
                string resolved = MediaPaths.Resolve(audio.AudioFile, _globalData.SessionDir);
                string relative = MediaPaths.TryMakeRelative(resolved, _globalData.SessionDir);
                if (!string.IsNullOrEmpty(relative) &&
                    !string.Equals(audio.AudioFile, relative, StringComparison.Ordinal))
                {
                    audio.AudioFile = relative;
                    count++;
                    _pathsChangedThisBatch = true;
                }
            }

            var video = cue.GetVideoComponent();
            if (video != null && !string.IsNullOrEmpty(video.VideoFile))
            {
                string resolved = MediaPaths.Resolve(video.VideoFile, _globalData.SessionDir);
                string relative = MediaPaths.TryMakeRelative(resolved, _globalData.SessionDir);
                if (!string.IsNullOrEmpty(relative) &&
                    !string.Equals(video.VideoFile, relative, StringComparison.Ordinal))
                {
                    video.VideoFile = relative;
                    count++;
                    _pathsChangedThisBatch = true;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// When media backup is enabled and a show is open, returns the show-relative path to store on the cue
    /// immediately (e.g. <c>Audio/song.wav</c>) and enqueues a background copy if the source is external.
    /// </summary>
    /// <param name="sourcePath">Absolute or relative source path of the media file.</param>
    /// <param name="kind">Optional media kind; detected from extension when null.</param>
    /// <param name="force">When true, ignore MediaBackupEnabled and always attempt show-local copy.</param>
    /// <returns>
    /// Show-relative path when backup applies; <c>null</c> when backup is disabled, no session is open,
    /// or the path cannot be handled (caller should keep the original path).
    /// </returns>
    public string EnsureMediaBackedUp(string sourcePath, MediaBackupKind? kind = null, bool force = false)
    {
        if (!force && (_globalData?.Settings == null || !_globalData.Settings.MediaBackupEnabled))
            return null;

        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrEmpty(_globalData.SessionDir))
            return null;

        string resolvedSource = MediaPaths.Resolve(sourcePath, _globalData.SessionDir);
        if (string.IsNullOrEmpty(resolvedSource) || !File.Exists(resolvedSource))
            return null;

        MediaBackupKind mediaKind = kind ?? DetectKindFromPath(resolvedSource);
        string destDir = GetDestinationDir(mediaKind);
        if (string.IsNullOrEmpty(destDir))
            return null;

        try
        {
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"MediaBackupManager:EnsureMediaBackedUp - Failed to create '{destDir}': {ex.Message}");
            return null;
        }

        // Already under show folder — relative only
        if (IsPathUnderDirectory(resolvedSource, _globalData.SessionDir))
        {
            string localRel = MediaPaths.TryMakeRelative(resolvedSource, _globalData.SessionDir);
            if (!string.IsNullOrEmpty(localRel))
                return localRel;
            return null;
        }

        string fileName = Path.GetFileName(resolvedSource);
        string destPath = Path.Combine(destDir, fileName);

        if (File.Exists(destPath))
        {
            try
            {
                long srcLen = new FileInfo(resolvedSource).Length;
                long dstLen = new FileInfo(destPath).Length;
                if (srcLen != dstLen)
                    destPath = AllocateUniquePath(destDir, fileName);
            }
            catch
            {
                destPath = AllocateUniquePath(destDir, fileName);
            }
        }

        string relative = MediaPaths.TryMakeRelative(destPath, _globalData.SessionDir);
        if (string.IsNullOrEmpty(relative))
            return null;

        // Dest already has identical content — no copy needed
        if (File.Exists(destPath))
        {
            try
            {
                if (new FileInfo(resolvedSource).Length == new FileInfo(destPath).Length)
                    return relative;
            }
            catch { /* fall through to copy */ }
        }

        // Queue background copy; caller stores relative path immediately
        EnqueueCopyJob(resolvedSource, destPath, mediaKind);
        GD.Print($"MediaBackupManager:EnsureMediaBackedUp - Queued copy → {relative}");
        return relative;
    }

    /// <summary>
    /// Enqueues a single file for backup into the appropriate type folder.
    /// </summary>
    /// <param name="sourcePath">Absolute or show-relative path to the media file.</param>
    /// <param name="kind">Destination folder kind.</param>
    /// <returns>True if the job was newly queued; false if skipped/duplicate.</returns>
    public bool EnqueueFile(string sourcePath, MediaBackupKind kind)
    {
        string relative = EnsureMediaBackedUp(sourcePath, kind);
        // EnsureMediaBackedUp returns relative whether queued or already present;
        // treat as "queued or handled" when non-null and source was external.
        return !string.IsNullOrEmpty(relative);
    }

    private bool EnqueueCopyJob(string resolvedSource, string destPath, MediaBackupKind kind)
    {
        string sourceKey = MediaPaths.NormalizeKey(resolvedSource);
        string destDir = Path.GetDirectoryName(destPath) ?? GetDestinationDir(kind);

        lock (_queueLock)
        {
            if (!_queuedSourceKeys.Add(sourceKey))
                return false;

            _queue.Enqueue(new BackupJob
            {
                SourcePath = resolvedSource,
                DestPath = destPath,
                Kind = kind,
                DestDir = destDir,
                SourceKey = sourceKey,
                RelativePath = MediaPaths.TryMakeRelative(destPath, _globalData.SessionDir) ?? Path.GetFileName(destPath)
            });

            if (!_workerRunning)
            {
                _totalJobs = _queue.Count;
                _completedJobs = 0;
                StartWorkerUnlocked();
            }
            else
            {
                _totalJobs = Math.Max(_totalJobs, _completedJobs + _queue.Count);
            }
        }

        EmitProgressDeferred();
        return true;
    }

    /// <summary>
    /// Starts a background worker for the current <see cref="_sessionEpoch"/>. Caller must hold <see cref="_queueLock"/>.
    /// </summary>
    private void StartWorkerUnlocked()
    {
        _workerRunning = true;
        try
        {
            _cts?.Dispose();
        }
        catch
        {
            // ignore
        }

        _cts = new CancellationTokenSource();
        int epoch = _sessionEpoch;
        var token = _cts.Token;
        Task.Run(() => WorkerLoop(token, epoch), token);
    }

    /// <summary>
    /// Detects media kind from file extension using GlobalData filter lists.
    /// </summary>
    public static MediaBackupKind DetectKindFromPath(string path)
    {
        string ext = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext))
            return MediaBackupKind.Audio;

        if (GlobalData.AudioFileFilters.Any(e => e.TrimStart('*').Equals(ext, StringComparison.OrdinalIgnoreCase)))
            return MediaBackupKind.Audio;
        if (GlobalData.ImageFileFilters.Any(e => e.TrimStart('*').Equals(ext, StringComparison.OrdinalIgnoreCase)))
            return MediaBackupKind.Image;
        if (GlobalData.VideoFileFilters.Any(e => e.TrimStart('*').Equals(ext, StringComparison.OrdinalIgnoreCase)))
            return MediaBackupKind.Video;

        return MediaBackupKind.Audio;
    }

    private void WorkerLoop(CancellationToken token, int epoch)
    {
        bool completedCleanly = false;
        try
        {
            while (true)
            {
                if (token.IsCancellationRequested || !IsWorkerEpochCurrent(epoch))
                    break;

                BackupJob job;
                lock (_queueLock)
                {
                    if (!IsWorkerEpochCurrent(epoch) || token.IsCancellationRequested)
                        break;

                    if (_queue.Count == 0)
                    {
                        _currentOriginPath = string.Empty;
                        _currentDestPath = string.Empty;
                        _queuedSourceKeys.Clear();
                        completedCleanly = true;
                        break;
                    }

                    job = _queue.Dequeue();
                    _currentOriginPath = job.SourcePath;
                    _currentDestPath = job.DestPath;
                }

                EmitProgressDeferred();

                MediaBackupResult result = ProcessJob(job);

                // Drop results after New Session / open — do not rewrite the new show's paths.
                if (token.IsCancellationRequested || !IsWorkerEpochCurrent(epoch))
                    break;

                Interlocked.Increment(ref _completedJobs);

                if (result.Success)
                {
                    if (result.Copied)
                    {
                        CallDeferred(nameof(DeferredLog),
                            $"Media backup: copied {Path.GetFileName(result.SourcePath)} → {result.RelativePath}",
                            0);
                    }
                    else
                    {
                        GD.Print($"MediaBackupManager:WorkerLoop - Skipped copy (already present): {result.RelativePath}");
                    }

                    // Epoch argument so deferred apply is a no-op after session wipe.
                    CallDeferred(nameof(DeferredApplyRelativePath),
                        result.SourcePath, result.RelativePath, epoch);
                }
                else
                {
                    CallDeferred(nameof(DeferredLog),
                        $"Media backup failed: {Path.GetFileName(result.SourcePath)} — {result.Error}",
                        2);
                }

                EmitProgressDeferred();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"MediaBackupManager:WorkerLoop - Unexpected error: {ex.Message}");
            CallDeferred(nameof(DeferredLog), $"Media backup worker error: {ex.Message}", 2);
        }
        finally
        {
            bool emitCompleted = false;
            lock (_queueLock)
            {
                _workerRunning = false;
                _currentOriginPath = string.Empty;
                _currentDestPath = string.Empty;

                // Jobs enqueued for a newer session while this worker was finishing a copy:
                // start a fresh worker for the current epoch (keys already tracked by Enqueue).
                if (_queue.Count > 0)
                {
                    StartWorkerUnlocked();
                }
                else
                {
                    _queuedSourceKeys.Clear();
                    // Only finish the batch UI / silent re-save for the worker's own epoch.
                    emitCompleted = completedCleanly && epoch == _sessionEpoch;
                }
            }

            EmitProgressDeferred();
            if (emitCompleted)
                CallDeferred(nameof(DeferredEmitCompleted), epoch);
        }
    }

    /// <summary>True when <paramref name="epoch"/> still matches the live session generation.</summary>
    private bool IsWorkerEpochCurrent(int epoch)
    {
        lock (_queueLock)
            return epoch == _sessionEpoch;
    }

    private MediaBackupResult ProcessJob(BackupJob job)
    {
        try
        {
            if (!File.Exists(job.SourcePath))
            {
                return new MediaBackupResult
                {
                    SourcePath = job.SourcePath,
                    Success = false,
                    Error = "Source file not found"
                };
            }

            string destPath = !string.IsNullOrEmpty(job.DestPath)
                ? job.DestPath
                : Path.Combine(job.DestDir, Path.GetFileName(job.SourcePath));

            string destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            string relative = !string.IsNullOrEmpty(job.RelativePath)
                ? job.RelativePath
                : MediaPaths.TryMakeRelative(destPath, _globalData.SessionDir) ?? Path.GetFileName(destPath);

            // Same path after normalization (already local edge case)
            if (string.Equals(MediaPaths.NormalizeKey(job.SourcePath), MediaPaths.NormalizeKey(destPath), StringComparison.OrdinalIgnoreCase))
            {
                return new MediaBackupResult
                {
                    SourcePath = job.SourcePath,
                    DestinationPath = destPath,
                    RelativePath = relative,
                    Copied = false,
                    Success = true
                };
            }

            // If destination exists with same size, treat as already backed up
            if (File.Exists(destPath))
            {
                long srcLen = new FileInfo(job.SourcePath).Length;
                long dstLen = new FileInfo(destPath).Length;
                if (srcLen == dstLen)
                {
                    return new MediaBackupResult
                    {
                        SourcePath = job.SourcePath,
                        DestinationPath = destPath,
                        RelativePath = relative,
                        Copied = false,
                        Success = true
                    };
                }
            }

            File.Copy(job.SourcePath, destPath, overwrite: true);

            return new MediaBackupResult
            {
                SourcePath = job.SourcePath,
                DestinationPath = destPath,
                RelativePath = relative,
                Copied = true,
                Success = true
            };
        }
        catch (Exception ex)
        {
            return new MediaBackupResult
            {
                SourcePath = job.SourcePath,
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Updates all cue components that reference <paramref name="sourceAbsolute"/> (or the original
    /// stored path) to use <paramref name="relativePath"/>.
    /// </summary>
    private bool ApplyRelativePathToCues(string sourceAbsolute, string originalStored, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || CueList.CueIndex == null)
            return false;

        string sourceKey = MediaPaths.NormalizeKey(sourceAbsolute);
        bool any = false;

        foreach (var cue in CueList.CueIndex.Values)
        {
            if (cue == null) continue;

            var audio = cue.GetAudioComponent();
            if (audio != null && !string.IsNullOrEmpty(audio.AudioFile))
            {
                if (PathMatches(audio.AudioFile, sourceKey, originalStored, sourceAbsolute))
                {
                    if (!string.Equals(audio.AudioFile, relativePath, StringComparison.Ordinal))
                    {
                        GD.Print($"MediaBackupManager:ApplyRelativePathToCues - Cue {cue.Id} audio: '{audio.AudioFile}' → '{relativePath}'");
                        audio.AudioFile = relativePath;
                        any = true;
                    }
                }
            }

            var video = cue.GetVideoComponent();
            if (video != null && !string.IsNullOrEmpty(video.VideoFile))
            {
                if (PathMatches(video.VideoFile, sourceKey, originalStored, sourceAbsolute))
                {
                    if (!string.Equals(video.VideoFile, relativePath, StringComparison.Ordinal))
                    {
                        GD.Print($"MediaBackupManager:ApplyRelativePathToCues - Cue {cue.Id} video: '{video.VideoFile}' → '{relativePath}'");
                        video.VideoFile = relativePath;
                        any = true;
                    }
                }
            }
        }

        return any;
    }

    private bool PathMatches(string stored, string sourceKey, string originalStored, string sourceAbsolute)
    {
        if (string.Equals(stored, originalStored, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.Equals(stored, sourceAbsolute, StringComparison.OrdinalIgnoreCase))
            return true;

        string resolved = MediaPaths.Resolve(stored, _globalData.SessionDir);
        if (!string.IsNullOrEmpty(resolved) &&
            string.Equals(MediaPaths.NormalizeKey(resolved), sourceKey, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private void DeferredApplyRelativePath(string sourceAbsolute, string relativePath, int epoch)
    {
        if (!IsWorkerEpochCurrent(epoch))
        {
            GD.Print(
                $"MediaBackupManager:DeferredApplyRelativePath - Ignored path rewrite (session changed). " +
                $"rel={relativePath}");
            return;
        }

        if (ApplyRelativePathToCues(sourceAbsolute, sourceAbsolute, relativePath))
        {
            _pathsChangedThisBatch = true;
            // Update inspector URL fields while the same cue remains selected
            _globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));
        }
    }

    private static string AllocateUniquePath(string destDir, string fileName)
    {
        string name = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 1; i < 1000; i++)
        {
            string candidate = Path.Combine(destDir, $"{name}_{i}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(destDir, $"{name}_{Guid.NewGuid():N}{ext}");
    }

    private string GetDestinationDir(MediaBackupKind kind)
    {
        return kind switch
        {
            MediaBackupKind.Audio => _globalData.SessionAudioPath,
            MediaBackupKind.Video => _globalData.SessionVideoPath,
            MediaBackupKind.Image => _globalData.SessionImagesPath,
            _ => _globalData.SessionAudioPath
        };
    }

    private static bool IsPathUnderDirectory(string filePath, string directory) =>
        MediaPaths.IsUnderDirectory(filePath, directory);

    private void EmitProgressDeferred()
    {
        CallDeferred(nameof(DeferredEmitProgress));
    }

    private void DeferredEmitProgress()
    {
        float percent = ProgressPercent;
        bool busy = IsBusy;
        int completed = _completedJobs;
        int total = Math.Max(_totalJobs, completed);
        string statusText = busy || total > 0
            ? $"Copying {percent:F0}%"
            : string.Empty;
        string origin = _currentOriginPath ?? string.Empty;
        string dest = _currentDestPath ?? string.Empty;

        _globalSignals.EmitSignal(nameof(GlobalSignals.MediaBackupProgress),
            percent, busy, statusText, origin, dest, completed, total);
    }

    private void DeferredEmitCompleted(int epoch)
    {
        if (!IsWorkerEpochCurrent(epoch))
        {
            GD.Print("MediaBackupManager:DeferredEmitCompleted - Ignored (session changed during backup).");
            return;
        }

        _globalSignals.EmitSignal(nameof(GlobalSignals.MediaBackupCompleted));
        _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "Media backup: idle.", 0);

        // Keep inspector labels in sync with new relative paths
        _globalSignals.EmitSignal(nameof(GlobalSignals.SyncShellInspector));

        // Relative paths may have been set before copy finished — clear false "missing" flags
        try
        {
            GetNodeOrNull<MediaHealthService>("/root/MediaHealthService")?.RecheckAllQuiet();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"MediaBackupManager:DeferredEmitCompleted - Health recheck: {ex.Message}");
        }

        RequestSilentResaveIfNeeded();
    }

    private void RequestSilentResaveIfNeeded()
    {
        if (!_pathsChangedThisBatch)
            return;

        // Never silent-resave after New Session / failed open left no show path.
        if (string.IsNullOrEmpty(_globalData?.SessionPath))
        {
            _pathsChangedThisBatch = false;
            GD.Print("MediaBackupManager:RequestSilentResaveIfNeeded - Skipped (no SessionPath).");
            return;
        }

        _pathsChangedThisBatch = false;

        try
        {
            _saveManager ??= GetNodeOrNull<SaveManager>("/root/SaveManager");
            if (_saveManager == null)
            {
                GD.PrintErr("MediaBackupManager:RequestSilentResaveIfNeeded - SaveManager not found.");
                return;
            }

            GD.Print("MediaBackupManager:RequestSilentResaveIfNeeded - Re-saving session with relative media paths.");
            _saveManager.ResaveSessionAfterMediaPathUpdate();
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                "Media backup: cue paths updated to show-relative; session re-saved.", 0);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"MediaBackupManager:RequestSilentResaveIfNeeded - {ex.Message}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"Failed to re-save after media path update: {ex.Message}", 2);
        }
    }

    private void DeferredLog(string message, int type)
    {
        _globalSignals.EmitSignal(nameof(GlobalSignals.Log), message, type);
    }

    private sealed class BackupJob
    {
        public string SourcePath { get; init; } = string.Empty;
        public string DestPath { get; init; } = string.Empty;
        public string RelativePath { get; init; } = string.Empty;
        public MediaBackupKind Kind { get; init; }
        public string DestDir { get; init; } = string.Empty;
        public string SourceKey { get; init; } = string.Empty;
    }
}
