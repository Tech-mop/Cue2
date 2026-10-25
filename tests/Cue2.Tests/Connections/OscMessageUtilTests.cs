// SPDX-FileCopyrightText: 2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using NUnit.Framework;
using Cue2.Domain.Connections;

namespace Cue2.Tests.Connections;

/// <summary>
/// Parsing of OSC argument text and command lines typed by the operator.
/// </summary>
[TestFixture]
public class OscMessageUtilTests
{
    /// <summary>Ints, floats, bools, quoted and bare strings each get the right type.</summary>
    [Test]
    public void ParsesEachArgumentType()
    {
        object[] args = OscMessageUtil.ParseArgsText("1 2.5, true \"hi there\" 'it\\'s' bare -3 1e3");
        Assert.That(args, Is.EqualTo(new object[] { 1, 2.5f, true, "hi there", "it's", "bare", -3, 1000f }));
    }

    /// <summary>Empty input gives no arguments; an unclosed quote takes the rest of the line.</summary>
    [Test]
    public void EmptyAndUnclosedInput()
    {
        Assert.That(OscMessageUtil.ParseArgsText(null), Is.Empty);
        Assert.That(OscMessageUtil.ParseArgsText("  , ,  "), Is.Empty);
        Assert.That(OscMessageUtil.ParseArgsText("\"open quote"), Is.EqualTo(new object[] { "open quote" }));
    }

    /// <summary>Odd input never throws.</summary>
    [TestCase("\\")]
    [TestCase("\"\\")]
    [TestCase(",,,,")]
    [TestCase("99999999999999999999")]
    [TestCase("NaN Infinity")]
    [TestCase("\u00e9\u00e8 \U0001F600")]
    public void OddInputNeverThrows(string text)
    {
        Assert.That(() => OscMessageUtil.ParseArgsText(text), Throws.Nothing);
    }

    /// <summary>Formatting quotes strings and uses invariant numbers.</summary>
    [Test]
    public void FormatsArguments()
    {
        Assert.That(OscMessageUtil.FormatArgs(new object[] { 1, 2.5f, "x", false, null }), Is.EqualTo("1, 2.5, \"x\", false, null"));
        Assert.That(OscMessageUtil.FormatArgs(System.Array.Empty<object>()), Is.Empty);
    }

    /// <summary>A QLab-style line splits into address and args; explicit args win.</summary>
    [Test]
    public void SplitsPathAndArgs()
    {
        Assert.That(OscMessageUtil.SplitPathAndArgs("/jump 2", "", out string path, out string args), Is.True);
        Assert.That(path, Is.EqualTo("/jump"));
        Assert.That(args, Is.EqualTo("2"));

        Assert.That(OscMessageUtil.SplitPathAndArgs("cue/go  1 2", "", out path, out args), Is.True);
        Assert.That(path, Is.EqualTo("/cue/go"));
        Assert.That(args, Is.EqualTo("1 2"));

        Assert.That(OscMessageUtil.SplitPathAndArgs("/jump 2", "5", out _, out args), Is.True);
        Assert.That(args, Is.EqualTo("5"));

        Assert.That(OscMessageUtil.SplitPathAndArgs("   ", "", out _, out _), Is.False);
    }

    /// <summary>Typed reads convert between numbers and refuse out-of-range indexes.</summary>
    [Test]
    public void TypedReads()
    {
        object[] args = { 2.6f, "3.5", "word", 7 };
        Assert.That(OscMessageUtil.TryGetInt(args, 0, out int i), Is.True);
        Assert.That(i, Is.EqualTo(3));
        Assert.That(OscMessageUtil.TryGetFloat(args, 1, out double d), Is.True);
        Assert.That(d, Is.EqualTo(3.5));
        Assert.That(OscMessageUtil.TryGetFloat(args, 2, out _), Is.False);
        Assert.That(OscMessageUtil.TryGetString(args, 3, out string s), Is.True);
        Assert.That(s, Is.EqualTo("7"));
        Assert.That(OscMessageUtil.TryGetFloat(args, 9, out _), Is.False);
        Assert.That(OscMessageUtil.TryGetFloat(null, 0, out _), Is.False);
    }
}
