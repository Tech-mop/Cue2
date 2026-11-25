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

    [Test]
    public void InvalidScaleFallsBackToOne()
    {
        Assert.That(
            UiUtilities.ClampOptionPopupHeightPixels(1600, 280, 0f, 1080),
            Is.EqualTo(280));
    }
}
