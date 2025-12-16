using System.Net;
using System.Net.Sockets;

namespace BaluMediaServer.Models;

/// <summary>
/// Represents a connected RTSP client with all associated streaming state.
/// Manages socket connections, RTP/RTCP state, and health tracking for a single client.
/// </summary>
public class Client : IDisposable
{
    /// <summary>
    /// Gets or sets the TCP socket for RTSP communication.
    /// </summary>
    public Socket Socket { get; set; } = default!;

    /// <summary>
    /// Gets or sets the unique identifier for this client.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp when the client connected.
    /// </summary>
    public DateTime ConnectedAt { get; set; }

    /// <summary>
    /// Gets or sets the RTSP session identifier.
    /// </summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the transport mode (UDP or TCP interleaved).
    /// </summary>
    public TransportMode Transport { get; set; }

    /// <summary>
    /// Gets or sets the RTP channel number for TCP interleaved mode.
    /// </summary>
    public byte RtpChannel { get; set; }

    /// <summary>
    /// Gets or sets the RTCP channel number for TCP interleaved mode.
    /// </summary>
    public byte RtcpChannel { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the client is currently receiving stream data.
    /// </summary>
    public bool IsPlaying { get; set; }

    /// <summary>
    /// Gets or sets the video width for this client's stream.
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    /// Gets or sets the video height for this client's stream.
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// Gets or sets the codec type requested by the client.
    /// </summary>
    public CodecType Codec { get; set; } = CodecType.H264;

    /// <summary>
    /// Gets or sets the timestamp of the last H.264 frame sent to this client.
    /// Used to prevent duplicate frame delivery.
    /// </summary>
    public long LastH264FrameTimestamp { get; set; }

    /// <summary>
    /// Gets or sets the UDP endpoint for RTP packets (UDP mode only).
    /// </summary>
    public IPEndPoint? RtpEndPoint { get; set; }

    /// <summary>
    /// Gets or sets the UDP endpoint for RTCP packets (UDP mode only).
    /// </summary>
    public IPEndPoint? RtcpEndPoint { get; set; }

    /// <summary>
    /// Gets or sets the UDP socket for RTP/RTCP communication (UDP mode only).
    /// </summary>
    public Socket? UdpSocket { get; set; }

    /// <summary>
    /// Gets or sets the RTP packet sequence number.
    /// Incremented for each packet sent.
    /// </summary>
    public ushort SequenceNumber { get; set; } = 0;

    /// <summary>
    /// Gets or sets the current RTP timestamp.
    /// </summary>
    public uint RtpTimestamp { get; set; } = 0;

    /// <summary>
    /// Gets or sets the Synchronization Source (SSRC) identifier for RTP.
    /// Randomly generated per client.
    /// </summary>
    public uint SsrcId { get; set; } = (uint)Random.Shared.Next();

    /// <summary>
    /// Gets or sets the timestamp of the last RTP packet sent.
    /// </summary>
    public DateTime LastRtpTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the timestamp of the last codec configuration update.
    /// </summary>
    public DateTime LastCodecUpdate { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the total number of frames sent to this client.
    /// </summary>
    public int FrameCount { get; set; } = 0;

    /// <summary>
    /// Gets or sets the current bitrate in bits per second.
    /// May be adjusted dynamically based on network conditions.
    /// </summary>
    public int CurrentBitrate { get; set; } = 2000000;

    /// <summary>
    /// Gets or sets the dedicated RTCP socket (UDP mode only).
    /// </summary>
    public Socket? RtcpSocket { get; set; }

    /// <summary>
    /// Gets or sets the camera identifier (0 for back, 1 for front).
    /// </summary>
    public int CameraId { get; set; } = 0;

    /// <summary>
    /// Gets or sets the video profile configuration for this client.
    /// </summary>
    public VideoProfile VideoProfile { get; set; } = new();

    /// <summary>
    /// Buffer for RTCP packets awaiting transmission.
    /// </summary>
    public List<byte[]> RtcpBuffer = new();

    /// <summary>
    /// Gets or sets the reusable RTP packet buffer (MTU size: 1500 bytes).
    /// </summary>
    public byte[] RtpBuffer { get; set; } = new byte[1500];

    /// <summary>
    /// Timestamp of the last RTCP buffer flush.
    /// </summary>
    public DateTime LastRtcpFlush = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the base encoder timestamp for RTP timestamp calculation.
    /// </summary>
    public ulong BaseEncoderTimestamp { get; set; } = 0;

    /// <summary>
    /// Gets or sets the base RTP timestamp for delta calculations.
    /// </summary>
    public uint BaseRtpTimestamp { get; set; } = 0;

    /// <summary>
    /// Lock object for RTCP buffer synchronization.
    /// </summary>
    public readonly object RtcpLock = new();

    /// <summary>
    /// Gets or sets the total number of RTP packets sent (for RTCP Sender Reports).
    /// </summary>
    public uint PacketCount { get; set; } = 0;

    /// <summary>
    /// Gets or sets the total number of payload bytes sent (for RTCP Sender Reports).
    /// </summary>
    public uint OctetCount { get; set; } = 0;

    /// <summary>
    /// Gets or sets the timestamp of the last RTCP Sender Report.
    /// </summary>
    public DateTime LastSenderReportTime { get; set; } = DateTime.MinValue;

    /// <summary>
    /// Gets or sets the last RTP timestamp included in a Sender Report.
    /// </summary>
    public uint LastRtpTimestampSent { get; set; } = 0;

    /// <summary>
    /// Gets or sets the timestamp of the last successful network activity.
    /// Used for connection health monitoring.
    /// </summary>
    public DateTime LastActivityTime { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the count of consecutive send errors.
    /// Used to detect broken connections.
    /// </summary>
    public int ConsecutiveSendErrors { get; set; } = 0;

    /// <summary>
    /// Releases all resources used by this client including sockets.
    /// </summary>
    public void Dispose()
    {
        this.Socket?.Close();
        this.Socket?.Dispose();
        this.UdpSocket?.Close();
        this.UdpSocket?.Dispose();
        this.RtcpSocket?.Close();
        this.RtcpSocket?.Dispose();
        this.RtcpSocket = null;
        this.UdpSocket = null;
    }
}

/// <summary>
/// Specifies the transport mode for RTP/RTCP communication.
/// </summary>
public enum TransportMode
{
    /// <summary>
    /// UDP transport with separate ports for RTP and RTCP.
    /// </summary>
    UDP,

    /// <summary>
    /// TCP interleaved transport where RTP/RTCP are multiplexed over the RTSP connection.
    /// </summary>
    TCPInterleaved
}
