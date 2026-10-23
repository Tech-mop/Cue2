// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System.Threading;

namespace Cue2.Media.Decoders;

/// <summary>
/// Decode-worker counters for one <see cref="VideoSourceDecoder"/>.
/// Writes use interlocked operations so the main thread can snapshot them
/// without taking the decode lock.
/// </summary>
public sealed class VideoDecodeStats
{
    private long _decoded;
    private long _converted;
    private long _droppedBeforeConvert;
    private long _keptToCatchUp;
    private long _nonRefSkipStarts;
    private long _decodeTicks;
    private long _decodeMaxTicks;
    private long _convertTicks;
    private long _convertMaxTicks;
    private long _copyTicks;
    private long _copyMaxTicks;
    private int _minRingDepth = int.MaxValue;
    private long _prepareTicks;
    private long _prepareMaxTicks;
    private long _prepared;

    /// <summary>Adds the time to make one frame's Godot plane images on the worker, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</summary>
    public void AddPrepare(long ticks)
    {
        AddSample(ref _prepareTicks, ref _prepareMaxTicks, ticks);
        Interlocked.Increment(ref _prepared);
    }

    /// <summary>Total and longest image-making time, and the number of frames. Safe from any thread.</summary>
    public void ReadPrepare(out long ticks, out long maxTicks, out long count)
    {
        ticks = Interlocked.Read(ref _prepareTicks);
        maxTicks = Interlocked.Read(ref _prepareMaxTicks);
        count = Interlocked.Read(ref _prepared);
    }

    /// <summary>One decoded picture was produced by the codec.</summary>
    public void NoteDecoded() => Interlocked.Increment(ref _decoded);

    /// <summary>One picture was converted into a pooled buffer.</summary>
    public void NoteConverted() => Interlocked.Increment(ref _converted);

    /// <summary>A decoded picture was discarded before colour conversion.</summary>
    public void NoteDroppedBeforeConvert() => Interlocked.Increment(ref _droppedBeforeConvert);

    /// <summary>A late picture was kept after a run of drops so the output keeps moving.</summary>
    public void NoteKeptToCatchUp() => Interlocked.Increment(ref _keptToCatchUp);

    /// <summary>The codec was asked to skip non-reference pictures to catch up.</summary>
    public void NoteNonRefSkipStart() => Interlocked.Increment(ref _nonRefSkipStarts);

    /// <summary>Adds a demux/decode interval, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</summary>
    public void AddDecode(long ticks) => AddSample(ref _decodeTicks, ref _decodeMaxTicks, ticks);

    /// <summary>Adds a colour-conversion interval, in <see cref="System.Diagnostics.Stopwatch"/> ticks.</summary>
    public void AddConvert(long ticks) => AddSample(ref _convertTicks, ref _convertMaxTicks, ticks);

    /// <summary>Adds a plane copy interval (Y and UV planes into pooled buffers), in <see cref="System.Diagnostics.Stopwatch"/> ticks.</summary>
    public void AddCopy(long ticks) => AddSample(ref _copyTicks, ref _copyMaxTicks, ticks);

    /// <summary>Records the ring depth after a convert. The minimum is kept.</summary>
    public void NoteRingDepth(int depth)
    {
        int current = Volatile.Read(ref _minRingDepth);
        while (depth < current)
        {
            int prev = Interlocked.CompareExchange(ref _minRingDepth, depth, current);
            if (prev == current)
                return;
            current = prev;
        }
    }

    /// <summary>Copies the counters. Safe from any thread.</summary>
    public Snapshot Copy()
    {
        return new Snapshot(
            Interlocked.Read(ref _decoded),
            Interlocked.Read(ref _converted),
            Interlocked.Read(ref _droppedBeforeConvert),
            Interlocked.Read(ref _keptToCatchUp),
            Interlocked.Read(ref _nonRefSkipStarts),
            Interlocked.Read(ref _decodeTicks),
            Interlocked.Read(ref _decodeMaxTicks),
            Interlocked.Read(ref _convertTicks),
            Interlocked.Read(ref _convertMaxTicks),
            Interlocked.Read(ref _copyTicks),
            Interlocked.Read(ref _copyMaxTicks),
            Volatile.Read(ref _minRingDepth));
    }

    private static void AddSample(ref long sum, ref long max, long ticks)
    {
        if (ticks < 0)
            ticks = 0;
        Interlocked.Add(ref sum, ticks);
        long current = Interlocked.Read(ref max);
        while (ticks > current)
        {
            long prev = Interlocked.CompareExchange(ref max, ticks, current);
            if (prev == current)
                return;
            current = prev;
        }
    }

    /// <summary>Point-in-time copy of <see cref="VideoDecodeStats"/>.</summary>
    public readonly struct Snapshot
    {
        /// <summary>Creates a snapshot.</summary>
        public Snapshot(
            long decoded,
            long converted,
            long droppedBeforeConvert,
            long keptToCatchUp,
            long nonRefSkipStarts,
            long decodeTicks,
            long decodeMaxTicks,
            long convertTicks,
            long convertMaxTicks,
            long copyTicks,
            long copyMaxTicks,
            int minRingDepth)
        {
            Decoded = decoded;
            Converted = converted;
            DroppedBeforeConvert = droppedBeforeConvert;
            KeptToCatchUp = keptToCatchUp;
            NonRefSkipStarts = nonRefSkipStarts;
            DecodeTicks = decodeTicks;
            DecodeMaxTicks = decodeMaxTicks;
            ConvertTicks = convertTicks;
            ConvertMaxTicks = convertMaxTicks;
            CopyTicks = copyTicks;
            CopyMaxTicks = copyMaxTicks;
            MinRingDepth = minRingDepth;
        }

        /// <summary>Pictures returned by the codec.</summary>
        public long Decoded { get; }

        /// <summary>Pictures converted into a pooled buffer.</summary>
        public long Converted { get; }

        /// <summary>Pictures discarded before conversion.</summary>
        public long DroppedBeforeConvert { get; }

        /// <summary>Late pictures kept after a run of drops.</summary>
        public long KeptToCatchUp { get; }

        /// <summary>Times the codec started skipping non-reference pictures.</summary>
        public long NonRefSkipStarts { get; }

        /// <summary>Sum of demux/decode intervals, in stopwatch ticks.</summary>
        public long DecodeTicks { get; }

        /// <summary>Longest demux/decode interval, in stopwatch ticks.</summary>
        public long DecodeMaxTicks { get; }

        /// <summary>Sum of colour-conversion intervals, in stopwatch ticks.</summary>
        public long ConvertTicks { get; }

        /// <summary>Longest colour-conversion interval, in stopwatch ticks.</summary>
        public long ConvertMaxTicks { get; }

        /// <summary>Sum of packed-frame copy intervals, in stopwatch ticks.</summary>
        public long CopyTicks { get; }

        /// <summary>Longest packed-frame copy interval, in stopwatch ticks.</summary>
        public long CopyMaxTicks { get; }

        /// <summary>Smallest ring depth seen, or <see cref="int.MaxValue"/> when none was recorded.</summary>
        public int MinRingDepth { get; }
    }
}
