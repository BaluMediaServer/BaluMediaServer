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

    /// <summary>
    /// Gets or sets the maximum supported video width in pixels.
    /// </summary>
    public int MaxSupportedWidth { get; set; }

    /// <summary>
    /// Gets or sets the maximum supported video height in pixels.
    /// </summary>
    public int MaxSupportedHeight { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the encoder supports FullHD (1920x1080) resolution.
    /// </summary>
    public bool SupportsFullHD { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the encoder supports HD (1280x720) resolution.
    /// </summary>
    public bool SupportsHD { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the encoder supports 4K UHD (3840x2160) resolution.
    /// </summary>
    public bool Supports4K { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the encoder supports QHD/2K (2560x1440) resolution.
    /// </summary>
    public bool SupportsQHD { get; set; }
}
