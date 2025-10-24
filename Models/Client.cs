using System.Net;
using System.Net.Sockets;

namespace BaluMediaServer.Models;

public class Client : IDisposable
{
    public Socket Socket { get; set; } = default!;
    public string Id { get; set; } = string.Empty;
    public DateTime ConnectedAt { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public TransportMode Transport { get; set; }
    public byte RtpChannel { get; set; }
    public byte RtcpChannel { get; set; }
    public bool IsPlaying { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public CodecType Codec { get; set; } = CodecType.H264;
    public long LastH264FrameTimestamp { get; set; }
    public IPEndPoint? RtpEndPoint { get; set; }
    public IPEndPoint? RtcpEndPoint { get; set; }
    public Socket? UdpSocket { get; set; }
    public ushort SequenceNumber { get; set; } = 0;
    public uint RtpTimestamp { get; set; } = 0;
    public uint SsrcId { get; set; } = (uint)Random.Shared.Next();
    public DateTime LastRtpTime { get; set; } = DateTime.UtcNow;
    public DateTime LastCodecUpdate { get; set; } = DateTime.UtcNow;
    public int FrameCount { get; set; } = 0;
    public int CurrentBitrate { get; set; } = 2000000;
    public Socket? RtcpSocket { get; set; }
    public int CameraId { get; set; } = 0;
    public VideoProfile VideoProfile { get; set; } = new();
    public List<byte[]> RtcpBuffer = new();
    public byte[] RtpBuffer { get; set; } = new byte[1500]; // MTU Size
    public DateTime LastRtcpFlush = DateTime.UtcNow;
    public ulong BaseEncoderTimestamp { get; set; } = 0;
    public uint BaseRtpTimestamp { get; set; } = 0;
    public readonly object RtcpLock = new();
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

public enum TransportMode
{
    UDP,
    TCPInterleaved
}