// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
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
using Cue2.UI.Inspectors;
using Cue2.UI.Popups;

namespace Cue2.UI.Popups;

/// <summary>
/// Handles file drag-and-drop events for the application window.
/// </summary>
public enum FileDropTargetType
{
    None,
    FileUrlAudio,
    FileUrlVideo,
    CueList,
    ShellBar
}

/// <summary>
/// Describes where to insert cues created from dropped files relative to a target cue.
/// </summary>
public enum DropInsertMode
{
    /// <summary>Append at end of list (top-level) or parent's children.</summary>
    AtEnd,
    /// <summary>Insert immediately above the target cue as sibling.</summary>
    Above,
    /// <summary>Insert immediately below the target cue as sibling.</summary>
    Below,
    /// <summary>Make the new cue(s) children of the target cue.</summary>
    AsChild
}

/// <summary>
/// How multiple dropped media files should be turned into cues.
/// </summary>
public enum MultiFileDropMode
{
    /// <summary>One top-level (or sibling) cue per file with media on that cue.</summary>
    SeparateCues = 0,

    /// <summary>One empty group parent; each file is a child cue with media.</summary>
    WrapInOneGroup = 1,

    /// <summary>
    /// For each file: create an empty parent cue and a single child cue that holds the media component
    /// (2 cues per file).
    /// </summary>
    ParentPerFile = 2
}

/// <summary>
/// User choices returned from the file drop confirmation popup.
/// </summary>
public class FileDropChoices
{
    /// <summary>Desired insert position (used when a specific shell was the drop target).</summary>
    public DropInsertMode InsertMode { get; set; } = DropInsertMode.Below;

    /// <summary>
    /// For multiple files: how to structure the created cues.
    /// Ignored when only one file is dropped.
    /// </summary>
    public MultiFileDropMode MultiFileMode { get; set; } = MultiFileDropMode.SeparateCues;

    /// <summary>
    /// Legacy convenience: true when <see cref="MultiFileMode"/> is <see cref="MultiFileDropMode.WrapInOneGroup"/>.
    /// </summary>
    public bool CreateAsGroup
    {
        get => MultiFileMode == MultiFileDropMode.WrapInOneGroup;
        set
        {
            if (value)
                MultiFileMode = MultiFileDropMode.WrapInOneGroup;
            else if (MultiFileMode == MultiFileDropMode.WrapInOneGroup)
                MultiFileMode = MultiFileDropMode.SeparateCues;
        }
    }
}

// ===========================
// DESIGN: All supported drop scenarios (single & multiple files)
// ===========================
// 1. Single valid media file dropped on visible Audio inspector FileURL LineEdit:
//    - Requires a focused/selected cue.
//    - Calls AudioInspector to set/replace the AudioComponent (existing path).
//
// 2. Single valid media file dropped on visible Video inspector FileURL LineEdit:
//    - Same as above for VideoComponent.
//
// 3. Single valid audio file dropped on CueList background (not over a shell bar):
//    - AUTO create: 1 new top-level cue with AudioComponent at end (or logical insert point).
//    - No popup.
//
// 4. Single valid video file dropped on CueList background:
//    - AUTO create: 1 new top-level cue with VideoComponent.
//
// 5. Single valid file dropped directly over a ShellBar:
//    - SHOW popup with position choices: Above / Below / AsChild of that cue.
//    - Default: Below. Confirm creates 1 cue in chosen position.
//
// 6. Multiple files (any valid mix of audio/video) dropped on CueList background:
//    - SHOW popup.
//    - Options:
//        a) separate cues (default) — N media cues
//        b) wrap all in one Group — 1 empty parent + N media children
//        c) each file under own parent — 2N cues (empty parent + media child per file)
//    - Insert at end / after selection.
//
// 7. Multiple files dropped over a specific ShellBar:
//    - SHOW popup with BOTH position choices + multi-file structure options.
//    - Creates N / 1+N / 2N cues inserted at chosen relation to target.
//
// 8. Mixed valid + invalid files in one drop:
//    - Silently filter to only supported extensions (audio + video + images as video).
//    - If none remain valid, log warning and abort (no popup).
//
// 9. Drop of images (png/jpg etc):
//    - Treated as VideoComponent targets (still image support via video path or future dedicated).
//    - Validated via GlobalData.ImageFileFilters.
//
// 10. Drop with 0 files or only unsupported:
//     - Log + no action + no popup.
//
// Notes:
// - Inspector URL drops stay direct (single file only).
// - List/shell drops create *new* cues (primary use case).
// - Replacing media on existing cue is done via inspector URL drop or file picker.
// - New cues derive their Name from the filename (without extension).
// - Metadata + waveform are fetched asynchronously after creation.
// - New cue(s) are selected after creation.
// ===========================

// Conditions on file drop (historical)
// Onto audio URL -> only one file -> validate valid audio file -> replace URL
// Onto video URL -> Only one file -> validate valid video file -> replace video URL
// Onto cuelist -> filecount / if one file -> new cue ; if multiple -> options
// Onto existing shell bar -> as above + position relative to the bar (above/below/child)

/// <summary>
/// Manages file drop detection and routing to appropriate targets.
/// </summary>
public partial class FileDropper : Control
{
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    private FileDropPopup _activeFileDropPopup;

    public override void _Ready()
    {
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        
        GD.Print("FileDropper ready");

        GetWindow().FilesDropped += OnFilesDropped;
    }

    public override void _ExitTree()
    {
        GetWindow().FilesDropped -= OnFilesDropped;
        CloseActivePopup();
    }

    private void CloseActivePopup()
    {
        if (_activeFileDropPopup != null && IsInstanceValid(_activeFileDropPopup))
        {
            // Disconnect to avoid leaks
            _activeFileDropPopup.Confirmed -= OnPopupConfirmed;
            _activeFileDropPopup.Cancelled -= OnPopupCancelled;
            _activeFileDropPopup.QueueFree();
            _activeFileDropPopup = null;
        }
    }

    private void OnFilesDropped(string[] files)
    {
        GD.Print("FileDropper:OnFilesDropped - Files dropped");

        if (TryOpenDroppedShowfile(files))
            return;

        // Show Mode: media drops create or replace cue media — block all drop editing paths.
        if (_globalData?.Settings?.IsCueEditingLocked == true)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                "Show Mode: file drops are disabled. Turn off Show Mode to add or change media.", (int)LogType.Info);
            return;
        }

        var dropInfo = GetDropTarget(GetGlobalMousePosition());
        GD.Print($"FileDropper:OnFilesDropped - targetType={dropInfo.TargetType}, files={files?.Length ?? 0}, targetCueId={dropInfo.TargetCueId}");

        // --- Inspector URL targets (single file only, direct, no popup) ---
        if (dropInfo.TargetType == FileDropTargetType.FileUrlAudio)
        {
            GD.Print("FileDropper:Audio URL drop detected");
            if (files == null || files.Length != 1)
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "FileDropper: Audio URL drop requires exactly 1 file", (int)LogType.Warning);
            }
            else if (IsSupportedMediaFile(files[0]))
            {
                var cue2Base = GetTree().Root.GetNode("Cue2Base");
                var audioInspector = cue2Base?.GetNode<AudioInspector>("%Audio");
                if (audioInspector != null)
                {
                    audioInspector.SetAudioFileUrlFromDrop(files[0]);
                }
            }
            return;
        }

        if (dropInfo.TargetType == FileDropTargetType.FileUrlVideo)
        {
            GD.Print("FileDropper:Video URL drop detected");
            if (files == null || files.Length != 1)
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "FileDropper: Video URL drop requires exactly 1 file", (int)LogType.Warning);
            }
            else if (IsSupportedMediaFile(files[0]))
            {
                var cue2Base = GetTree().Root.GetNode("Cue2Base");
                // Tab node was renamed Video → Visual; component type remains Video.
                var videoInspector = cue2Base?.GetNode<VideoInspector>("%Visual");
                if (videoInspector != null)
                {
                    videoInspector.SetVideoFileUrlFromDrop(files[0]);
                }
            }
            return;
        }

        // --- List / Shell targets: filter files first ---
        if (files == null || files.Length == 0)
            return;

        var validFiles = FilterValidMediaFiles(files);
        if (validFiles.Count == 0)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "FileDropper: No supported media files in drop (supported: audio, video, images).", (int)LogType.Warning);
            return;
        }

        string targetDisplay = dropInfo.TargetType.ToString();
        int targetCueId = dropInfo.TargetCueId;

        if (dropInfo.TargetType == FileDropTargetType.ShellBar && targetCueId >= 0)
        {
            CueList.CueIndex.TryGetValue(targetCueId, out Cue cue);
            targetDisplay = cue?.Name ?? $"Cue {targetCueId}";
            GD.Print($"FileDropper: Drop on ShellBar '{targetDisplay}' (ID {targetCueId}) with {validFiles.Count} valid file(s)");
        }
        else if (dropInfo.TargetType == FileDropTargetType.CueList)
        {
            targetDisplay = "Cue List";
        }

        // AUTO-CREATE for the simplest case: single file dropped on the list background (not a specific shell)
        if (dropInfo.TargetType == FileDropTargetType.CueList && validFiles.Count == 1)
        {
            GD.Print("FileDropper: Auto-creating single cue from list drop (no popup).");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"FileDropper: Creating cue from dropped file: {Path.GetFileName(validFiles[0])}", (int)LogType.Info);
            CreateCuesFromDrop(validFiles.ToArray(), targetCueId: -1, DropInsertMode.AtEnd, MultiFileDropMode.SeparateCues);
            return;
        }

        // All other list/shell cases (multiple files, or drop on specific shell) → show interactive popup
        GD.Print("FileDropper: Showing FileDropPopup for choices.");
        CloseActivePopup();

        var popupNode = SceneLoader.LoadScene("uid://cwvgtrsfp0vjh", out string loadErr);
        if (popupNode == null || popupNode is not FileDropPopup popup)
        {
            GD.PrintErr($"FileDropper: Failed to load FileDropPopup: {loadErr}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "Failed to open file drop options.", (int)LogType.Error);
            return;
        }

        _activeFileDropPopup = popup;
        popup.ConfigureForDrop(validFiles.ToArray(), dropInfo.TargetType, targetDisplay, targetCueId);

        // Capture state for callback
        _pendingDropFiles = validFiles.ToArray();
        _pendingTargetCueId = targetCueId;

        popup.Confirmed += OnPopupConfirmed;
        popup.Cancelled += OnPopupCancelled;

        popup.TreeExiting += () =>
        {
            if (_activeFileDropPopup == popup) _activeFileDropPopup = null;
        };

        AddChild(popup);
        popup.ShowConfigured();

        _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
            $"FileDropper: Dropped {validFiles.Count} file(s) on '{dropInfo.TargetType}'. Awaiting user choices.", (int)LogType.Info);
    }

    private void OnPopupConfirmed(FileDropChoices choices)
    {
        string[] files = _pendingDropFiles;
        int targetId = _pendingTargetCueId;
        var mode = choices.InsertMode;
        var multiMode = choices.MultiFileMode;

        if (_activeFileDropPopup != null)
        {
            _activeFileDropPopup.Confirmed -= OnPopupConfirmed;
            _activeFileDropPopup.Cancelled -= OnPopupCancelled;
        }
        CloseActivePopup();
        _pendingDropFiles = null;

        if (files == null || files.Length == 0)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "FileDropper: No files to create after popup confirmation.", (int)LogType.Warning);
            return;
        }

        GD.Print($"FileDropper: Popup confirmed. mode={mode}, multiMode={multiMode}, target={targetId}, count={files.Length}");
        CreateCuesFromDrop(files, targetId, mode, multiMode);
    }

    private void OnPopupCancelled()
    {
        _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "FileDropper: Drop cancelled by user.", (int)LogType.Info);
        CloseActivePopup();
        _pendingDropFiles = null;
    }

    /// <summary>
    /// Rich result from target detection.
    /// </summary>
    private readonly struct DropTarget
    {
        public FileDropTargetType TargetType { get; init; }
        public string TargetInfo { get; init; }
        public int TargetCueId { get; init; }
    }

    /// <summary>
    /// Opens a dropped <c>.c2</c> when the drop is on window chrome, or on the cuelist with no media files.
    /// </summary>
    private bool TryOpenDroppedShowfile(string[] files)
    {
        if (files == null || files.Length == 0)
            return false;

        string show = null;
        foreach (string file in files)
        {
            string resolved = ShowfileLaunchArgs.TryResolveShowfile(file);
            if (resolved != null)
            {
                show = resolved;
                break;
            }
        }

        if (show == null)
            return false;

        var dropInfo = GetDropTarget(GetGlobalMousePosition());
        bool mediaAlso = FilterValidMediaFiles(files).Count > 0;
        bool openShow = dropInfo.TargetType == FileDropTargetType.None
                        || (dropInfo.TargetType == FileDropTargetType.CueList && !mediaAlso);
        if (!openShow)
            return false;

        _globalSignals?.EmitSignal(nameof(GlobalSignals.OpenSelectedSession), show);
        return true;
    }

    private DropTarget GetDropTarget(Vector2 mousePos)
    {
        var root = GetTree().Root;
        var cue2Base = root.GetNode("Cue2Base");
        if (cue2Base == null)
            return new DropTarget { TargetType = FileDropTargetType.None };

        // Inspector URL targets take precedence when visible
        var audioInspector = cue2Base.GetNodeOrNull<Control>("%Audio");
        if (audioInspector != null && audioInspector.Visible)
        {
            var audioFileUrl = audioInspector.GetNodeOrNull<LineEdit>("%FileURL");
            if (audioFileUrl != null && audioFileUrl.Visible && audioFileUrl.GetGlobalRect().HasPoint(mousePos))
            {
                GD.Print("FileDropper:GetDropTarget - Audio URL drop detected");
                return new DropTarget { TargetType = FileDropTargetType.FileUrlAudio, TargetInfo = "AudioFileURL" };
            }
        }

        // Inspector tab node is "Visual" (unique name %Visual); component type remains Video.
        var videoInspector = cue2Base.GetNodeOrNull<Control>("%Visual");
        if (videoInspector != null && videoInspector.Visible)
        {
            var videoFileUrl = videoInspector.GetNodeOrNull<LineEdit>("%FileUrl");
            if (videoFileUrl != null && videoFileUrl.Visible && videoFileUrl.GetGlobalRect().HasPoint(mousePos))
            {
                GD.Print("FileDropper:GetDropTarget - Video/Visual URL drop detected");
                return new DropTarget { TargetType = FileDropTargetType.FileUrlVideo, TargetInfo = "VideoFileURL" };
            }
        }

        var cueListUi = cue2Base.GetNodeOrNull("%CueListUi") as Control;
        var cueContainer = cueListUi?.GetNodeOrNull<VBoxContainer>("%CueContainer");

        if (cueContainer != null)
        {
            var shellBar = FindShellBarInContainer(cueContainer, mousePos);
            if (shellBar != null && IsMouseInVisibleShellBarArea(shellBar, mousePos))
            {
                return new DropTarget
                {
                    TargetType = FileDropTargetType.ShellBar,
                    TargetInfo = shellBar.CueId.ToString(),
                    TargetCueId = shellBar.CueId
                };
            }
        }

        if (cueListUi != null && cueListUi.GetGlobalRect().HasPoint(mousePos))
        {
            return new DropTarget { TargetType = FileDropTargetType.CueList, TargetInfo = "CueList", TargetCueId = -1 };
        }

        return new DropTarget { TargetType = FileDropTargetType.None };
    }



    private ShellBar FindShellBarInContainer(VBoxContainer container, Vector2 screenPosition)
    {
        for (int i = 0; i < container.GetChildCount(); i++)
        {
            if (container.GetChild(i) is ShellBar shellBar && shellBar.Visible)
            {
                var childContainer = shellBar.GetNodeOrNull<VBoxContainer>("%ShellChildContainer");
                if (childContainer != null && childContainer.Visible)
                {
                    ShellBar nestedResult = FindShellBarInContainer(childContainer, screenPosition);
                    if (nestedResult != null)
                        return nestedResult;
                }

                if (shellBar.GetGlobalRect().HasPoint(screenPosition))
                {
                    return shellBar;
                }
            }
        }

        return null;
    }

    private bool IsMouseInVisibleShellBarArea(ShellBar shellBar, Vector2 mousePos)
    {
        var globalRect = shellBar.GetGlobalRect();
        
        if (!shellBar.ShellChildContainer.Visible)
        {
            float visibleHeight = shellBar.Size.Y;
            if (mousePos.Y > globalRect.Position.Y + visibleHeight)
                return false;
        }
        
        return true;
    }

    // --- Pending drop state for popup callback ---
    private string[] _pendingDropFiles;
    private int _pendingTargetCueId;
    private DropInsertMode _pendingInsertMode = DropInsertMode.AtEnd;

    /// <summary>
    /// Creates cues from dropped files using the supplied parameters.
    /// Delegates to CueList.
    /// </summary>
    private void CreateCuesFromDrop(string[] files, int targetCueId, DropInsertMode insertMode, MultiFileDropMode multiFileMode)
    {
        if (files == null || files.Length == 0) return;

        if (_globalData?.Cuelist == null)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "FileDropper: No active CueList to create cues into.", (int)LogType.Error);
            return;
        }

        _globalData.Cuelist.CreateCuesFromDroppedFiles(files, targetCueId, insertMode, multiFileMode);
    }

    // --- Helpers ---

    private List<string> FilterValidMediaFiles(string[] files)
    {
        var result = new List<string>();
        foreach (string f in files)
        {
            if (IsSupportedMediaFile(f))
                result.Add(f);
        }
        return result;
    }

    private bool IsSupportedMediaFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext)) return false;

        // Audio
        if (GlobalData.AudioFileFilters.Any(e => e.TrimStart('*').Equals(ext, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Video
        if (GlobalData.VideoFileFilters.Any(e => e.TrimStart('*').Equals(ext, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Images (treated as video cues for now)
        if (GlobalData.ImageFileFilters.Any(e => e.TrimStart('*').Equals(ext, StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    /// <summary>
    /// Returns a description of the media type for a file.
    /// </summary>
    private string GetMediaTypeForFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (GlobalData.AudioFileFilters.Any(e => e.TrimStart('*').Equals(ext, StringComparison.OrdinalIgnoreCase))) return "Audio";
        if (GlobalData.VideoFileFilters.Any(e => e.TrimStart('*').Equals(ext, StringComparison.OrdinalIgnoreCase))) return "Video";
        if (GlobalData.ImageFileFilters.Any(e => e.TrimStart('*').Equals(ext, StringComparison.OrdinalIgnoreCase))) return "Image";
        return "Media";
    }
}
