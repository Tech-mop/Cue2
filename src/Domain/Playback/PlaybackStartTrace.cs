// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Threading;
using Godot;

namespace Cue2.Domain.Playback;

/// <summary>
/// Start timing for one playback, for measuring GO latency. Logs once per play how long after
/// GO (or after play, when the GO time is not known) the first picture was uploaded, the
/// first audio was queued, and the output device first took audio. Diagnostic only.
/// </summary>
internal sealed class PlaybackStartTrace
{
    private string _label = string.Empty;
    private long _goTicks;
    private long _playTicks;
    private int _pictureLogged = 1;
    private int _audioLogged = 1;
    private long _primedBytes = -1;

    /// <summary>Label (cue and file) used in the log lines.</summary>
    public string Label => _label;

    /// <summary>Label (cue and file) and GO time for the next play. 0 = GO time unknown.</summary>
    public void Prepare(string label, long goTicks)
    {
        _label = label ?? string.Empty;
        _goTicks = goTicks;
    }

    /// <summary>Starts timing a play. Main thread.</summary>
    public void Arm()
    {
        _playTicks = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _primedBytes, -1);
        Volatile.Write(ref _pictureLogged, 0);
        Volatile.Write(ref _audioLogged, 0);
    }

    /// <summary>The first picture of this play was uploaded. Main thread.</summary>
    public void NotePicture()
    {
        if (Interlocked.Exchange(ref _pictureLogged, 1) != 0)
            return;
        PlaybackTimingRecorder.Record(_label, PlaybackTimingRecorder.StageFirstPicture, _goTicks, _playTicks);
        GD.Print($"PlaybackStartTrace:NotePicture - Start timing {_label}: first picture {Describe()}");
    }

    /// <summary>The output streams were primed; <paramref name="queuedBytes"/> is the smallest stream queue.</summary>
    public void NoteAudioPrimed(long queuedBytes)
    {
        if (Volatile.Read(ref _audioLogged) != 0 || queuedBytes <= 0)
            return;
        Interlocked.Exchange(ref _primedBytes, queuedBytes);
        PlaybackTimingRecorder.Record(_label, PlaybackTimingRecorder.StageAudioQueued, _goTicks, _playTicks);
        GD.Print($"PlaybackStartTrace:NoteAudioPrimed - Start timing {_label}: audio queued {Describe()}");
    }

    /// <summary>A stream queue was read by the fill loop; the first drop below the primed level is logged.</summary>
    public void NoteAudioQueued(long queuedBytes)
    {
        long primed = Interlocked.Read(ref _primedBytes);
        if (primed <= 0 || queuedBytes < 0 || queuedBytes >= primed)
            return;
        if (Interlocked.Exchange(ref _audioLogged, 1) != 0)
            return;
        PlaybackTimingRecorder.Record(_label, PlaybackTimingRecorder.StageOutputTookAudio, _goTicks, _playTicks);
        GD.Print($"PlaybackStartTrace:NoteAudioQueued - Start timing {_label}: output started taking audio {Describe()}");
    }

    private string Describe()
    {
        long now = Stopwatch.GetTimestamp();
        double fromPlay = (now - _playTicks) * 1000.0 / Stopwatch.Frequency;
        if (_goTicks == 0)
            return $"{fromPlay:F0} ms after play";
        double fromGo = (now - _goTicks) * 1000.0 / Stopwatch.Frequency;
        return $"{fromGo:F0} ms after GO ({fromPlay:F0} ms after play)";
    }
}
