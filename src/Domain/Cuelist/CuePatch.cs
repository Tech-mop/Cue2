// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Godot.Collections;

namespace Cue2.Domain.Cuelist;


/// <summary>
/// Represents per-cue routing matrix for audio channels to output channels with volumes.
/// </summary>
public class CuePatch
{
    public int InputChannels { get; private set; }
    public List<string> InputLabels { get; private set; }
    public int OutputChannels { get; private set; }
    public List<string> OutputLabels { get; private set; }
    public float[,] VolumeMatrix { get; private set; } // Linear volumes [input, output]

    /// <summary>
    /// Initializes with defaults: identity mapping at 1.0 where applicable.
    /// </summary>
    /// <param name="inputCh">Input channels.</param>
    /// <param name="inputLabels">Input labels.</param>
    /// <param name="outputCh">Output channels.</param>
    /// <param name="outputLabels">Output labels.</param>
    public CuePatch(int inputCh, List<string> inputLabels, int outputCh, List<string> outputLabels)
    {
        InputChannels = inputCh;
        InputLabels = inputLabels;
        OutputChannels = outputCh;
        OutputLabels = outputLabels;
        VolumeMatrix = new float[inputCh, outputCh];

        // Default 1:1 at 1.0, others 0.0
        var minCh = Math.Min(inputCh, outputCh);
        for (int i = 0; i < minCh; i++)
        {
            VolumeMatrix[i, i] = 1.0f;
        }
    }
    
    /// <summary>
    /// Derfault constructor for deserialisation.
    /// </summary>
    public CuePatch() {}

    
    /// <summary>
    /// Gets volume for specific input-output pair.
    /// </summary>
    /// <param name="inputCh"></param>
    /// <param name="outputCh"></param>
    /// <returns></returns>
    public float GetVolume(int inputCh, int outputCh)
    {
        if (inputCh < 0 || inputCh >= InputChannels || outputCh < 0 || outputCh >= OutputChannels)
        {
            throw new IndexOutOfRangeException("Invalid channel index.");
        }
        return VolumeMatrix[inputCh, outputCh];
    }

    
    /// <summary>
    /// Sets volume for specific input-output pair.
    /// </summary>
    /// <param name="inputCh"></param>
    /// <param name="outputCh"></param>
    /// <param name="linearVol"></param>
    /// <exception cref="IndexOutOfRangeException"></exception>
    public void SetVolume(int inputCh, int outputCh, float linearVol)
    {
        if (inputCh < 0 || inputCh >= InputChannels || outputCh < 0 || outputCh >= OutputChannels)
        {
            throw new IndexOutOfRangeException("Invalid channel index.");
        }

        if (linearVol < 0.0f || linearVol > 1.0f)
        {
            linearVol = Math.Clamp(linearVol, 0.0f, 1.0f);
        }
        VolumeMatrix[inputCh, outputCh] = linearVol;
    }

    /// <summary>
    /// Serialises to Dictionary for saving.
    /// </summary>
    public Dictionary GetData()
    {
        var data = new Dictionary();
        data.Add("InputChannels", InputChannels);
        data.Add("InputLabels", new Array<string>(InputLabels));
        data.Add("OutputChannels", OutputChannels);
        data.Add("OutputLabels", new Array<string>(OutputLabels));

        var matrixData = new Godot.Collections.Array();
        for (int i = 0; i < InputChannels; i++)
        {
            var row = new Godot.Collections.Array();
            for (int j = 0; j < OutputChannels; j++)
            {
                row.Add(VolumeMatrix[i, j]);
            }
            matrixData.Add(row);
        }
        data.Add("VolumeMatrix", matrixData);
        return data;
    }

    /// <summary>
    /// Deep-clones this routing matrix (used for per-playback runtime copies).
    /// </summary>
    /// <returns>Independent <see cref="CuePatch"/> with the same dimensions and levels.</returns>
    public CuePatch Clone()
    {
        var clone = new CuePatch();
        clone.LoadFromData(GetData());
        return clone;
    }
    
    
    /// <summary>
    /// Loads from Dictionary.
    /// </summary>
    public void LoadFromData(Dictionary dataDict)
    {
        InputChannels = dataDict["InputChannels"].AsInt32();
        InputLabels = new List<string>();
        if (dataDict.ContainsKey("InputLabels"))
        {
            var inLabels = dataDict["InputLabels"].AsGodotArray();
            foreach (var label in inLabels)
                InputLabels.Add(label.AsString());
        }
        OutputChannels = dataDict["OutputChannels"].AsInt32();
        OutputLabels = new List<string>();
        if (dataDict.ContainsKey("OutputLabels"))
        {
            var outLabels = dataDict["OutputLabels"].AsGodotArray();
            foreach (var label in outLabels)
                OutputLabels.Add(label.AsString());
        }

        var matrixData = dataDict["VolumeMatrix"].AsGodotArray();
        VolumeMatrix = new float[InputChannels, OutputChannels];
        for (int i = 0; i < InputChannels; i++)
        {
            var row = matrixData[i].AsGodotArray();
            for (int j = 0; j < OutputChannels; j++)
            {
                // After JSON history clone values are typically doubles.
                VolumeMatrix[i, j] = row[j].AsSingle();
            }
        }
    }
    
}