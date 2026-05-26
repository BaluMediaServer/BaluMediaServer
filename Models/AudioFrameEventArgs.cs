namespace BaluMediaServer.Models;

/// <summary>
/// Raw PCM audio frame produced by <c>AudioCaptureService</c>. Mirrors
/// <see cref="FrameEventArgs"/> but carries 16-bit signed little-endian PCM samples
/// instead of YUV video data.
/// </summary>
public class AudioFrameEventArgs : EventArgs
{
    /// <summary>
    /// Raw PCM-16 sample bytes. Layout for stereo is interleaved L/R, two bytes per
    /// sample (little-endian). Length equals <c>samples * Channels * 2</c>.
    /// </summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Number of audio samples in <see cref="Data"/> per channel.
    /// </summary>
    public int SampleCount { get; set; }

    /// <summary>
    /// Sample rate in Hz (e.g. 44100, 48000).
    /// </summary>
    public int SampleRateHz { get; set; }

    /// <summary>
    /// Channel count: 1 for mono, 2 for stereo.
    /// </summary>
    public int Channels { get; set; }

    /// <summary>
    /// Capture timestamp in microseconds, suitable for passing straight to MediaCodec
    /// as the input buffer presentation time. Wall-clock based so it aligns with the
    /// video pipeline's Stopwatch-derived RTP timestamps.
    /// </summary>
    public long Timestamp { get; set; }
}
