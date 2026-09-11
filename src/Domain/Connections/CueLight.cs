// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

#nullable enable
using Godot;
using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Cue2.Services;

namespace Cue2.Domain.Connections;

/// <summary>
/// Manages a single ESP32-based cue light with persistent TCP connection.
/// Supports Ethernet/WiFi config, secure credential sending, and LED commands.
/// </summary>
public partial class CueLight : GodotObject, IDisposable
{
    private UdpClient? _udpClient;
    private bool _disposed = false;
    private readonly int _connectionTimeoutMs = 1000;

    public int Id { get; private set; }
    public string Name { get; set; }
    public string IpAddress { get; private set; }
    public int Port { get; private set; }
    public bool CueLightIsConnected { get; private set; }
    public bool IsEnabled { get; set; } = true;
    
    [Signal] public delegate void ConnectionChangedEventHandler(bool connection);
    
    
    public CueLight(int id)
    {
        Id = id;
        Name = $"CueLight_{id}";
        IpAddress = "192.168.1.100"; // Default for testing
        Port = 80;
        CueLightIsConnected = false;
        IsEnabled = true;
    }
    
    /// <summary>
    /// Deserializes cue light data from file loading.
    /// </summary>
    public CueLight(Godot.Collections.Dictionary data)
    {
        Id = data.ContainsKey("Id") ? data["Id"].AsInt32() : 0;
        Name = data.ContainsKey("Name") ? data["Name"].ToString() : $"CueLight_{Id}";
        IpAddress = data.ContainsKey("IpAddress") ? data["IpAddress"].ToString() : "192.168.1.100";
        Port = data.ContainsKey("Port") ? data["Port"].AsInt32() : 80;
        CueLightIsConnected = false;
        IsEnabled = data.ContainsKey("IsEnabled") ? data["IsEnabled"].AsBool() : true;
    }
    
    /// <summary>
    /// Sends a PING command and awaits acknowledgment.
    /// </summary>
    public async Task<bool> PingAsync()
    {
        if (_disposed) return false;
        try
        {
            if (_udpClient == null)
            {
                _udpClient = new UdpClient();
                _udpClient.Client.ReceiveTimeout = _connectionTimeoutMs;
                if (CueLightIsConnected == true)
                {
                    CueLightIsConnected = false;
                    EmitSignal(SignalName.ConnectionChanged, false);
                }
            }

            string pingCommand = "PING";
            byte[] sendBytes = Encoding.UTF8.GetBytes(pingCommand);
            await _udpClient.SendAsync(sendBytes, sendBytes.Length, IpAddress, Port);
            GD.Print($"CueLight:PingAsync - Sent {pingCommand} to {Name} at {IpAddress}:{Port}");

            var receiveTask = _udpClient.ReceiveAsync();
            if (await Task.WhenAny(receiveTask, Task.Delay(_connectionTimeoutMs)) == receiveTask)
            {
                var result = await receiveTask;
                string response = Encoding.UTF8.GetString(result.Buffer).Trim();
                if (response == "PONG")
                {
                    if (CueLightIsConnected == false)
                    {
                        CueLightIsConnected = true;
                        EmitSignal(SignalName.ConnectionChanged, true);
                    }
                    GD.Print($"CueLight:PingAsync - Received PONG:{Id} from {Name}");
                    return true;
                }
                else
                {
                    if (CueLightIsConnected == true)
                    {
                        CueLightIsConnected = false;
                        EmitSignal(SignalName.ConnectionChanged, false);
                    }
                    GD.PrintErr($"CueLight:PingAsync - Invalid response from {Name}: {response}");
                    return false;
                }
            }
            else
            {
                GD.PrintErr($"CueLight:PingAsync - Timeout pinging {Name}");
                _udpClient.Close();
                _udpClient = null;
                CueLightIsConnected = false;
                EmitSignal(SignalName.ConnectionChanged, false);
                return false;
            }
        }
        catch (Exception ex)
        {
            if (CueLightIsConnected == true)
            {
                CueLightIsConnected = false;
                EmitSignal(SignalName.ConnectionChanged, false);
            }
            GD.PrintErr($"CueLight:PingAsync - Error pinging {Name}: {ex.Message}");
            _udpClient?.Close();
            _udpClient = null;
            return false;
        }
    }
    
    /// <summary>
    /// Sends a command and awaits acknowledgment.
    /// </summary>
    public async Task<bool> SendCommandAsync(string command)
    {
        if (_disposed || !IsEnabled)
        {
            if (!IsEnabled)
            {
                GD.Print($"CueLight:SendCommandAsync - Skipped command '{command}' for {Name} (disabled)");
                return false;
            }
            return false;
        }
        try
        {
            if (_udpClient == null)
            {
                _udpClient = new UdpClient();
                _udpClient.Client.ReceiveTimeout = _connectionTimeoutMs;
                CueLightIsConnected = false;
            }

            byte[] sendBytes = Encoding.UTF8.GetBytes(command);
            await _udpClient.SendAsync(sendBytes, sendBytes.Length, IpAddress, Port);
            GD.Print($"CueLight:SendCommandAsync - Sent {command} to {Name} at {IpAddress}:{Port}");

            var receiveTask = _udpClient.ReceiveAsync(); 
            if (await Task.WhenAny(receiveTask, Task.Delay(_connectionTimeoutMs)) == receiveTask) 
            { 
                var result = await receiveTask; 
                string response = Encoding.UTF8.GetString(result.Buffer).Trim(); 
                if (response == "OK") 
                { 
                    GD.Print($"CueLight:SendCommandAsync - Received OK from {Name}"); 
                    CueLightIsConnected = true;
                    EmitSignal(SignalName.ConnectionChanged, true);
                    return true; 
                } 
                else 
                { 
                    GD.PrintErr($"CueLight:SendCommandAsync - Invalid response from {Name}: {response}"); 
                    return false; 
                } 
            } 
            else 
            { 
                GD.PrintErr($"CueLight:SendCommandAsync - Timeout sending command to {Name}"); 
                _udpClient.Close(); 
                _udpClient = null; 
                CueLightIsConnected = false; 
                EmitSignal(SignalName.ConnectionChanged, false);
                return false; 
            } 
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLight:SendCommandAsync - Error sending command to {Name}: {ex.Message}"); 
            _udpClient?.Close(); 
            _udpClient = null; 
            CueLightIsConnected = false; 
            EmitSignal(SignalName.ConnectionChanged, false);
            return false; 
        }
    }
    
    /// <summary>
    /// Sets the cue light IP address.
    /// </summary>
    /// <param name="newIp">IPv4 address string.</param>
    public void SetIpAddress(string newIp)
    {
        if (IpAddress == newIp) return;
        IpAddress = newIp;
    }
    
    /// <summary>
    /// Disposes UDP resources for this cue light.
    /// </summary>
    public new void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _udpClient?.Close();
            _udpClient?.Dispose();
            _udpClient = null;
            CueLightIsConnected = false;
            GD.Print($"CueLight:Dispose - Disposed CueLight {Id} ({Name})");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"CueLight:Dispose - Error disposing CueLight {Id} ({Name}): {ex.Message}");
        }
    }
    

    /// <summary>
    /// Serializes cue light data for saving.
    /// </summary>
    public Godot.Collections.Dictionary GetData()
    {
        return new Godot.Collections.Dictionary
        {
            { "Id", Id },
            { "Name", Name },
            { "IpAddress", IpAddress },
            { "Port", Port },
            {"IsEnabled", IsEnabled },
        };
    }
    
    
    // ~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-
    // Commands
    

    /// <summary>
    /// Configures colors and brightness for the CueLight.
    /// </summary>
    /// <param name="idleColor">Idle color (RGB).</param>
    /// <param name="goColor">Go color (RGB).</param>
    /// <param name="standbyColor">Standby color (RGB).</param>
    /// <param name="countInColor">Count-in color (RGB).</param>
    /// <param name="brightness">Brightness (0-255).</param>
    public async Task<bool> ConfigureAsync(Color idleColor, Color goColor, Color standbyColor, Color countInColor, byte brightness)
    {
        string command = $"CONFIG:{Name}:" + 
                         $"{(byte)(idleColor.R * 255)},{(byte)(idleColor.G * 255)},{(byte)(idleColor.B * 255)}:" + 
                         $"{(byte)(goColor.R * 255)},{(byte)(goColor.G * 255)},{(byte)(goColor.B * 255)}:" + 
                         $"{(byte)(standbyColor.R * 255)},{(byte)(standbyColor.G * 255)},{(byte)(standbyColor.B * 255)}:" + 
                         $"{(byte)(countInColor.R * 255)},{(byte)(countInColor.G * 255)},{(byte)(countInColor.B * 255)}:" + 
                         $"{brightness}";
        return await SendCommandAsync(command);
    }
    
    /// <summary>
    /// Triggers GO state with optional display text (max 5 chars).
    /// </summary>
    /// <param name="text">Optional TFT text (defaults to empty).</param>
    public async Task<bool> GoAsync(string text = "")
    {
        if (text.Length > 5) text = text.Substring(0, 5);
        return await SendCommandAsync($"GO:{text}");
    }

    /// <summary>
    /// Triggers STANDBY state with optional display text (max 5 chars).
    /// </summary>
    /// <param name="text">Optional TFT text (defaults to empty).</param>
    public async Task<bool> StandbyAsync(string text = "")
    {
        if (text.Length > 5) text = text.Substring(0, 5);
        return await SendCommandAsync($"STANDBY:{text}");
    }
    
    /// <summary>
    /// Cancels STANDBY state.
    /// </summary>
    public async Task<bool> CancelAsync() 
    {
        return await SendCommandAsync("CANCEL");
    }

    /// <summary>
    /// Triggers COUNTIN state with duration and optional display text.
    /// </summary>
    /// <param name="timeUntilGo">Duration in seconds.</param>
    /// <param name="text">Optional TFT text (defaults to empty).</param>
    public async Task<bool> CountInAsync(float timeUntilGo, string text = "")
    {
        if (timeUntilGo <= 0)
        {
            GD.PrintErr($"CueLight:CountInAsync - Invalid duration for {Name}: {timeUntilGo}");
            return false;
        }
        if (text.Length > 5) text = text.Substring(0, 5);
        return await SendCommandAsync($"COUNTIN:{timeUntilGo}:{text}");
    }

    /// <summary>
    /// Toggles identification mode on the cue light via UDP.
    /// </summary>
    public async Task<bool> IdentifyAsync(bool on)
    {
        return await SendCommandAsync($"IDENTIFY:{(on ? "START" : "STOP")}");
    }
    
    // ~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-~-
    // Network Configuration Commands 
    // THIS IS DRAFT CODE, NOT YET IMPLEMENTED WITH THE PROTOTYPE CUELIGHT ESP
    
    /// <summary>
    /// Sets the preferred network mode (WiFi or Ethernet).
    /// </summary>
    /// <param name="mode">The network mode: "WiFi" or "Ethernet".</param>
    public async Task<bool> SetNetworkModeAsync(string mode) 
    { 
        if (mode != "WiFi" && mode != "Ethernet") 
        { 
            GD.PrintErr($"CueLight:SetNetworkModeAsync - Invalid mode '{mode}' for {Name}"); 
            return false; 
        } 
        return await SendCommandAsync($"SET_MODE:{mode}"); 
    } 
    
    /// <summary>
    /// Sets the WiFi configuration.
    /// </summary>
    /// <param name="ssid">WiFi SSID.</param>
    /// <param name="password">WiFi password.</param>
    /// <param name="dhcp">True for DHCP, false for static IP.</param>
    /// <param name="ip">Static IP address (if dhcp=false).</param>
    /// <param name="subnet">Subnet mask (if dhcp=false).</param>
    /// <param name="gateway">Gateway (if dhcp=false).</param>
    public async Task<bool> SetWiFiConfigAsync(string ssid, string password, bool dhcp, string ip = "", string subnet = "", string gateway = "") 
    { 
        string command = $"SET_WIFI:{ssid}:{password}:{(dhcp ? "dhcp" : "static")}"; 
        if (!dhcp) 
        { 
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(subnet) || string.IsNullOrEmpty(gateway)) 
            { 
                GD.PrintErr($"CueLight:SetWiFiConfigAsync - Missing static IP details for {Name}"); 
                return false; 
            } 
            command += $":{ip}:{subnet}:{gateway}"; 
        } 
        return await SendCommandAsync(command); 
    } 
    
    /// <summary>
    /// Sets the Ethernet configuration.
    /// </summary>
    /// <param name="dhcp">True for DHCP, false for static IP.</param>
    /// <param name="ip">Static IP address (if dhcp=false).</param>
    /// <param name="subnet">Subnet mask (if dhcp=false).</param>
    /// <param name="gateway">Gateway (if dhcp=false).</param>
    public async Task<bool> SetEthernetConfigAsync(bool dhcp, string ip = "", string subnet = "", string gateway = "") 
    { 
        string command = $"SET_ETHERNET:{(dhcp ? "dhcp" : "static")}"; 
        if (!dhcp) 
        { 
            if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(subnet) || string.IsNullOrEmpty(gateway)) 
            { 
                GD.PrintErr($"CueLight:SetEthernetConfigAsync - Missing static IP details for {Name}"); 
                return false; 
            } 
            command += $":{ip}:{subnet}:{gateway}"; 
        } 
        return await SendCommandAsync(command); 
    } 
}