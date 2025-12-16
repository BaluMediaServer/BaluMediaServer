namespace BaluMediaServer.Models;

/// <summary>
/// Represents a video encoding profile configuration.
/// Defines resolution, bitrate, and quality settings for video streaming.
/// </summary>
public class VideoProfile
{
    private string _name = string.Empty;

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
    /// Gets or sets the video height in pixels. Default is 640.
    /// </summary>
    public int Height { get; set; } = 640;

    /// <summary>
    /// Gets or sets the video width in pixels. Default is 480.
    /// </summary>
    public int Width { get; set; } = 480;

    /// <summary>
    /// Gets or sets the maximum bitrate in bits per second. Default is 4,000,000 (4 Mbps).
    /// </summary>
    public int MaxBitrate { get; set; } = 4000000;

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
}
