namespace BaluMediaServer.Models;

/// <summary>
/// Encoded AAC access unit produced by <c>AacEncoder</c>. Mirrors
/// <see cref="H264FrameEventArgs"/>, but each event carries exactly one AAC frame
/// (1024 samples for AAC-LC) rather than a list of NAL units.
/// </summary>
public class AacFrameEventArgs : EventArgs
{
    /// <summary>
    /// Raw AAC access-unit bytes as emitted by MediaCodec — no ADTS header,
    /// suitable for direct use as the payload of an RFC 3640 AAC-hbr RTP packet.
    /// </summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Presentation timestamp in microseconds as reported by MediaCodec.
    /// </summary>
    public long Timestamp { get; set; }

    /// <summary>
    /// Stopwatch ticks captured when this frame was drained from the hardware encoder.
    /// Lets downstream code measure encode latency the same way <c>H264FrameEventArgs</c> does.
    /// </summary>
    public long EncodedAt { get; set; }

    /// <summary>
    /// AudioSpecificConfig bytes (csd-0) reported by MediaCodec on the first
    /// CodecConfig frame. Non-null only on the first event so subscribers can
    /// cache it for SDP / RTP fmtp use. Null on every subsequent frame.
    /// </summary>
    public byte[]? AudioSpecificConfig { get; set; }
}
