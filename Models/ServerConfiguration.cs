#if ANDROID
using BaluMediaServer.Services;
#endif

namespace BaluMediaServer.Models;

/// <summary>
/// Configuration class for initializing the RTSP server with all available options.
/// Provides a simplified way to configure server settings compared to constructor parameters.
/// </summary>
/// <remarks>
/// <para>
/// This class allows configuring resolution for both front and back cameras independently.
/// Resolution can be set using either <see cref="VideoResolution"/> presets or custom width/height values.
/// </para>
/// <para>
/// <b>Resolution and H.264:</b> The selected resolution directly affects H.264 encoder configuration.
/// Higher resolutions require more processing power and bandwidth. The encoder buffer size is
/// calculated as (width * height * 3) / 2 for YUV420 format.
/// </para>
/// </remarks>
public class ServerConfiguration
{
    /// <summary>
    /// Gets or sets the RTSP server port. Default is 7778.
    /// </summary>
    public int Port { get; set; } = 7778;

    /// <summary>
    /// Gets or sets the maximum number of concurrent clients. Default is 10.
    /// </summary>
    public int MaxClients { get; set; } = 10;

    /// <summary>
    /// Gets or sets the dictionary of username/password pairs for authentication.
    /// </summary>
    public Dictionary<string, string> Users { get; set; } = new();

    /// <summary>
    /// Gets or sets the JPEG compression quality for MJPEG streaming (1-100). Default is 80.
    /// </summary>
    public int MjpegServerQuality { get; set; } = 80;

    /// <summary>
    /// Gets or sets the MJPEG HTTP server port. Default is 8089.
    /// </summary>
    public int MjpegServerPort { get; set; } = 8089;

    /// <summary>
    /// Gets or sets a value indicating whether authentication is required. Default is true.
    /// </summary>
    public bool AuthRequired { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the front camera is enabled. Default is true.
    /// </summary>
    public bool FrontCameraEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the back camera is enabled. Default is true.
    /// </summary>
    public bool BackCameraEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the video resolution for the back camera using a predefined preset.
    /// Default is <see cref="VideoResolution.VGA_640x480"/>.
    /// </summary>
    /// <remarks>
    /// Setting this property automatically configures the appropriate bitrate for H.264 encoding.
    /// For custom resolutions, use <see cref="BackCameraWidth"/> and <see cref="BackCameraHeight"/> instead.
    /// </remarks>
    public VideoResolution BackCameraResolution { get; set; } = VideoResolution.VGA_640x480;

    /// <summary>
    /// Gets or sets a custom width for the back camera in pixels.
    /// When set to a value greater than 0, this overrides <see cref="BackCameraResolution"/>.
    /// Default is 0 (use resolution preset).
    /// </summary>
    public int BackCameraWidth { get; set; } = 0;

    /// <summary>
    /// Gets or sets a custom height for the back camera in pixels.
    /// When set to a value greater than 0, this overrides <see cref="BackCameraResolution"/>.
    /// Default is 0 (use resolution preset).
    /// </summary>
    public int BackCameraHeight { get; set; } = 0;

    /// <summary>
    /// Gets or sets the video resolution for the front camera using a predefined preset.
    /// Default is <see cref="VideoResolution.VGA_640x480"/>.
    /// </summary>
    /// <remarks>
    /// Setting this property automatically configures the appropriate bitrate for H.264 encoding.
    /// For custom resolutions, use <see cref="FrontCameraWidth"/> and <see cref="FrontCameraHeight"/> instead.
    /// </remarks>
    public VideoResolution FrontCameraResolution { get; set; } = VideoResolution.VGA_640x480;

    /// <summary>
    /// Gets or sets a custom width for the front camera in pixels.
    /// When set to a value greater than 0, this overrides <see cref="FrontCameraResolution"/>.
    /// Default is 0 (use resolution preset).
    /// </summary>
    public int FrontCameraWidth { get; set; } = 0;

    /// <summary>
    /// Gets or sets a custom height for the front camera in pixels.
    /// When set to a value greater than 0, this overrides <see cref="FrontCameraResolution"/>.
    /// Default is 0 (use resolution preset).
    /// </summary>
    public int FrontCameraHeight { get; set; } = 0;

    /// <summary>
    /// Gets the effective width for the back camera.
    /// Returns <see cref="BackCameraWidth"/> if set, otherwise the width from <see cref="BackCameraResolution"/>.
    /// </summary>
    public int GetBackCameraWidth() => BackCameraWidth > 0 ? BackCameraWidth : BackCameraResolution.GetWidth();

    /// <summary>
    /// Gets the effective height for the back camera.
    /// Returns <see cref="BackCameraHeight"/> if set, otherwise the height from <see cref="BackCameraResolution"/>.
    /// </summary>
    public int GetBackCameraHeight() => BackCameraHeight > 0 ? BackCameraHeight : BackCameraResolution.GetHeight();

    /// <summary>
    /// Gets the effective width for the front camera.
    /// Returns <see cref="FrontCameraWidth"/> if set, otherwise the width from <see cref="FrontCameraResolution"/>.
    /// </summary>
    public int GetFrontCameraWidth() => FrontCameraWidth > 0 ? FrontCameraWidth : FrontCameraResolution.GetWidth();

    /// <summary>
    /// Gets the effective height for the front camera.
    /// Returns <see cref="FrontCameraHeight"/> if set, otherwise the height from <see cref="FrontCameraResolution"/>.
    /// </summary>
    public int GetFrontCameraHeight() => FrontCameraHeight > 0 ? FrontCameraHeight : FrontCameraResolution.GetHeight();

    /// <summary>
    /// Gets or sets a value indicating whether to auto-start the MJPEG server. Default is true.
    /// </summary>
    public bool StartMjpegServer { get; set; } = true;

    /// <summary>
    /// Gets or sets the base address for the server to bind to. Default is "0.0.0.0" (all interfaces).
    /// </summary>
    public string BaseAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// Gets or sets the primary video profile configuration.
    /// </summary>
    public VideoProfile PrimaryProfile { get; set; } = new();

    /// <summary>
    /// Gets or sets the secondary video profile configuration.
    /// </summary>
    public VideoProfile SecondaryProfile { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether to use HTTPS for the MJPEG server. Default is false.
    /// </summary>
    public bool UseHttps { get; set; } = false;

    /// <summary>
    /// Gets or sets the path to the SSL certificate file for HTTPS.
    /// </summary>
    public string? CertificatePath { get; set; }

    /// <summary>
    /// Gets or sets the password for the SSL certificate.
    /// </summary>
    public string? CertificatePassword { get; set; }

#if ANDROID
    /// <summary>
    /// Optional text overlay slots burned into the back camera H.264 stream.
    /// <list type="bullet">
    ///   <item><c>null</c> — use the default layout (device name + clock in bottom-left).</item>
    ///   <item>Empty array — disable all overlays.</item>
    ///   <item>Non-empty array — use the supplied slots (up to 4).</item>
    /// </list>
    /// </summary>
    public OverlaySlot[]? BackCameraOverlaySlots { get; set; }

    /// <summary>
    /// Optional text overlay slots burned into the front camera H.264 stream.
    /// <list type="bullet">
    ///   <item><c>null</c> — no overlay (front camera has no default).</item>
    ///   <item>Non-empty array — use the supplied slots (up to 4).</item>
    /// </list>
    /// </summary>
    public OverlaySlot[]? FrontCameraOverlaySlots { get; set; }
#endif

    /// <summary>
    /// Gets or sets a value indicating whether the server is enabled and allowed to start.
    /// When false, calling <see cref="Server.Start"/> will return immediately without starting.
    /// Default is true.
    /// </summary>
    /// <remarks>
    /// This flag controls whether the server can be started. It is checked in the Server.Start() method
    /// before initializing the socket and beginning to accept connections. Setting this to false
    /// effectively disables the server without having to dispose of it.
    /// </remarks>
    public bool EnableServer { get; set; } = true;
}
