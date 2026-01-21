using System.Net;
using System.Net.Sockets;
using Android.Util;
using BaluMediaServer.Models;

namespace BaluMediaServer.RTSP.Transport;

/// <summary>
/// Manages RTCP sender reports and receiver feedback.
/// Handles bitrate adaptation based on network conditions.
/// </summary>
public class RtcpManager : IRtcpManager
{
    private readonly ITransportManager _transportManager;
    private readonly CancellationToken _cancellationToken;

    /// <summary>
    /// Event raised when a client should be cleaned up.
    /// </summary>
    public event EventHandler<Client>? ClientCleanupRequired;

    /// <summary>
    /// Event raised when bitrate adjustment is needed.
    /// </summary>
    public event EventHandler<(Client client, int newBitrate)>? BitrateAdjustmentRequired;

    /// <summary>
    /// Creates a new RtcpManager.
    /// </summary>
    /// <param name="transportManager">The transport manager.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public RtcpManager(ITransportManager transportManager, CancellationToken cancellationToken)
    {
        _transportManager = transportManager;
        _cancellationToken = cancellationToken;
    }

    /// <inheritdoc/>
    public async Task SendSenderReportAsync(Client client)
    {
        try
        {
            // RTCP SR packet structure (28 bytes minimum):
            // - Header (8 bytes): V=2, P=0, RC=0, PT=200 (SR), Length
            // - SSRC (4 bytes)
            // - NTP timestamp (8 bytes)
            // - RTP timestamp (4 bytes)
            // - Sender's packet count (4 bytes)
            // - Sender's octet count (4 bytes)

            var srPacket = new byte[28];

            // Header
            srPacket[0] = 0x80; // V=2, P=0, RC=0
            srPacket[1] = 200;  // PT=200 (Sender Report)

            // Length in 32-bit words minus one (28/4 - 1 = 6)
            srPacket[2] = 0;
            srPacket[3] = 6;

            // SSRC
            lock (client)
            {
                srPacket[4] = (byte)(client.SsrcId >> 24);
                srPacket[5] = (byte)(client.SsrcId >> 16);
                srPacket[6] = (byte)(client.SsrcId >> 8);
                srPacket[7] = (byte)(client.SsrcId & 0xFF);
            }

            // NTP timestamp (64-bit): seconds since 1900 + fractional seconds
            var now = DateTime.UtcNow;
            var ntpEpoch = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var ntpSeconds = (ulong)(now - ntpEpoch).TotalSeconds;
            var ntpFraction = (ulong)((now - ntpEpoch).TotalSeconds % 1 * uint.MaxValue);

            srPacket[8] = (byte)(ntpSeconds >> 24);
            srPacket[9] = (byte)(ntpSeconds >> 16);
            srPacket[10] = (byte)(ntpSeconds >> 8);
            srPacket[11] = (byte)(ntpSeconds & 0xFF);
            srPacket[12] = (byte)(ntpFraction >> 24);
            srPacket[13] = (byte)(ntpFraction >> 16);
            srPacket[14] = (byte)(ntpFraction >> 8);
            srPacket[15] = (byte)(ntpFraction & 0xFF);

            // RTP timestamp (last sent)
            uint rtpTimestamp;
            uint packetCount;
            uint octetCount;

            lock (client)
            {
                rtpTimestamp = client.LastRtpTimestampSent;
                packetCount = client.PacketCount;
                octetCount = client.OctetCount;
            }

            srPacket[16] = (byte)(rtpTimestamp >> 24);
            srPacket[17] = (byte)(rtpTimestamp >> 16);
            srPacket[18] = (byte)(rtpTimestamp >> 8);
            srPacket[19] = (byte)(rtpTimestamp & 0xFF);

            // Sender's packet count
            srPacket[20] = (byte)(packetCount >> 24);
            srPacket[21] = (byte)(packetCount >> 16);
            srPacket[22] = (byte)(packetCount >> 8);
            srPacket[23] = (byte)(packetCount & 0xFF);

            // Sender's octet count
            srPacket[24] = (byte)(octetCount >> 24);
            srPacket[25] = (byte)(octetCount >> 16);
            srPacket[26] = (byte)(octetCount >> 8);
            srPacket[27] = (byte)(octetCount & 0xFF);

            // Send via appropriate transport
            if (client.Transport == TransportMode.UDP)
            {
                await _transportManager.SendUdpDataAsync(client, srPacket, true).ConfigureAwait(false);
            }
            else if (client.Transport == TransportMode.TCPInterleaved)
            {
                await _transportManager.SendInterleavedDataAsync(client.Socket, client.RtcpChannel, srPacket).ConfigureAwait(false);
            }

            lock (client)
            {
                client.LastSenderReportTime = DateTime.UtcNow;
            }

            Log.Debug("[RtcpManager]", $"Sent RTCP SR to client {client.Id}");
        }
        catch (Exception ex)
        {
            Log.Error("[RtcpManager]", $"Error sending RTCP SR: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async Task ListenRtcpPortAsync(Client client)
    {
        byte[] buffer = new byte[1024];

        while (!client.IsPlaying && !_cancellationToken.IsCancellationRequested)
            await Task.Delay(100, _cancellationToken).ConfigureAwait(false);

        while (!_cancellationToken.IsCancellationRequested && client.RtcpSocket != null)
        {
            try
            {
                var timeout = Task.Delay(TimeSpan.FromSeconds(60), _cancellationToken); // 1 minute timeout
                EndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
                var task = client.RtcpSocket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEndpoint, _cancellationToken).AsTask();
                var result = await Task.WhenAny(task, timeout).ConfigureAwait(false);

                if (result == timeout)
                {
                    ClientCleanupRequired?.Invoke(this, client);
                    break;
                }
                else
                {
                    var request = task.Result;
                    if (request.ReceivedBytes > 0 && buffer.Length >= 2)
                    {
                        if (buffer[1] == 203) // BYE
                        {
                            ClientCleanupRequired?.Invoke(this, client);
                            break;
                        }
                        else if (buffer[1] == 201) // RR (Receiver Report)
                        {
                            HandleRtcpReport(client, buffer);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("[RtcpManager]", $"Error listening RTCP: {ex.Message}");
                break;
            }
        }
    }

    /// <inheritdoc/>
    public void HandleRtcpReport(Client client, byte[] rtcpData)
    {
        // Parse RTCP RR (Receiver Report)
        if (rtcpData.Length >= 8 && (rtcpData[1] == 201)) // RR packet type
        {
            // Extract packet loss and jitter
            if (rtcpData.Length >= 24)
            {
                var fractionLost = rtcpData[12];
                var cumulativeLost = (uint)((rtcpData[13] << 16) | (rtcpData[14] << 8) | rtcpData[15]);
                var jitter = (uint)((rtcpData[20] << 24) | (rtcpData[21] << 16) | (rtcpData[22] << 8) | rtcpData[23]);

                // Adjust bitrate based on network conditions
                AdjustBitrate(client, fractionLost, jitter);
            }
        }
    }

    private void AdjustBitrate(Client client, byte fractionLost, uint jitter)
    {
        int MIN_BITRATE = client.VideoProfile.MinBitrate;  // 500 kbps
        int MAX_BITRATE = client.VideoProfile.MaxBitrate; // 4 Mbps

        lock (client)
        {
            var previousBitrate = client.CurrentBitrate;
            if (fractionLost > 10) // More than 4% loss
            {
                client.CurrentBitrate = Math.Max(MIN_BITRATE, (int)(client.CurrentBitrate * 0.6));
                client.VideoProfile.Quality = Math.Max(10, (int)(client.VideoProfile.Quality * 0.6));
            }
            else if (fractionLost > 5 && fractionLost <= 10)
            {
                client.CurrentBitrate = Math.Max(MIN_BITRATE, (int)(client.CurrentBitrate * 0.9));
                client.VideoProfile.Quality = Math.Max(10, (int)(client.VideoProfile.Quality * 0.9));
            }
            else if (fractionLost < 2 && jitter < 100) // Good conditions
            {
                var now = DateTime.UtcNow;
                if (now - client.LastCodecUpdate > TimeSpan.FromSeconds(10))
                {
                    client.CurrentBitrate = Math.Min(MAX_BITRATE, (int)(client.CurrentBitrate * 1.1));
                    client.VideoProfile.Quality = Math.Min(100, (int)(client.VideoProfile.Quality * 0.6));
                }
            }

            if (client.CurrentBitrate != previousBitrate && client.Codec == CodecType.H264)
            {
                client.LastCodecUpdate = DateTime.UtcNow;
                BitrateAdjustmentRequired?.Invoke(this, (client, client.CurrentBitrate));
            }
        }
    }
}
