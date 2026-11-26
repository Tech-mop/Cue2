// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using Cue2.UI.Utilities;

namespace Cue2.Tests.Ui;

/// <summary>
/// Height cap for long OptionButton lists (audio input / timecode channel pickers).
/// </summary>
[TestFixture]
public class OptionPopupHeightTests
{
    [Test]
    public void ShortListHugsContent()
    {
        Assert.That(
            UiUtilities.ClampOptionPopupHeightPixels(120, 280, 1f, 1080),
            Is.EqualTo(120));
    }

    [Test]
    public void LongListCapsAtScaledMax()
    {
        Assert.That(
            UiUtilities.ClampOptionPopupHeightPixels(1600, 280, 1f, 1080),
            Is.EqualTo(280));
        Assert.That(
            UiUtilities.ClampOptionPopupHeightPixels(1600, 280, 2f, 2000),
            Is.EqualTo(560));
    }

    [Test]
    public void CapRespectsUsableScreen()
    {
        Assert.That(
            UiUtilities.ClampOptionPopupHeightPixels(1600, 280, 1f, 200),
            Is.EqualTo(152));
    }

    /// <summary>macOS shows Option; Windows and Linux show Alt.</summary>
    [Test]
    public void AltModifierLabelIsOptionOnMacOs()
    {
        Assert.That(UiUtilities.AltModifierLabel("macOS"), Is.EqualTo("Option"));
        Assert.That(UiUtilities.AltModifierLabel("Windows"), Is.EqualTo("Alt"));
        Assert.That(UiUtilities.AltModifierLabel("Linux"), Is.EqualTo("Alt"));
        Assert.That(UiUtilities.AltModifierLabel(null), Is.EqualTo("Alt"));
        Assert.That(UiUtilities.AltModifierLabel(""), Is.EqualTo("Alt"));
    }

    /// <summary>Rotation tooltip is a complete sentence, never a leftover {0} placeholder.</summary>
    [Test]
    public void LayerRotationTooltipFillsModifierAndHasNoPlaceholder()
    {
        string mac = UiUtilities.LayerRotationTooltipEnglish("macOS");
        string win = UiUtilities.LayerRotationTooltipEnglish("Windows");
        Assert.That(mac, Does.Contain("Hold Option and drag"));
        Assert.That(win, Does.Contain("Hold Alt and drag"));
        Assert.That(mac, Does.Not.Contain("{0}"));
        Assert.That(win, Does.Not.Contain("{0}"));
    }

    [Test]
    public void InvalidScaleFallsBackToOne()
    {
        Assert.That(
            UiUtilities.ClampOptionPopupHeightPixels(1600, 280, 0f, 1080),
            Is.EqualTo(280));
    }
}
