namespace BaluMediaServer.Models;

/// <summary>
/// Predefined video resolution presets for camera capture and H.264 encoding.
/// Resolution selection directly affects H.264 encoder configuration, bitrate requirements, and bandwidth usage.
/// </summary>
/// <remarks>
/// <para>
/// <b>Important H.264 Considerations:</b>
/// </para>
/// <list type="bullet">
/// <item>Higher resolutions require more processing power and may cause frame drops on lower-end devices</item>
/// <item>The H.264 encoder buffer size is calculated as (width * height * 3) / 2 for YUV420 format</item>
/// <item>Bitrate should scale with resolution - higher resolution needs higher bitrate for quality</item>
/// <item>Some devices may not support all resolutions - always check camera capabilities</item>
/// </list>
/// </remarks>
public enum VideoResolution
{
    /// <summary>
    /// 320x240 resolution (QVGA). Lowest quality, minimal bandwidth.
    /// Recommended bitrate: 300-500 Kbps. Frame buffer: ~115 KB.
    /// </summary>
    QVGA_320x240,

    /// <summary>
    /// 480x360 resolution. Low quality, low bandwidth.
    /// Recommended bitrate: 500-800 Kbps. Frame buffer: ~259 KB.
    /// </summary>
    Low_480x360,

    /// <summary>
    /// 640x480 resolution (VGA). Standard definition, balanced quality and bandwidth.
    /// This is the default and most compatible resolution for H.264 encoding.
    /// Recommended bitrate: 800 Kbps - 1.5 Mbps. Frame buffer: ~460 KB.
    /// </summary>
    VGA_640x480,

    /// <summary>
    /// 800x600 resolution (SVGA). Enhanced standard definition.
    /// Recommended bitrate: 1-2 Mbps. Frame buffer: ~720 KB.
    /// </summary>
    SVGA_800x600,

    /// <summary>
    /// 1280x720 resolution (HD/720p). High definition quality.
    /// Requires more processing power and bandwidth.
    /// Recommended bitrate: 2-4 Mbps. Frame buffer: ~1.38 MB.
    /// </summary>
    HD_1280x720,

    /// <summary>
    /// 1920x1080 resolution (Full HD/1080p). High quality.
    /// Requires significant processing power and bandwidth.
    /// Recommended bitrate: 4-8 Mbps. Frame buffer: ~3.11 MB.
    /// </summary>
    FullHD_1920x1080,

    /// <summary>
    /// 2560x1440 resolution (QHD/2K). Ultra-high quality.
    /// Requires high-end device with hardware encoder support.
    /// Recommended bitrate: 8-16 Mbps. Frame buffer: ~5.53 MB.
    /// </summary>
    QHD_2560x1440,

    /// <summary>
    /// 3840x2160 resolution (4K UHD). Maximum quality.
    /// Requires flagship device with 4K hardware encoder support.
    /// May not be supported on all devices - encoder will fall back automatically.
    /// Recommended bitrate: 15-30 Mbps. Frame buffer: ~12.4 MB.
    /// </summary>
    UHD_3840x2160
}

/// <summary>
/// Extension methods for <see cref="VideoResolution"/> enum.
/// Provides helper methods to extract width, height, and recommended settings.
/// </summary>
public static class VideoResolutionExtensions
{
    /// <summary>
    /// Gets the width in pixels for the specified resolution.
    /// </summary>
    /// <param name="resolution">The video resolution preset.</param>
    /// <returns>The width in pixels.</returns>
    public static int GetWidth(this VideoResolution resolution) => resolution switch
    {
        VideoResolution.QVGA_320x240 => 320,
        VideoResolution.Low_480x360 => 480,
        VideoResolution.VGA_640x480 => 640,
        VideoResolution.SVGA_800x600 => 800,
        VideoResolution.HD_1280x720 => 1280,
        VideoResolution.FullHD_1920x1080 => 1920,
        VideoResolution.QHD_2560x1440 => 2560,
        VideoResolution.UHD_3840x2160 => 3840,
        _ => 640
    };

    /// <summary>
    /// Gets the height in pixels for the specified resolution.
    /// </summary>
    /// <param name="resolution">The video resolution preset.</param>
    /// <returns>The height in pixels.</returns>
    public static int GetHeight(this VideoResolution resolution) => resolution switch
    {
        VideoResolution.QVGA_320x240 => 240,
        VideoResolution.Low_480x360 => 360,
        VideoResolution.VGA_640x480 => 480,
        VideoResolution.SVGA_800x600 => 600,
        VideoResolution.HD_1280x720 => 720,
        VideoResolution.FullHD_1920x1080 => 1080,
        VideoResolution.QHD_2560x1440 => 1440,
        VideoResolution.UHD_3840x2160 => 2160,
        _ => 480
    };

    /// <summary>
    /// Gets the recommended minimum bitrate in bits per second for H.264 encoding.
    /// </summary>
    /// <param name="resolution">The video resolution preset.</param>
    /// <returns>The recommended minimum bitrate in bps.</returns>
    public static int GetRecommendedMinBitrate(this VideoResolution resolution) => resolution switch
    {
        VideoResolution.QVGA_320x240 => 300000,
        VideoResolution.Low_480x360 => 500000,
        VideoResolution.VGA_640x480 => 800000,
        VideoResolution.SVGA_800x600 => 1000000,
        VideoResolution.HD_1280x720 => 2000000,
        VideoResolution.FullHD_1920x1080 => 4000000,
        VideoResolution.QHD_2560x1440 => 8000000,
        VideoResolution.UHD_3840x2160 => 15000000,
        _ => 800000
    };

    /// <summary>
    /// Gets the recommended maximum bitrate in bits per second for H.264 encoding.
    /// </summary>
    /// <param name="resolution">The video resolution preset.</param>
    /// <returns>The recommended maximum bitrate in bps.</returns>
    public static int GetRecommendedMaxBitrate(this VideoResolution resolution) => resolution switch
    {
        VideoResolution.QVGA_320x240 => 500000,
        VideoResolution.Low_480x360 => 800000,
        VideoResolution.VGA_640x480 => 1500000,
        VideoResolution.SVGA_800x600 => 2000000,
        VideoResolution.HD_1280x720 => 4000000,
        VideoResolution.FullHD_1920x1080 => 8000000,
        VideoResolution.QHD_2560x1440 => 16000000,
        VideoResolution.UHD_3840x2160 => 30000000,
        _ => 1500000
    };

    /// <summary>
    /// Gets the dimensions as a tuple (width, height).
    /// </summary>
    /// <param name="resolution">The video resolution preset.</param>
    /// <returns>A tuple containing (width, height).</returns>
    public static (int Width, int Height) GetDimensions(this VideoResolution resolution)
        => (resolution.GetWidth(), resolution.GetHeight());

    /// <summary>
    /// Calculates the expected YUV420 frame buffer size in bytes.
    /// Used for H.264 encoder buffer allocation.
    /// </summary>
    /// <param name="resolution">The video resolution preset.</param>
    /// <returns>The frame buffer size in bytes.</returns>
    public static int GetFrameBufferSize(this VideoResolution resolution)
        => (resolution.GetWidth() * resolution.GetHeight() * 3) / 2;

    /// <summary>
    /// Gets a human-readable display name for the resolution.
    /// </summary>
    /// <param name="resolution">The video resolution preset.</param>
    /// <returns>A formatted display name string.</returns>
    public static string GetDisplayName(this VideoResolution resolution) => resolution switch
    {
        VideoResolution.QVGA_320x240 => "QVGA (320x240)",
        VideoResolution.Low_480x360 => "Low (480x360)",
        VideoResolution.VGA_640x480 => "VGA (640x480)",
        VideoResolution.SVGA_800x600 => "SVGA (800x600)",
        VideoResolution.HD_1280x720 => "HD 720p (1280x720)",
        VideoResolution.FullHD_1920x1080 => "Full HD 1080p (1920x1080)",
        VideoResolution.QHD_2560x1440 => "QHD 2K (2560x1440)",
        VideoResolution.UHD_3840x2160 => "4K UHD (3840x2160)",
        _ => "VGA (640x480)"
    };
}
