// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using Cue2.Domain.Devices;
using NUnit.Framework;

namespace Cue2.Tests.Devices;

/// <summary>
/// Canvas-layer rotation is stored in <c>[-360, 360)</c> so showfiles stay tidy.
/// </summary>
[TestFixture]
public class VideoTargetLayerRotationTests
{
    [Test]
    public void UnrotatedAndHalfTurnStayPut()
    {
        Assert.That(VideoTargetLayer.NormalizeRotation(0f), Is.EqualTo(0f));
        Assert.That(VideoTargetLayer.NormalizeRotation(180f), Is.EqualTo(180f));
        Assert.That(VideoTargetLayer.NormalizeRotation(-90f), Is.EqualTo(-90f));
        Assert.That(VideoTargetLayer.NormalizeRotation(-360f), Is.EqualTo(-360f));
    }

    [Test]
    public void FullTurnWrapsToNegativeFullTurn()
    {
        Assert.That(VideoTargetLayer.NormalizeRotation(360f), Is.EqualTo(-360f));
    }

    [Test]
    public void ValuesOutsideTheEditorRangeWrap()
    {
        Assert.That(VideoTargetLayer.NormalizeRotation(370f), Is.EqualTo(-350f).Within(0.0001f));
        Assert.That(VideoTargetLayer.NormalizeRotation(-370f), Is.EqualTo(350f).Within(0.0001f));
        Assert.That(VideoTargetLayer.NormalizeRotation(720f), Is.EqualTo(0f).Within(0.0001f));
    }
}
