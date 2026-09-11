// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using Godot;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Cue2.Domain.Connections;
using Godot.Collections;

#nullable enable

namespace Cue2.Services;

/// <summary>
/// Manages a collection of CueLight instances, including creation, connection, and session integration.
/// </summary>
public partial class CueLightManager : Node
{
    #pragma warning disable CS8618 // Fields are initialized in _Ready
    private GlobalSignals _globalSignals;
    private GlobalData _globalData;
    #pragma warning restore CS8618
    private System.Collections.Generic.Dictionary<int, CueLight> _cueLights = new();
    private int _nextId = 0;

    public override void _Ready()
    {
        _globalSignals = GetNode<GlobalSignals>("/root/GlobalSignals");
        _globalData = GetNode<GlobalData>("/root/GlobalData");
        GD.Print("CueLightManager:_Ready - Initialized");

        TreeExiting += Clean;
    }

    /// <summary>
    /// Creates a new CueLight instance.
    /// </summary>
    public CueLight CreateCueLight()
    {
        var cueLight = new CueLight(_nextId++);
        _cueLights[cueLight.Id] = cueLight;
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            $"CueLightManager:CreateCueLight - Created {_nextId - 1}: {cueLight.Name}", 0);
        return cueLight;
    }
    
    /// <summary>
    /// Creates a new CueLight instance from given IP address
    /// </summary>
    public CueLight CreateCueLightWithIp(string ipAddress)
    {
        var cueLight = new CueLight(_nextId++);
        cueLight.Name = $"CueLight_{ipAddress.Split('.')[3]}";
        cueLight.SetIpAddress(ipAddress);
        _cueLights[cueLight.Id] = cueLight;
        _globalSignals?.EmitSignal(nameof(GlobalSignals.Log),
            $"CueLightManager:CreateCueLight - Created {_nextId - 1}: {cueLight.Name}", 0);
        return cueLight;
    }

    /// <summary>
    /// Retrieves a CueLight by ID.
    /// </summary>
    public CueLight? GetCueLight(int id) => _cueLights.TryGetValue(id, out var cl) ? cl : null;


    public void DeleteCueLight(CueLight cueLight)
    {
        if (_cueLights.Remove(cueLight.Id))
        {
            cueLight.Dispose();
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log),
                $"CueLightManager:DeleteCueLight - Removed CueLight {cueLight.Id} ({cueLight.Name})", 0);
        }
    }

    /// <summary>
    /// Returns an array of all cuelights
    /// </summary>
    /// <returns></returns>
    public Array<CueLight> GetCueLights()
    {
        var result = new Array<CueLight>();
        foreach (var cueLight in _cueLights.Values)
        {
            result.Add(cueLight);
        }
        return result;
    }

    public async void AllGo(string cueNum = "")
    {
        foreach (var cueLight in _cueLights.Values)
        {
            await cueLight.GoAsync(cueNum);
        }
    }

    public async void AllStandby(string cueNum = "")
    {
        foreach (var cueLight in _cueLights.Values)
        {
            if (cueLight.CueLightIsConnected)
                await cueLight.StandbyAsync(cueNum);
        }
    }
    
    public async void AllCancel()
    {
        foreach (var cueLight in _cueLights.Values)
        {
            if (cueLight.CueLightIsConnected)
                await cueLight.CancelAsync();
        }
    }

    public async void AllCountIn(int timeUntilGo = 3, string cueNum = "")
    {
        foreach (var cueLight in _cueLights.Values)
        {
            if (cueLight.CueLightIsConnected)
                await cueLight.CountInAsync(timeUntilGo, cueNum);
        }
    }

    public async void AllIdentify(bool state)
    {
        foreach (var cueLight in _cueLights.Values)
        {
            if (cueLight.CueLightIsConnected)
                await cueLight.IdentifyAsync(state);
        }
    }
    
    

    private void Clean()
    {
        // Snapshot keys — disposing mutates the dictionary.
        foreach (var cueLight in _cueLights.Values.ToList())
        {
            if (_cueLights.Remove(cueLight.Id))
                cueLight.Dispose();
        }
    }
    
    /// <summary>
    /// Discovers cue lights on the local network using UDP broadcast.
    /// Sends "PING" to the broadcast address and collects IPs that respond with "PONG".
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds to wait for responses.</param>
    /// <returns>A list of discovered IP addresses.</returns>
    public async Task<List<string>> DiscoverCueLightsAsync(int timeoutMs = 2000)
    {
        var discoveredIps = new List<string>();
        UdpClient? udpClient = null;

        try
        {
            udpClient = new UdpClient();
            udpClient.EnableBroadcast = true; // Required for broadcast
            // Note: No ReceiveTimeout set, as it doesn't apply to async receives reliably 

            // Send "PING" to broadcast address on port 80
            byte[] sendBytes = Encoding.UTF8.GetBytes("PING");
            await udpClient!.SendAsync(sendBytes, sendBytes.Length, new IPEndPoint(IPAddress.Broadcast, 80));
            GD.Print($"CueLightManager:DiscoverCueLightsAsync - Sent broadcast PING"); 
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), "Sent UDP broadcast for cue light discovery", 0);

            // Listen for responses with per-receive timeout using Task.WhenAny 
            var endTime = DateTime.UtcNow.AddMilliseconds(timeoutMs); 
            while (DateTime.UtcNow < endTime) 
            {
                var remainingMs = (int)(endTime - DateTime.UtcNow).TotalMilliseconds; 
                remainingMs = Math.Max(remainingMs, 1); // Avoid zero or negative 

                var receiveTask = udpClient.ReceiveAsync(); 
                var delayTask = Task.Delay(remainingMs); 

                var completedTask = await Task.WhenAny(receiveTask, delayTask); 

                if (completedTask == receiveTask) 
                {
                    try 
                    {
                        var receiveResult = await receiveTask; 
                        string response = Encoding.UTF8.GetString(receiveResult.Buffer).Trim(); 
                        string remoteIp = receiveResult.RemoteEndPoint.Address.ToString(); 

                        if (response == "PONG" && !discoveredIps.Contains(remoteIp)) 
                        {
                            discoveredIps.Add(remoteIp); 
                            GD.Print($"CueLightManager:DiscoverCueLightsAsync - Discovered cue light at {remoteIp}"); 
                            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Discovered cue light at {remoteIp}", 0); 
                        } 
                    } 
                    catch (Exception ex) 
                    { 
                        GD.PrintErr($"CueLightManager:DiscoverCueLightsAsync - Error processing response: {ex.Message}"); 
                        _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Error processing discovery response: {ex.Message}", 2); 
                    } 
                } 
                else 
                { 
                    // Per-receive timeout reached; overall loop will check endTime and exit if needed 
                    GD.Print($"CueLightManager:DiscoverCueLightsAsync - Receive timeout, continuing discovery"); 
                    break; // Optional: Break early if no data, but continue to allow multiple receives 
                } 
            } 
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLightManager:DiscoverCueLightsAsync - Error during discovery: {ex.Message}"); 
            _globalSignals.EmitSignal(nameof(GlobalSignals.Log), $"Error during cue light discovery: {ex.Message}", 2);
        }
        finally
        {
            udpClient?.Close();
            udpClient?.Dispose();
        }

        return discoveredIps;
    }
     
    

    public Dictionary GetData()
    {
        var data = new Dictionary();
        foreach (var kvp in _cueLights)
            data[kvp.Key] = kvp.Value.GetData();
        return data;
    }

    /// <summary>
    /// Removes all cue lights and resets the id allocator (New Session).
    /// </summary>
    public void Reset()
    {
        _cueLights.Clear();
        _nextId = 0;
        GD.Print("CueLightManager:Reset - All cue lights cleared.");
    }

    /// <summary>
    /// Replaces all cue lights from serialized show data (session load / history restore).
    /// </summary>
    /// <param name="data">Dictionary of cue-light records keyed arbitrarily.</param>
    public void LoadData(Dictionary data)
    {
        // Replace, do not merge — required for session load and document undo/redo restore.
        _cueLights.Clear();
        _nextId = 0;

        if (data == null) return;

        foreach (var value in data.Values)
        {
            var cueLightDict = value.AsGodotDictionary();
            if (!cueLightDict.ContainsKey("Id"))
            {
                GD.PrintErr("CueLightManager:LoadData - Missing 'Id' key in data.");
                continue;
            }
            if ((int)cueLightDict["Id"] >= _nextId) _nextId = (int)cueLightDict["Id"] + 1;
            var cueLight = new CueLight(cueLightDict);
            _cueLights[cueLight.Id] = cueLight;
        }
    }
}