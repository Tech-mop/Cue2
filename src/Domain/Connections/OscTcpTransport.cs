// SPDX-FileCopyrightText: 2025-2026 Samuel Moxham
// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Godot;
using Rug.Osc;

namespace Cue2.Domain.Connections;

/// <summary>
/// TCP OSC helpers using Rug.Osc binary stream framing (length-prefixed packets).
/// Compatible with common OSC-over-TCP implementations (OscWriter/OscReader Binary format).
/// </summary>
public static class OscTcpTransport
{
    /// <summary>Default connect timeout for outbound TCP OSC.</summary>
    public const int ConnectTimeoutMs = 3000;

    /// <summary>
    /// Creates a connected <see cref="TcpClient"/> to the remote endpoint.
    /// </summary>
    public static TcpClient Connect(IPAddress address, int port, int timeoutMs = ConnectTimeoutMs)
    {
        var client = new TcpClient();
        var ar = client.BeginConnect(address, port, null, null);
        if (!ar.AsyncWaitHandle.WaitOne(timeoutMs))
        {
            try { client.Close(); } catch { /* ignore */ }
            throw new TimeoutException($"TCP OSC connect timed out ({address}:{port})");
        }
        client.EndConnect(ar);
        client.NoDelay = true;
        return client;
    }

    /// <summary>
    /// Writes one OSC packet on a TCP stream using binary framing.
    /// </summary>
    public static void WritePacket(Stream stream, OscPacket packet)
    {
        if (stream == null || packet == null) return;
        using var writer = new OscWriter(stream, OscPacketFormat.Binary);
        // OscWriter may dispose the stream depending on version — keep stream open:
        // Rug.Osc OscWriter.Dispose closes the stream. Write without disposing stream:
        WritePacketRaw(stream, packet);
    }

    /// <summary>
    /// Writes a length-prefixed OSC packet without disposing the stream.
    /// Format matches Rug.Osc OscPacketFormat.Binary (4-byte big-endian size + body).
    /// </summary>
    public static void WritePacketRaw(Stream stream, OscPacket packet)
    {
        if (stream == null || packet == null) return;
        byte[] body = packet.ToByteArray();
        if (body == null || body.Length == 0) return;

        // Prefer Rug.Osc writer into a MemoryStream then copy, so framing matches library.
        using var ms = new MemoryStream();
        using (var writer = new OscWriter(ms, OscPacketFormat.Binary))
        {
            writer.Write(packet);
        }
        byte[] framed = ms.ToArray();
        stream.Write(framed, 0, framed.Length);
        stream.Flush();
    }

    /// <summary>
    /// Reads one OSC packet from a TCP stream using binary framing.
    /// Returns null on graceful EOF.
    /// </summary>
    public static OscPacket ReadPacket(Stream stream, IPEndPoint origin)
    {
        if (stream == null) return null;
        // Rug.Osc OscReader blocks until a full packet is available.
        // Construct without disposing the underlying stream on reader dispose if possible.
        var reader = new OscReader(stream, OscPacketFormat.Binary);
        try
        {
            if (reader.EndOfStream) return null;
            return reader.Read();
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            // Do not dispose reader if it closes stream — leave stream open for next packet.
            // Rug.Osc OscReader.Dispose typically closes BaseStream; avoid Dispose and only read.
        }
    }

    /// <summary>
    /// Reads packets from <paramref name="client"/> until disconnect or cancel.
    /// Invokes <paramref name="onPacket"/> for each message/bundle (may be background thread).
    /// </summary>
    public static void ReceiveLoop(
        TcpClient client,
        Func<bool> isRunning,
        Action<OscPacket, IPEndPoint> onPacket)
    {
        if (client == null || onPacket == null) return;

        IPEndPoint origin = null;
        try
        {
            if (client.Client?.RemoteEndPoint is IPEndPoint ep)
                origin = ep;
        }
        catch { /* ignore */ }

        NetworkStream stream;
        try { stream = client.GetStream(); }
        catch { return; }

        // Manual binary frame read so we don't dispose the stream via OscReader.
        var sizeBuf = new byte[4];
        while (isRunning())
        {
            try
            {
                if (!client.Connected) break;
                if (!ReadExact(stream, sizeBuf, 0, 4, isRunning))
                    break;

                // Big-endian length
                int size = (sizeBuf[0] << 24) | (sizeBuf[1] << 16) | (sizeBuf[2] << 8) | sizeBuf[3];
                if (size <= 0 || size > 1024 * 1024)
                {
                    GD.PrintErr($"OscTcpTransport:ReceiveLoop - invalid frame size {size}");
                    break;
                }

                var body = new byte[size];
                if (!ReadExact(stream, body, 0, size, isRunning))
                    break;

                OscPacket packet = origin != null
                    ? OscPacket.Read(body, size, origin)
                    : OscPacket.Read(body, size);

                if (packet != null && packet.Error == OscPacketError.None)
                    onPacket(packet, origin);
            }
            catch (Exception ex)
            {
                if (isRunning())
                    GD.PrintErr($"OscTcpTransport:ReceiveLoop - {ex.Message}");
                break;
            }
        }

        try { client.Close(); } catch { /* ignore */ }
    }

    /// <summary>
    /// Writes an OSC packet with 4-byte big-endian size prefix (matches Rug.Osc Binary framing).
    /// </summary>
    public static void SendPacket(TcpClient client, OscPacket packet)
    {
        if (client == null || packet == null || !client.Connected)
            throw new InvalidOperationException("TCP OSC client not connected");

        byte[] body = packet.ToByteArray();
        if (body == null) return;

        int size = body.Length;
        var header = new byte[4];
        header[0] = (byte)((size >> 24) & 0xFF);
        header[1] = (byte)((size >> 16) & 0xFF);
        header[2] = (byte)((size >> 8) & 0xFF);
        header[3] = (byte)(size & 0xFF);

        var stream = client.GetStream();
        stream.Write(header, 0, 4);
        stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int offset, int count, Func<bool> isRunning)
    {
        int read = 0;
        while (read < count)
        {
            if (!isRunning()) return false;
            int n;
            try
            {
                n = stream.Read(buffer, offset + read, count - read);
            }
            catch
            {
                return false;
            }
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }
}
