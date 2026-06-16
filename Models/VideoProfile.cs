namespace BaluMediaServer.Models;

/// <summary>
/// Represents a video encoding profile configuration.
/// Defines resolution, bitrate, and quality settings for video streaming.
/// </summary>
/// <remarks>
/// <para>
/// The resolution can be set either by using the <see cref="Resolution"/> property with a preset,
/// or by manually setting <see cref="Width"/> and <see cref="Height"/> properties.
/// </para>
/// <para>
/// <b>Important:</b> Resolution directly affects H.264 encoder performance and bandwidth requirements.
/// Higher resolutions require more processing power and network bandwidth.
/// </para>
/// </remarks>
public class VideoProfile
{
    private string _name = string.Empty;
    private int _width = 640;
    private int _height = 480;
    private VideoResolution? _resolution = null;

    /// <summary>
    /// Gets or sets the profile name used in the URL path.
    /// Special characters and spaces are automatically removed for URL compatibility.
    /// </summary>
    public string Name
    {
        get => _name; set
        {
            if (!String.IsNullOrEmpty(value))
            {
                _name = value.Trim().Replace(" ", "").Replace("/", "");
            }
        }
    }

    /// <summary>
    /// Gets or sets the video resolution using a predefined preset.
    /// Setting this property automatically updates <see cref="Width"/>, <see cref="Height"/>,
    /// <see cref="MinBitrate"/>, and <see cref="MaxBitrate"/> to recommended values.
    /// </summary>
    /// <remarks>
    /// When set to a valid preset, the Width and Height properties will return
    /// the corresponding values. Setting Width or Height manually will clear this preset.
    /// </remarks>
    public VideoResolution? Resolution
    {
        get => _resolution;
        set
        {
            _resolution = value;
            if (value.HasValue)
            {
                _width = value.Value.GetWidth();
                _height = value.Value.GetHeight();
                MinBitrate = value.Value.GetRecommendedMinBitrate();
                MaxBitrate = value.Value.GetRecommendedMaxBitrate();
            }
        }
    }

    /// <summary>
    /// Gets or sets the video width in pixels. Default is 640.
    /// Setting this property clears the <see cref="Resolution"/> preset.
    /// </summary>
    /// <remarks>
    /// For H.264 encoding, width affects the encoder buffer size calculated as (width * height * 3) / 2.
    /// </remarks>
    public int Width
    {
        get => _width;
        set
        {
            _width = value;
            _resolution = null; // Clear preset when manually setting dimensions
        }
    }

    /// <summary>
    /// Gets or sets the video height in pixels. Default is 480.
    /// Setting this property clears the <see cref="Resolution"/> preset.
    /// </summary>
    /// <remarks>
    /// For H.264 encoding, height affects the encoder buffer size calculated as (width * height * 3) / 2.
    /// </remarks>
    public int Height
    {
        get => _height;
        set
        {
            _height = value;
            _resolution = null; // Clear preset when manually setting dimensions
        }
    }

    /// <summary>
    /// Gets or sets the maximum bitrate in bits per second — the ceiling RTCP adaptive bitrate can
    /// recover up to. Default is 20,000,000 (20 Mbps) to match the encoder's AutoBitrateMax. The old
    /// 4 Mbps default capped RTCP below the configured 1080p target (~12 Mbps), so the encoder could
    /// never use its full bitrate and motion stayed starved/blocky regardless of network headroom.
    /// </summary>
    public int MaxBitrate { get; set; } = 20000000;

    /// <summary>
    /// Gets or sets the minimum bitrate in bits per second. Default is 500,000 (500 Kbps).
    /// </summary>
    public int MinBitrate { get; set; } = 500000;

    private int _quality = 80;

    /// <summary>
    /// Gets or sets the JPEG compression quality (10-100). Default is 80.
    /// Values outside the range are clamped to the valid range.
    /// </summary>
    public int Quality
    {
        get => _quality;
        set
        {
            if (value > 100) _quality = 100;
            _quality = value > 100 ? 100 : value < 10 ? 10 : value;
        }
    }

    /// <summary>
    /// Calculates the expected YUV420 frame buffer size in bytes for H.264 encoding.
    /// </summary>
    /// <returns>The frame buffer size in bytes calculated as (width * height * 3) / 2.</returns>
    public int GetFrameBufferSize() => (Width * Height * 3) / 2;

    /// <summary>
    /// Gets the dimensions as a tuple (width, height).
    /// </summary>
    /// <returns>A tuple containing (Width, Height).</returns>
    public (int Width, int Height) GetDimensions() => (Width, Height);
}
