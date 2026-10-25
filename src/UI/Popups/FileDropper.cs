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
using Cue2.UI.Shell;

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
    ShellBar,
    ButtonBoard
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
    ParentPerFile = 2,

    /// <summary>
    /// Recreate dropped folders as group cues named after each folder; media files become child cues.
    /// Nested folders become nested groups. Ignored when the drop has no folders.
    /// </summary>
    PreserveFolderTree = 3
}

/// <summary>
/// How a drop onto a button-board cell is applied.
/// </summary>
public enum BoardDropAction
{
    /// <summary>Create cues in empty cells, starting at the drop cell, in row-major order.</summary>
    FillNext = 0,

    /// <summary>Replace media on the occupied cell that received the drop (single file).</summary>
    Replace = 1
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

    /// <summary>Button-board action when the drop landed on an occupied cell.</summary>
    public BoardDropAction BoardAction { get; set; } = BoardDropAction.Replace;

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
// 8b. Dropped folders:
//    - Recurse into the folder (and nested folders); skip hidden files and folders.
//    - Keep only supported media; empty folders after filtering are omitted.
//    - Popup adds "Preserve folder tree as groups" (folder names become group cue names).
//    - Large imports (≥ 50 files) show a warning on the popup.
//
// 9. Drop of images (png/jpg etc):
//    - Treated as VideoComponent targets (still image support via video path or future dedicated).
//    - Validated via GlobalData.ImageFileFilters.
//
// 10. Drop with 0 files or only unsupported:
//     - Log + no action + no popup.
//
// 11. Button Board — single valid file on an empty cell:
//     - AUTO create a media cue in that cell (no popup). Grows empty neighbours.
//
// 12. Button Board — single valid file on an occupied cell:
//     - SHOW popup: Replace this cue's media (default) or place in the next empty cell.
//
// 13. Button Board — multiple valid files:
//     - AUTO fill empty cells in row-major order from the drop cell (skip occupied).
//     - Grow the grid when empty cells run out. No popup.
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

        ClearPendingBoard();
    }

    private void OnFilesDropped(string[] files)
    {
        GD.Print("FileDropper:OnFilesDropped - Files dropped");

        if (files == null || files.Length == 0)
            return;

        var expansion = DroppedMediaExpansion.Expand(files, AllMediaGlobs());

        if (TryOpenDroppedShowfile(files, expansion))
            return;

        // Show Mode: media drops create or replace cue media — block all drop editing paths.
        if (_globalData?.Settings?.IsCueEditingLocked == true)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                "Show Mode: file drops are disabled. Turn off Show Mode to add or change media.", (int)LogType.Info);
            return;
        }

        var dropInfo = GetDropTarget(GetGlobalMousePosition());
        GD.Print($"FileDropper:OnFilesDropped - targetType={dropInfo.TargetType}, files={files.Length}, expanded={expansion.FileCount}, targetCueId={dropInfo.TargetCueId}");

        // --- Inspector URL targets (single file only, direct, no popup) ---
        if (dropInfo.TargetType == FileDropTargetType.FileUrlAudio)
        {
            GD.Print("FileDropper:Audio URL drop detected");
            if (files.Length != 1)
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "FileDropper: Audio URL drop requires exactly 1 file", (int)LogType.Warning);
            }
            else if (Directory.Exists(files[0]))
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "FileDropper: Audio URL drop needs a file, not a folder", (int)LogType.Warning);
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
            if (files.Length != 1)
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "FileDropper: Video URL drop requires exactly 1 file", (int)LogType.Warning);
            }
            else if (Directory.Exists(files[0]))
            {
                _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "FileDropper: Video URL drop needs a file, not a folder", (int)LogType.Warning);
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

        if (expansion.FileCount == 0)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "FileDropper: No supported media files in drop (supported: audio, video, images).", (int)LogType.Warning);
            return;
        }

        string[] validFiles = ToFileArray(expansion);
        string targetDisplay = dropInfo.TargetType.ToString();
        int targetCueId = dropInfo.TargetCueId;

        if (dropInfo.TargetType == FileDropTargetType.ShellBar && targetCueId >= 0)
        {
            CueList.CueIndex.TryGetValue(targetCueId, out Cue cue);
            targetDisplay = cue?.Name ?? $"Cue {targetCueId}";
            GD.Print($"FileDropper: Drop on ShellBar '{targetDisplay}' (ID {targetCueId}) with {expansion.FileCount} valid file(s)");
        }
        else if (dropInfo.TargetType == FileDropTargetType.CueList)
        {
            targetDisplay = "Cue List";
        }
        else if (dropInfo.TargetType == FileDropTargetType.ButtonBoard)
        {
            targetDisplay = ButtonBoard.Live?.ListName ?? "Button Board";
            HandleBoardDrop(validFiles, dropInfo, targetDisplay);
            return;
        }

        // AUTO-CREATE for the simplest case: single file dropped on the list background (not a specific shell)
        if (dropInfo.TargetType == FileDropTargetType.CueList && expansion.FileCount == 1)
        {
            GD.Print("FileDropper: Auto-creating single cue from list drop (no popup).");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"FileDropper: Creating cue from dropped file: {Path.GetFileName(validFiles[0])}", (int)LogType.Info);
            CreateCuesFromDrop(expansion, targetCueId: -1, DropInsertMode.AtEnd, MultiFileDropMode.SeparateCues);
            return;
        }

        // All other list/shell cases (multiple files, or drop on specific shell) → show interactive popup
        ShowDropPopup(expansion, dropInfo.TargetType, targetDisplay, targetCueId);
    }

    private void HandleBoardDrop(string[] files, DropTarget dropInfo, string targetDisplay)
    {
        var board = ButtonBoard.Live;
        if (board == null || !GodotObject.IsInstanceValid(board))
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                "FileDropper: No active Button Board for drop.", (int)LogType.Error);
            return;
        }

        int row = dropInfo.BoardRow;
        int col = dropInfo.BoardCol;
        bool occupied = dropInfo.TargetCueId >= 0;

        if (files.Length == 1 && !occupied)
        {
            GD.Print($"FileDropper: Auto-creating board cue at ({row},{col}) from drop.");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"FileDropper: Creating cue from dropped file: {Path.GetFileName(files[0])}", (int)LogType.Info);
            board.ImportDroppedFiles(files, row, col, BoardDropAction.FillNext);
            return;
        }

        if (files.Length > 1)
        {
            GD.Print($"FileDropper: Filling board from ({row},{col}) with {files.Length} file(s).");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"FileDropper: Filling button board with {files.Length} dropped file(s).", (int)LogType.Info);
            board.ImportDroppedFiles(files, row, col, BoardDropAction.FillNext);
            return;
        }

        ShowDropPopup(DroppedMediaResultForFiles(files), dropInfo.TargetType, targetDisplay, dropInfo.TargetCueId, board, row, col);
    }

    private void ShowDropPopup(
        DroppedMediaResult expansion,
        FileDropTargetType targetType,
        string targetDisplay,
        int targetCueId,
        ButtonBoard board = null,
        int boardRow = -1,
        int boardCol = -1)
    {
        GD.Print("FileDropper: Showing FileDropPopup for choices.");
        CloseActivePopup();

        var popupNode = SceneLoader.LoadScene("uid://cwvgtrsfp0vjh", out string loadErr);
        if (popupNode == null || popupNode is not FileDropPopup popup)
        {
            GD.PrintErr($"FileDropper: Failed to load FileDropPopup: {loadErr}");
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "Failed to open file drop options.", (int)LogType.Error);
            return;
        }

        string[] files = ToFileArray(expansion);
        _activeFileDropPopup = popup;
        popup.ConfigureForDrop(files, targetType, targetDisplay, targetCueId, expansion);

        _pendingDrop = expansion ?? DroppedMediaResult.Empty;
        _pendingTargetCueId = targetCueId;
        _pendingBoard = board;
        _pendingBoardRow = boardRow;
        _pendingBoardCol = boardCol;

        popup.Confirmed += OnPopupConfirmed;
        popup.Cancelled += OnPopupCancelled;

        popup.TreeExiting += () =>
        {
            if (_activeFileDropPopup == popup) _activeFileDropPopup = null;
        };

        AddChild(popup);
        popup.ShowConfigured();

        _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
            $"FileDropper: Dropped {files.Length} file(s) on '{targetType}'. Awaiting user choices.", (int)LogType.Info);
    }

    private void ClearPendingBoard()
    {
        _pendingBoard = null;
        _pendingBoardRow = -1;
        _pendingBoardCol = -1;
    }

    private void OnPopupConfirmed(FileDropChoices choices)
    {
        var expansion = _pendingDrop ?? DroppedMediaResult.Empty;
        string[] files = ToFileArray(expansion);
        int targetId = _pendingTargetCueId;
        var mode = choices.InsertMode;
        var multiMode = choices.MultiFileMode;
        var board = _pendingBoard;
        int boardRow = _pendingBoardRow;
        int boardCol = _pendingBoardCol;
        var boardAction = choices.BoardAction;

        if (_activeFileDropPopup != null)
        {
            _activeFileDropPopup.Confirmed -= OnPopupConfirmed;
            _activeFileDropPopup.Cancelled -= OnPopupCancelled;
        }
        CloseActivePopup();
        _pendingDrop = null;

        if (files.Length == 0)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "FileDropper: No files to create after popup confirmation.", (int)LogType.Warning);
            return;
        }

        GD.Print($"FileDropper: Popup confirmed. mode={mode}, multiMode={multiMode}, target={targetId}, count={files.Length}");

        if (board != null && GodotObject.IsInstanceValid(board))
        {
            board.ImportDroppedFiles(files, boardRow, boardCol, boardAction);
            return;
        }

        CreateCuesFromDrop(expansion, targetId, mode, multiMode);
    }

    private void OnPopupCancelled()
    {
        _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "FileDropper: Drop cancelled by user.", (int)LogType.Info);
        CloseActivePopup();
        _pendingDrop = null;
        ClearPendingBoard();
    }

    /// <summary>
    /// Rich result from target detection.
    /// </summary>
    private readonly struct DropTarget
    {
        public FileDropTargetType TargetType { get; init; }
        public string TargetInfo { get; init; }
        public int TargetCueId { get; init; }
        public int BoardRow { get; init; }
        public int BoardCol { get; init; }
    }

    /// <summary>
    /// Opens a dropped <c>.c2</c> when the drop is on window chrome, or on the cuelist with no media files.
    /// </summary>
    private bool TryOpenDroppedShowfile(string[] files, DroppedMediaResult expansion)
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
        bool mediaAlso = (expansion?.FileCount ?? 0) > 0;
        bool openShow = dropInfo.TargetType == FileDropTargetType.None
                        || ((dropInfo.TargetType == FileDropTargetType.CueList
                             || dropInfo.TargetType == FileDropTargetType.ButtonBoard) && !mediaAlso);
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

        var board = ButtonBoard.Live;
        if (board != null && GodotObject.IsInstanceValid(board) && board.Visible
            && board.GetGlobalRect().HasPoint(mousePos))
        {
            board.TryGetCellAt(mousePos, out int row, out int col, out int cueId);
            GD.Print($"FileDropper:GetDropTarget - ButtonBoard cell ({row},{col}) cue={cueId}");
            return new DropTarget
            {
                TargetType = FileDropTargetType.ButtonBoard,
                TargetInfo = board.ListName ?? "Button Board",
                TargetCueId = cueId,
                BoardRow = row,
                BoardCol = col
            };
        }

        var cueListUi = CueList.Live;
        var cueContainer = cueListUi?.GetNodeOrNull<VBoxContainer>("%CueContainer");

        if (cueContainer != null && cueListUi.Visible)
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

        if (cueListUi != null && cueListUi.Visible && cueListUi.GetGlobalRect().HasPoint(mousePos))
        {
            return new DropTarget { TargetType = FileDropTargetType.CueList, TargetInfo = "Cue List", TargetCueId = -1 };
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
    private DroppedMediaResult _pendingDrop;
    private int _pendingTargetCueId;
    private DropInsertMode _pendingInsertMode = DropInsertMode.AtEnd;
    private ButtonBoard _pendingBoard;
    private int _pendingBoardRow = -1;
    private int _pendingBoardCol = -1;

    /// <summary>
    /// Creates cues from an expanded drop using the supplied parameters.
    /// Delegates to CueList.
    /// </summary>
    private void CreateCuesFromDrop(DroppedMediaResult expansion, int targetCueId, DropInsertMode insertMode, MultiFileDropMode multiFileMode)
    {
        if (expansion == null || expansion.FileCount == 0) return;

        if (_globalData?.Cuelist == null)
        {
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "FileDropper: No active CueList to create cues into.", (int)LogType.Error);
            return;
        }

        _globalData.Cuelist.CreateCuesFromDroppedFiles(expansion, targetCueId, insertMode, multiFileMode);
    }

    // --- Helpers ---

    private static string[] ToFileArray(DroppedMediaResult expansion)
    {
        if (expansion?.Files == null || expansion.Files.Count == 0)
            return Array.Empty<string>();
        if (expansion.Files is string[] arr)
            return arr;
        var copy = new string[expansion.Files.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = expansion.Files[i];
        return copy;
    }

    private static DroppedMediaResult DroppedMediaResultForFiles(string[] files)
    {
        if (files == null || files.Length == 0)
            return DroppedMediaResult.Empty;
        return new DroppedMediaResult(files, Array.Empty<DroppedMediaNode>(), false, 0);
    }

    private static IEnumerable<string> AllMediaGlobs()
    {
        return GlobalData.AudioFileFilters
            .Concat(GlobalData.VideoFileFilters)
            .Concat(GlobalData.ImageFileFilters);
    }

    private bool IsSupportedMediaFile(string path)
    {
        return DroppedMediaExpansion.IsSupportedMediaFile(path, AllMediaGlobs());
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
