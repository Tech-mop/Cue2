// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Cue2.Domain.Devices;

namespace Cue2.UI.Settings.PatchMatrix;

/// <summary>Shared pixel metrics for the audio output patch matrix.</summary>
public static class PatchMatrixMetrics
{
    /// <summary>Cell and bus-row height/width for an expanded hardware column.</summary>
    public const float CellSize = 32f;

    /// <summary>Width of the pinned bus-name column.</summary>
    public const float RowHeaderWidth = 180f;

    /// <summary>Hardware outputs grouped for collapse.</summary>
    public const int GroupSize = 8;

    /// <summary>Device-name band in the pinned column header.</summary>
    public const float DeviceNameRow = 22f;

    /// <summary>Group collapse chips (shown when any device has more than <see cref="GroupSize"/> outputs).</summary>
    public const float GroupRow = 18f;

    /// <summary>1-based hardware channel numbers.</summary>
    public const float NumberRow = 18f;

    /// <summary>Floor height for an empty routing grid (one bus row).</summary>
    public const float ViewportMinHeight = CellSize;

    /// <summary>Header height including an optional group row.</summary>
    /// <param name="showGroups">True when at least one device uses group chips.</param>
    /// <returns>Total header height in pixels.</returns>
    public static float HeaderHeight(bool showGroups) =>
        DeviceNameRow + (showGroups ? GroupRow : 0f) + NumberRow;
}

/// <summary>One visible column in the routing grid (a hardware output, or a collapsed group).</summary>
public sealed class PatchMatrixColumn
{
    /// <summary>Device this column belongs to.</summary>
    public string DeviceName { get; init; }

    /// <summary>Index into the patch’s output list, or -1 for a collapsed-group placeholder.</summary>
    public int OutputIndex { get; init; }

    /// <summary>0-based hardware channel (placeholder uses the group’s first channel).</summary>
    public int DeviceChannel { get; init; }

    /// <summary>0-based group index (<c>outputIndex / 8</c>).</summary>
    public int GroupIndex { get; init; }

    /// <summary>True when this column stands in for a collapsed group of 8.</summary>
    public bool IsGroupPlaceholder { get; init; }

    /// <summary>First output-list index in the group.</summary>
    public int GroupOutputStart { get; init; }

    /// <summary>How many outputs the group covers.</summary>
    public int GroupOutputCount { get; init; }

    /// <summary>True when the device is assigned but currently unavailable.</summary>
    public bool DeviceMissing { get; init; }

    /// <summary>Column width in pixels.</summary>
    public float Width { get; init; }
}

/// <summary>Horizontal span of one device in the column header.</summary>
public sealed class PatchMatrixDeviceSpan
{
    /// <summary>Device name (patch key).</summary>
    public string Name { get; init; }

    /// <summary>Left edge in header/cell coordinates.</summary>
    public float X { get; init; }

    /// <summary>Total width of visible columns for this device.</summary>
    public float Width { get; set; }

    /// <summary>True when the device is assigned but currently unavailable.</summary>
    public bool Missing { get; init; }

    /// <summary>Group chips inside this device.</summary>
    public List<PatchMatrixGroupSpan> Groups { get; } = new();
}

/// <summary>One group-of-8 chip inside a device header.</summary>
public sealed class PatchMatrixGroupSpan
{
    /// <summary>0-based group index.</summary>
    public int GroupIndex { get; init; }

    /// <summary>Left edge in header/cell coordinates.</summary>
    public float X { get; init; }

    /// <summary>Chip width (one cell when collapsed, N cells when expanded).</summary>
    public float Width { get; init; }

    /// <summary>True when the group is collapsed to a placeholder column.</summary>
    public bool Collapsed { get; init; }

    /// <summary>First output-list index in the group.</summary>
    public int FirstOutputIndex { get; init; }

    /// <summary>Outputs in the group.</summary>
    public int OutputCount { get; init; }

    /// <summary>0-based first hardware channel (inclusive).</summary>
    public int FirstHw { get; init; }

    /// <summary>0-based last hardware channel (inclusive).</summary>
    public int LastHw { get; init; }
}

/// <summary>Layout snapshot for the frozen header + cell grid.</summary>
public sealed class PatchMatrixView
{
    /// <summary>Patch bus ids in row order.</summary>
    public List<int> BusIds { get; } = new();

    /// <summary>Patch bus names in row order.</summary>
    public List<string> BusNames { get; } = new();

    /// <summary>Visible columns (expanded outputs and collapsed placeholders).</summary>
    public List<PatchMatrixColumn> Columns { get; } = new();

    /// <summary>Device header spans.</summary>
    public List<PatchMatrixDeviceSpan> Devices { get; } = new();

    /// <summary>True when the group-chip row is shown.</summary>
    public bool ShowGroupRow { get; set; }

    /// <summary>Pinned header height.</summary>
    public float HeaderHeight { get; set; }

    /// <summary>Sum of column widths.</summary>
    public float TotalWidth { get; set; }

    /// <summary>Cell-grid height (rows × cell size).</summary>
    public float BodyHeight { get; set; }
}

/// <summary>Builds a <see cref="PatchMatrixView"/> from patch data and UI collapse state.</summary>
public static class PatchMatrixViewBuilder
{
    /// <summary>
    /// Rebuilds column/device layout. Collapse sets are mutated only by the caller;
    /// missing keys mean “all groups expanded”.
    /// </summary>
    /// <param name="patch">Patch to display.</param>
    /// <param name="availableDevices">Currently enumerated playback device names.</param>
    /// <param name="collapsedGroups">Per-device set of collapsed group indices.</param>
    /// <returns>A view ready for the header and cell controls.</returns>
    public static PatchMatrixView Build(
        AudioOutputPatch patch,
        IReadOnlyCollection<string> availableDevices,
        IReadOnlyDictionary<string, HashSet<int>> collapsedGroups)
    {
        var view = new PatchMatrixView();
        if (patch?.Channels != null)
        {
            foreach (var bus in patch.Channels)
            {
                view.BusIds.Add(bus.Key);
                view.BusNames.Add(bus.Value ?? string.Empty);
            }

            // Stable row order by bus id (same as the previous VBox).
            var order = new List<int>(view.BusIds.Count);
            for (int i = 0; i < view.BusIds.Count; i++)
                order.Add(i);
            order.Sort((a, b) => view.BusIds[a].CompareTo(view.BusIds[b]));

            var ids = new List<int>(view.BusIds.Count);
            var names = new List<string>(view.BusNames.Count);
            foreach (int i in order)
            {
                ids.Add(view.BusIds[i]);
                names.Add(view.BusNames[i]);
            }
            view.BusIds.Clear();
            view.BusNames.Clear();
            view.BusIds.AddRange(ids);
            view.BusNames.AddRange(names);
        }

        view.BodyHeight = Math.Max(view.BusIds.Count * PatchMatrixMetrics.CellSize, PatchMatrixMetrics.CellSize);

        if (patch?.OutputDevices == null || patch.OutputDevices.Count == 0)
        {
            view.ShowGroupRow = false;
            view.HeaderHeight = PatchMatrixMetrics.HeaderHeight(false);
            view.TotalWidth = 1f;
            return view;
        }

        bool showGroups = false;
        foreach (var kv in patch.OutputDevices)
        {
            if (kv.Value != null && kv.Value.Count > PatchMatrixMetrics.GroupSize)
            {
                showGroups = true;
                break;
            }
        }

        view.ShowGroupRow = showGroups;
        view.HeaderHeight = PatchMatrixMetrics.HeaderHeight(showGroups);

        var available = availableDevices == null
            ? new HashSet<string>()
            : new HashSet<string>(availableDevices);

        float x = 0f;
        foreach (var device in patch.OutputDevices)
        {
            var outputs = device.Value;
            if (outputs == null || outputs.Count == 0)
                continue;

            bool missing = !available.Contains(device.Key);
            HashSet<int> collapsed = null;
            collapsedGroups?.TryGetValue(device.Key, out collapsed);

            var span = new PatchMatrixDeviceSpan
            {
                Name = device.Key,
                X = x,
                Missing = missing
            };

            int groupCount = (outputs.Count + PatchMatrixMetrics.GroupSize - 1) / PatchMatrixMetrics.GroupSize;
            for (int g = 0; g < groupCount; g++)
            {
                int start = g * PatchMatrixMetrics.GroupSize;
                int count = Math.Min(PatchMatrixMetrics.GroupSize, outputs.Count - start);
                bool isCollapsed = showGroups && collapsed != null && collapsed.Contains(g);

                int firstHw = outputs[start].ResolveDeviceChannel(start);
                int lastHw = outputs[start + count - 1].ResolveDeviceChannel(start + count - 1);

                var group = new PatchMatrixGroupSpan
                {
                    GroupIndex = g,
                    X = x,
                    Width = isCollapsed ? PatchMatrixMetrics.CellSize : count * PatchMatrixMetrics.CellSize,
                    Collapsed = isCollapsed,
                    FirstOutputIndex = start,
                    OutputCount = count,
                    FirstHw = firstHw,
                    LastHw = lastHw
                };
                span.Groups.Add(group);

                if (isCollapsed)
                {
                    view.Columns.Add(new PatchMatrixColumn
                    {
                        DeviceName = device.Key,
                        OutputIndex = -1,
                        DeviceChannel = firstHw,
                        GroupIndex = g,
                        IsGroupPlaceholder = true,
                        GroupOutputStart = start,
                        GroupOutputCount = count,
                        DeviceMissing = missing,
                        Width = PatchMatrixMetrics.CellSize
                    });
                    x += PatchMatrixMetrics.CellSize;
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        int idx = start + i;
                        view.Columns.Add(new PatchMatrixColumn
                        {
                            DeviceName = device.Key,
                            OutputIndex = idx,
                            DeviceChannel = outputs[idx].ResolveDeviceChannel(idx),
                            GroupIndex = g,
                            IsGroupPlaceholder = false,
                            GroupOutputStart = start,
                            GroupOutputCount = count,
                            DeviceMissing = missing,
                            Width = PatchMatrixMetrics.CellSize
                        });
                        x += PatchMatrixMetrics.CellSize;
                    }
                }
            }

            span.Width = x - span.X;
            view.Devices.Add(span);
        }

        view.TotalWidth = Math.Max(x, 1f);
        return view;
    }
}
