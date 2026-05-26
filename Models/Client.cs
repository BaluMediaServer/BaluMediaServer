using System.Net;
using System.Net.Sockets;
using System.Threading;

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

    // Volatile backing field: IsPlaying is written under lock(client) in TransportManager
    // but read lock-free in the StreamingController streaming loop.
    private volatile bool _isPlaying;
    /// <summary>
    /// Gets or sets a value indicating whether the client is currently receiving stream data.
    /// </summary>
    public bool IsPlaying { get => _isPlaying; set => _isPlaying = value; }

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
    /// <summary>
    /// Monotonic millisecond timestamp of last successful send (from Environment.TickCount64).
    /// </summary>
    public long LastActivityTick { get; set; } = Environment.TickCount64;

    // Volatile backing field: ConsecutiveSendErrors is incremented under lock(client) in
    // TransportManager but read lock-free in the StreamingController streaming loop.
    private volatile int _consecutiveSendErrors;
    /// <summary>
    /// Gets or sets the count of consecutive send errors.
    /// Used to detect broken connections.
    /// </summary>
    public int ConsecutiveSendErrors { get => _consecutiveSendErrors; set => _consecutiveSendErrors = value; }

    /// <summary>
    /// Serializes all RTP/RTCP sends for this client to prevent interleaved TCP framing corruption.
    /// </summary>
    public SemaphoreSlim SendLock { get; } = new SemaphoreSlim(1, 1);

    // ---------------------------------------------------------------------
    // Audio track (trackID=1) transport state. Populated only when the
    // server is configured with EnableAudioTrack=true and the client has
    // issued a SETUP for the audio track. Mirrors the video fields above.
    // ---------------------------------------------------------------------

    /// <summary>True once SETUP for the audio track (trackID=1) has been accepted.</summary>
    public bool AudioSetupComplete { get; set; }

    /// <summary>RTP channel for the audio track in TCP interleaved mode.</summary>
    public byte AudioRtpChannel { get; set; }

    /// <summary>RTCP channel for the audio track in TCP interleaved mode.</summary>
    public byte AudioRtcpChannel { get; set; }

    /// <summary>UDP RTP endpoint for the audio track.</summary>
    public IPEndPoint? AudioRtpEndPoint { get; set; }

    /// <summary>UDP RTCP endpoint for the audio track.</summary>
    public IPEndPoint? AudioRtcpEndPoint { get; set; }

    /// <summary>Server-side UDP socket carrying audio RTP packets.</summary>
    public Socket? AudioUdpSocket { get; set; }

    /// <summary>Server-side UDP socket carrying audio RTCP packets.</summary>
    public Socket? AudioRtcpSocket { get; set; }

    /// <summary>Random SSRC for the audio RTP stream (independent from video).</summary>
    public uint AudioSsrcId { get; set; } = (uint)Random.Shared.Next();

    /// <summary>Sequence number for the audio RTP stream.</summary>
    public ushort AudioSequenceNumber { get; set; }

    /// <summary>Current RTP timestamp (audio clock units) for the audio stream.</summary>
    public uint AudioRtpTimestamp { get; set; }

    /// <summary>Base Stopwatch tick captured the first time an audio RTP packet is built.</summary>
    public ulong AudioBaseEncoderTimestamp { get; set; }

    /// <summary>Initial RTP timestamp for the audio stream, used as the rtptime baseline.</summary>
    public uint AudioBaseRtpTimestamp { get; set; }

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
        this.AudioUdpSocket?.Close();
        this.AudioUdpSocket?.Dispose();
        this.AudioRtcpSocket?.Close();
        this.AudioRtcpSocket?.Dispose();
        this.RtcpSocket = null;
        this.UdpSocket = null;
        this.AudioUdpSocket = null;
        this.AudioRtcpSocket = null;
        this.SendLock?.Dispose();
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
