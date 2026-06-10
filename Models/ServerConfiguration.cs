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
/// <para>
/// <b>Audio:</b> Set <see cref="EnableAudioTrack"/> to <c>true</c> to publish a hardware-encoded
/// AAC-LC audio stream as a second SDP track (<c>m=audio</c>, <c>trackID=1</c>). Sample rate and
/// channel count are controlled by <see cref="AudioSampleRateHz"/> and <see cref="AudioChannels"/>.
/// The feature is off by default so existing single-track clients see byte-identical SDP. Enabling
/// it requires <c>android.permission.RECORD_AUDIO</c> in the host manifest plus a runtime grant.
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
    /// The effective resolution (this preset, or <see cref="BackCameraWidth"/>/<see cref="BackCameraHeight"/>
    /// when set) drives the automatic H.264 bitrate, scaled at ~0.1 bits/pixel/frame. Override the bitrate
    /// at runtime with <c>Server.SetBackCameraBitrate(int)</c>, or pass 0 / call <c>SetBackCameraAutoBitrate()</c>
    /// to return to automatic. For custom resolutions, use <see cref="BackCameraWidth"/> and <see cref="BackCameraHeight"/>.
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
    /// The effective resolution (this preset, or <see cref="FrontCameraWidth"/>/<see cref="FrontCameraHeight"/>
    /// when set) drives the automatic H.264 bitrate, scaled at ~0.1 bits/pixel/frame. Override the bitrate
    /// at runtime with <c>Server.SetFrontCameraBitrate(int)</c>, or pass 0 / call <c>SetFrontCameraAutoBitrate()</c>
    /// to return to automatic. For custom resolutions, use <see cref="FrontCameraWidth"/> and <see cref="FrontCameraHeight"/>.
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
    /// Gets or sets whether RTCP-driven adaptive bitrate is enabled. Default is <c>true</c>.
    /// <para>
    /// When enabled, the server reduces the encoder bitrate on detected packet loss and restores
    /// it (up to the configured auto/manual value) when the network recovers — useful for lossy
    /// or remote links. The configured bitrate (resolution auto-scaling, or a manual
    /// <c>Server.SetBackCameraBitrate</c>) is the ceiling; adaptation never raises it above that,
    /// so on a clean LAN it stays exactly at the configured value.
    /// </para>
    /// <para>
    /// Set to <c>false</c> to pin the encoder strictly to the configured bitrate and ignore RTCP
    /// entirely — recommended when you select the bitrate manually and want it honored verbatim.
    /// </para>
    /// </summary>
    public bool AdaptiveBitrate { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the H.264 Main profile (CABAC entropy coding) should be used when
    /// the hardware encoder supports it. Default is <c>false</c> (Baseline / CAVLC).
    /// <para>
    /// Main yields ~10-15% better compression at the same bitrate, but on MediaTek VENC the
    /// per-frame encode time scales with the entropy-coded bit count under CABAC — at high
    /// resolutions this throttles the frame rate precisely when motion inflates frame sizes
    /// (measured on MT6768 at 2560x1440: static ~13.6fps, motion dropping to ~9.4fps as frames
    /// grew 98→139KB). Baseline keeps encode time roughly resolution-bound instead of
    /// bitrate-bound. Enable Main only for low resolutions or SoCs with fast CABAC hardware.
    /// </para>
    /// </summary>
    public bool PreferMainProfile { get; set; } = false;

    /// <summary>
    /// Gets or sets the H.264 keyframe (IDR) interval in seconds. Default is 5.
    /// <para>
    /// This is a scheduled backstop for reference-chain recovery. The primary mechanism is a
    /// drop-triggered IDR: when a client's send loop falls behind and the fan-out drops an encoded
    /// frame, the streaming loop immediately requests a keyframe. This interval bounds worst-case
    /// corruption if a drop is ever missed. Lower values recover faster but add more IDR encode
    /// stalls (each ~120-220ms on MediaTek at 2K); higher values are smoother but slower to recover.
    /// New/reconnecting clients always receive an immediate IDR regardless of this value.
    /// </para>
    /// </summary>
    public int KeyFrameIntervalSeconds { get; set; } = 5;

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

    /// <summary>
    /// Gets or sets a value indicating whether RTSP SDP should advertise a second
    /// <c>m=audio</c> track alongside the video track. Default is false.
    /// </summary>
    /// <remarks>
    /// When true, the SDP includes an AAC-LC audio track (payload type 97,
    /// <c>a=control:trackID=1</c>) and SETUP for <c>trackID=1</c> is accepted.
    /// In the current build no audio RTP packets are actually emitted — this flag
    /// only exercises the multi-track RTSP/SDP path so player compatibility
    /// (VLC, FFmpeg) can be validated ahead of the real AAC encoder work.
    /// </remarks>
    public bool EnableAudioTrack { get; set; } = false;

    /// <summary>
    /// Audio sample rate in Hz advertised in the SDP <c>a=rtpmap</c> for the audio
    /// track. Default is 44100. Only used when <see cref="EnableAudioTrack"/> is true.
    /// </summary>
    public int AudioSampleRateHz { get; set; } = 44100;

    /// <summary>
    /// Audio channel count advertised in the SDP <c>a=rtpmap</c> for the audio
    /// track. Default is 1 (mono). Only used when <see cref="EnableAudioTrack"/> is true.
    /// </summary>
    public int AudioChannels { get; set; } = 1;
}
