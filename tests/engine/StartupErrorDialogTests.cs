// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Text;
using System.Threading.Tasks;
using Cue2.Services;
using Cue2.UI.Popups;
using GdUnit4;
using Godot;
using static GdUnit4.Assertions;

namespace Cue2.EngineTests;

/// <summary>
/// Startup dependency-fault overlay: long copy scrolls instead of covering Continue / Restart.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class StartupErrorDialogTests
{
    /// <summary>Loads the main window once for this suite.</summary>
    [Before]
    public async Task Boot() => await EngineHarness.BootAsync();

    /// <summary>Starts each test from an empty show.</summary>
    [BeforeTest]
    public async Task Reset() => await EngineHarness.ResetShowAsync();

    /// <summary>
    /// A long technical details block stays inside a scroll area, and Continue / Restart remain
    /// fully on screen.
    /// </summary>
    [TestCase(Timeout = 20000)]
    public async Task LongDetailsKeepActionButtonsVisible()
    {
        int mark = EngineHarness.ErrorMark();
        var dialog = StartupErrorDialog.Create(out string error);
        AssertThat(dialog)
            .OverrideFailureMessage($"Failed to load StartupErrorDialog: {error}")
            .IsNotNull();

        EngineHarness.Tree.Root.AddChild(dialog);

        var detail = new StringBuilder();
        for (int i = 0; i < 40; i++)
        {
            if (i > 0)
                detail.AppendLine();
            detail.Append("FFmpeg: native library failed to load from ");
            detail.Append("/very/long/path/to/bin/ffmpeg/libavcodec.so.");
            detail.Append(i);
        }

        var fault = new StartupFault(
            "ffmpeg",
            "FFmpeg",
            detail.ToString(),
            new[] { "Video playback", "Audio playback" });
        dialog.Configure(new[] { fault }, fault.AffectedFeatures);
        dialog.ShowConfigured();
        await EngineHarness.FramesAsync(8);

        var scroll = dialog.FindChild("ContentScroll", true, false) as ScrollContainer;
        var detailsLabel = dialog.FindChild("DetailsLabel", true, false) as Label;
        var continueBtn = dialog.FindChild("ContinueButton", true, false) as Button;
        var restartBtn = dialog.FindChild("RestartButton", true, false) as Button;
        var card = dialog.FindChild("Card", true, false) as Control;

        AssertThat(scroll).IsNotNull();
        AssertThat(detailsLabel).IsNotNull();
        AssertThat(continueBtn).IsNotNull();
        AssertThat(restartBtn).IsNotNull();
        AssertThat(card).IsNotNull();
        AssertThat(scroll.IsAncestorOf(detailsLabel)).IsTrue();
        AssertThat(continueBtn.Visible).IsTrue();
        AssertThat(restartBtn.Visible).IsTrue();
        AssertThat(continueBtn.Disabled).IsFalse();
        AssertThat(restartBtn.Disabled).IsFalse();

        Rect2 overlay = dialog.GetGlobalRect();
        AssertThat(overlay.Encloses(continueBtn.GetGlobalRect()))
            .OverrideFailureMessage("Continue is outside the overlay")
            .IsTrue();
        AssertThat(overlay.Encloses(restartBtn.GetGlobalRect()))
            .OverrideFailureMessage("Restart is outside the overlay")
            .IsTrue();
        AssertThat(card.Size.Y)
            .OverrideFailureMessage($"Card height {card.Size.Y} should stay below overlay {dialog.Size.Y}")
            .IsLess(dialog.Size.Y - 8f);
        AssertThat(detailsLabel.GetCombinedMinimumSize().Y)
            .OverrideFailureMessage("Details should be taller than the scroll so the bar appears")
            .IsGreater(scroll.Size.Y);

        dialog.QueueFree();
        await EngineHarness.FramesAsync(2);
        EngineHarness.AssertNoNewErrors(mark);
    }

    /// <summary>
    /// An Intel Mac fault uses the unsupported-hardware copy and hides Restart (retry will not help).
    /// </summary>
    [TestCase(Timeout = 20000)]
    public async Task IntelMacShowsUnsupportedHardwareCopy()
    {
        int mark = EngineHarness.ErrorMark();
        var dialog = StartupErrorDialog.Create(out string error);
        AssertThat(dialog)
            .OverrideFailureMessage($"Failed to load StartupErrorDialog: {error}")
            .IsNotNull();

        EngineHarness.Tree.Root.AddChild(dialog);

        var fault = new StartupFault(
            HostHardware.UnsupportedMacIntelId,
            "Hardware",
            HostHardware.UnsupportedMacIntelDetail,
            new[] { "Audio playback", "Video playback", "MIDI input and output" });
        dialog.Configure(new[] { fault }, fault.AffectedFeatures);
        dialog.ShowConfigured();
        await EngineHarness.FramesAsync(8);

        var title = dialog.FindChild("TitleLabel", true, false) as Label;
        var body = dialog.FindChild("BodyLabel", true, false) as Label;
        var details = dialog.FindChild("DetailsLabel", true, false) as Label;
        var continueBtn = dialog.FindChild("ContinueButton", true, false) as Button;
        var restartBtn = dialog.FindChild("RestartButton", true, false) as Button;

        AssertThat(title).IsNotNull();
        AssertThat(body).IsNotNull();
        AssertThat(details).IsNotNull();
        AssertThat(continueBtn).IsNotNull();
        AssertThat(restartBtn).IsNotNull();
        AssertThat(title.Text).Contains("Unsupported hardware");
        AssertThat(body.Text).Contains("Apple Silicon");
        AssertThat(body.Text).Contains("Intel");
        AssertThat(details.Visible).IsTrue();
        AssertThat(details.Text).Contains("Intel");
        AssertThat(continueBtn.Visible).IsTrue();
        AssertThat(restartBtn.Visible).IsFalse();

        dialog.QueueFree();
        await EngineHarness.FramesAsync(2);
        EngineHarness.AssertNoNewErrors(mark);
    }
}
