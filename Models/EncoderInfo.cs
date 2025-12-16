using Android.Media;

namespace BaluMediaServer.Models;

/// <summary>
/// Represents information about a hardware video encoder.
/// Used for encoder selection and capability detection.
/// </summary>
public class EncoderInfo
{
    /// <summary>
    /// Gets or sets the Android MediaCodecInfo for this encoder.
    /// </summary>
    public MediaCodecInfo Codec { get; set; } = default!;

    /// <summary>
    /// Gets or sets the encoder name (e.g., "OMX.MTK.VIDEO.ENCODER.AVC").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the encoder score used for ranking during selection.
    /// Higher scores indicate better-suited encoders for streaming.
    /// </summary>
    public int Score { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the encoder supports low-latency mode.
    /// </summary>
    public bool SupportsLowLatency { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the encoder supports bitrate mode configuration.
    /// </summary>
    public bool SupportsBitrateMode { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the encoder supports intra-refresh for error resilience.
    /// </summary>
    public bool SupportsIntraRefresh { get; set; }

    /// <summary>
    /// Gets or sets the list of supported color formats for input buffers.
    /// </summary>
    public List<int> ColorFormats { get; set; } = new();

    /// <summary>
    /// Gets or sets the codec capabilities including supported profiles and levels.
    /// </summary>
    public MediaCodecInfo.CodecCapabilities Capabilities { get; set; } = default!;
}
