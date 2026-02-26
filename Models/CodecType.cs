namespace BaluMediaServer.Models;

/// <summary>
/// Specifies the video codec type used for RTSP streaming.
/// </summary>
public enum CodecType
{
    /// <summary>
    /// Motion JPEG codec. Each frame is independently compressed as a JPEG image.
    /// Higher bandwidth but simpler encoding and universal compatibility.
    /// </summary>
    MJPEG,

    /// <summary>
    /// H.264/AVC codec. Uses inter-frame compression for efficient bandwidth usage.
    /// Lower bandwidth with hardware-accelerated encoding support.
    /// </summary>
    H264
}
