using System.Buffers;
using System.Net.Sockets;
using Android.Util;
using BaluMediaServer.Models;

namespace BaluMediaServer.RTSP.Transport;

/// <summary>
/// Manages RTP/RTCP transport for both UDP and TCP interleaved modes.
/// Handles port allocation, data sending, and connection health monitoring.
/// </summary>
public class TransportManager : ITransportManager
{
    private readonly object _portLock = new();
    private readonly HashSet<int> _usedPorts = new();
    private int _nextRtpPort = 5000;
    private readonly CancellationTokenSource _cts;

    /// <summary>
    /// Creates a new TransportManager.
    /// </summary>
    /// <param name="cts">The cancellation token source.</param>
    public TransportManager(CancellationTokenSource cts)
    {
        _cts = cts;
    }

    /// <inheritdoc/>
    public CancellationToken CancellationToken => _cts.Token;

    /// <inheritdoc/>
    /// <remarks>
    /// Uses a per-client SendLock (3s timeout) to serialize sends. Errors are tracked
    /// via ConsecutiveSendErrors with graduated thresholds: the client is only marked
    /// for cleanup after 10 consecutive failures (TCP) or 5 failures / unreachable (UDP),
    /// preventing premature disconnection from transient network issues.
    /// </remarks>
    public async Task<bool> SendDataAsync(Client client, byte[] data)
    {
        // Serialize all sends per-client to prevent TCP interleaved framing corruption
        // and UDP out-of-order delivery from concurrent async sends.
        // Use a timeout instead of the server CTS to avoid silent failures
        // if the CTS is cancelled during server lifecycle events.
        if (!await client.SendLock.WaitAsync(3000).ConfigureAwait(false))
        {
            Log.Warn("[TransportManager]", $"SendLock timeout (3s) for client {client.Id} - skipping packet");
            return false;
        }
        try
        {
            if (client.Transport == TransportMode.UDP)
            {
                return await SendUdpDataAsync(client, data, false).ConfigureAwait(false);
            }
            else if (client.Transport == TransportMode.TCPInterleaved)
            {
                return await SendInterleavedDataAsync(client.Socket, client.RtpChannel, data, client).ConfigureAwait(false);
            }
            return false;
        }
        finally
        {
            client.SendLock.Release();
        }
    }

    /// <summary>
    /// Sends multiple RTP packets in a single batch, acquiring the SendLock once.
    /// For TCP: concatenates all interleaved-framed packets into one buffer for a single socket.SendAsync.
    /// For UDP: sends all packets in a tight loop within the lock.
    /// </summary>
    public async Task<bool> SendBatchAsync(Client client, List<byte[]> packets)
    {
        if (packets.Count == 0) return true;

        // Single packet — skip batching overhead
        if (packets.Count == 1)
            return await SendDataAsync(client, packets[0]).ConfigureAwait(false);

        if (!await client.SendLock.WaitAsync(3000).ConfigureAwait(false))
        {
            Log.Warn("[TransportManager]", $"SendLock timeout (3s) for client {client.Id} - skipping batch");
            return false;
        }
        try
        {
            if (client.Transport == TransportMode.TCPInterleaved)
            {
                return await SendInterleavedBatchAsync(client, packets).ConfigureAwait(false);
            }
            else if (client.Transport == TransportMode.UDP)
            {
                bool allOk = true;
                foreach (var pkt in packets)
                {
                    if (!await SendUdpDataAsync(client, pkt, false).ConfigureAwait(false))
                        allOk = false;
                }
                return allOk;
            }
            return false;
        }
        finally
        {
            client.SendLock.Release();
        }
    }

    /// <summary>
    /// Concatenates multiple RTP packets into a single TCP interleaved buffer and sends with one syscall.
    /// </summary>
    private async Task<bool> SendInterleavedBatchAsync(Client client, List<byte[]> packets)
    {
        // Calculate total frame size: each packet needs 4-byte interleaved header + data
        int totalSize = 0;
        for (int i = 0; i < packets.Count; i++)
            totalSize += 4 + packets[i].Length;

        var frame = ArrayPool<byte>.Shared.Rent(totalSize);
        try
        {
            int offset = 0;
            for (int i = 0; i < packets.Count; i++)
            {
                var rtpPacket = packets[i];
                frame[offset] = 0x24; // $ magic byte
                frame[offset + 1] = client.RtpChannel;
                frame[offset + 2] = (byte)(rtpPacket.Length >> 8);
                frame[offset + 3] = (byte)(rtpPacket.Length & 0xFF);
                Buffer.BlockCopy(rtpPacket, 0, frame, offset + 4, rtpPacket.Length);
                offset += 4 + rtpPacket.Length;
            }

            try
            {
                var socket = client.Socket;
                if (socket?.Connected ?? false)
                {
                    await socket.SendAsync(frame.AsMemory(0, totalSize), SocketFlags.None).ConfigureAwait(false);
                    lock (client)
                    {
                        client.LastActivityTick = Environment.TickCount64;
                        client.ConsecutiveSendErrors = 0;
                    }
                    return true;
                }

                lock (client)
                {
                    client.ConsecutiveSendErrors++;
                    if (client.ConsecutiveSendErrors >= 10)
                        client.IsPlaying = false;
                }
                return false;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException)
            {
                lock (client)
                {
                    client.ConsecutiveSendErrors++;
                    if (client.ConsecutiveSendErrors >= 10)
                    {
                        client.IsPlaying = false;
                        Log.Error("[TransportManager]", $"Client {client.Id} marked for cleanup after batch send error");
                    }
                }
                return false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SendInterleavedDataAsync(Socket socket, byte channel, byte[] rtpPacket, Client? client = null)
    {
        // Use ArrayPool to avoid per-packet heap allocation for the 4-byte RTSP framing header.
        // The pooled buffer is returned after the send completes (safe because SendAsync is awaited).
        int frameSize = 4 + rtpPacket.Length;
        var frame = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            frame[0] = 0x24; // $ magic byte
            frame[1] = channel;
            frame[2] = (byte)(rtpPacket.Length >> 8);
            frame[3] = (byte)(rtpPacket.Length & 0xFF);
            Buffer.BlockCopy(rtpPacket, 0, frame, 4, rtpPacket.Length);

            try
            {
                if (socket?.Connected ?? false)
                {
                    // Send with socket-level timeout (set at connection time) instead of
                    // allocating a new CancellationTokenSource per packet.
                    await socket.SendAsync(frame.AsMemory(0, frameSize), SocketFlags.None).ConfigureAwait(false);

                    // Update activity time on successful send
                    if (client != null)
                    {
                        lock (client)
                        {
                            client.LastActivityTick = Environment.TickCount64;
                            client.ConsecutiveSendErrors = 0;
                        }
                    }
                    return true;
                }

                // Socket not connected — track the error so the streaming loop can detect it
                if (client != null)
                {
                    lock (client)
                    {
                        client.ConsecutiveSendErrors++;
                        if (client.ConsecutiveSendErrors >= 10)
                        {
                            client.IsPlaying = false;
                        }
                    }
                }
                return false;
            }
            catch (OperationCanceledException)
            {
                if (client != null)
                {
                    lock (client)
                    {
                        client.ConsecutiveSendErrors++;
                        Log.Warn("[TransportManager]", $"TCP send timeout (3s) - client {client.Id} error count: {client.ConsecutiveSendErrors}");
                        if (client.ConsecutiveSendErrors >= 10)
                        {
                            client.IsPlaying = false;
                            Log.Error("[TransportManager]", $"Client {client.Id} marked for cleanup - too many timeouts");
                        }
                    }
                }
                else
                {
                    Log.Warn("[TransportManager]", "TCP send timeout - client may be disconnected");
                }
                return false;
            }
            catch (SocketException ex)
            {
                if (client != null)
                {
                    lock (client)
                    {
                        client.ConsecutiveSendErrors++;
                        Log.Error("[TransportManager]", $"TCP send socket error for client {client.Id} (error count: {client.ConsecutiveSendErrors}): {ex.SocketErrorCode} - {ex.Message}");
                        if (client.ConsecutiveSendErrors >= 10)
                        {
                            client.IsPlaying = false;
                            Log.Error("[TransportManager]", $"Client {client.Id} marked for cleanup - too many socket errors");
                        }
                    }
                }
                else
                {
                    Log.Error("[TransportManager]", $"TCP send socket error: {ex.SocketErrorCode} - {ex.Message}");
                }
                return false;
            }
            catch (Exception ex)
            {
                if (client != null)
                {
                    lock (client)
                    {
                        client.ConsecutiveSendErrors++;
                        Log.Error("[TransportManager]", $"TCP send error for client {client.Id} (error count: {client.ConsecutiveSendErrors}): {ex.Message}");
                        if (client.ConsecutiveSendErrors >= 10)
                        {
                            client.IsPlaying = false;
                            Log.Error("[TransportManager]", $"Client {client.Id} marked for cleanup after {client.ConsecutiveSendErrors} errors");
                        }
                    }
                }
                else
                {
                    Log.Error("[TransportManager]", $"TCP send error: {ex.Message}");
                }
                return false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> SendUdpDataAsync(Client client, byte[] rtpPacket, bool isRtcp)
    {
        try
        {
            var endpoint = isRtcp ? client.RtcpEndPoint : client.RtpEndPoint;
            if (client.UdpSocket != null && endpoint != null)
            {
                await client.UdpSocket.SendToAsync(rtpPacket, SocketFlags.None, endpoint).ConfigureAwait(false);
                lock (client)
                {
                    client.LastActivityTick = Environment.TickCount64;
                    client.ConsecutiveSendErrors = 0;
                }
                return true;
            }
            return false;
        }
        catch (SocketException ex)
        {
            lock (client)
            {
                client.ConsecutiveSendErrors++;
                Log.Error("[TransportManager]", $"UDP send error for client {client.Id} (error count: {client.ConsecutiveSendErrors}): {ex.SocketErrorCode} - {ex.Message}");

                // Mark client for cleanup if persistent errors
                if (ex.SocketErrorCode == SocketError.HostUnreachable ||
                    ex.SocketErrorCode == SocketError.NetworkUnreachable ||
                    client.ConsecutiveSendErrors >= 5)
                {
                    client.IsPlaying = false;
                    Log.Warn("[TransportManager]", $"Client {client.Id} marked for cleanup - unreachable or too many UDP errors");
                }
            }
            return false;
        }
    }

    /// <inheritdoc/>
    public int GetAvailablePort()
    {
        lock (_portLock)
        {
            // Find next available even port (RTP uses even, RTCP uses odd)
            while (_usedPorts.Contains(_nextRtpPort) || _usedPorts.Contains(_nextRtpPort + 1))
            {
                _nextRtpPort += 2;

                // Wrap around if we reach the upper limit
                if (_nextRtpPort > 65000)
                {
                    _nextRtpPort = 5000;
                }
            }

            // Reserve both RTP and RTCP ports
            _usedPorts.Add(_nextRtpPort);
            _usedPorts.Add(_nextRtpPort + 1);

            var port = _nextRtpPort;
            _nextRtpPort += 2;

            return port;
        }
    }

    /// <inheritdoc/>
    public void ReleaseClientPorts(Client client)
    {
        lock (_portLock)
        {
            if (client.RtpEndPoint != null)
            {
                var rtpPort = client.RtpEndPoint.Port;
                if (rtpPort > 0)
                {
                    _usedPorts.Remove(rtpPort);
                    _usedPorts.Remove(rtpPort + 1);
                }
            }
        }
    }

    /// <inheritdoc/>
    public Dictionary<string, string> ParseTransport(string transport)
    {
        var result = new Dictionary<string, string>();
        var parts = transport.Split(';');

        foreach (var part in parts)
        {
            var keyValue = part.Split('=');
            if (keyValue.Length == 2)
            {
                result[keyValue[0].Trim()] = keyValue[1].Trim();
            }
            else
            {
                result[part.Trim()] = "";
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public bool IsSocketConnected(Socket? socket)
    {
        if (socket == null)
            return false;

        try
        {
            // First check the Connected property
            if (!socket.Connected)
                return false;

            // Poll with SelectRead: returns true if connection is closed, reset, terminated, or data is available
            // If Poll returns true but Available is 0, the connection was closed
            // Increased from 1ms to 10ms timeout to be more forgiving for slower but stable connections
            bool pollResult = socket.Poll(10000, SelectMode.SelectRead);
            bool hasData = socket.Available > 0;

            // If poll says readable but no data available, the socket was closed
            if (pollResult && !hasData)
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }
}
