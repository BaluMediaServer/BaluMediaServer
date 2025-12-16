namespace BaluMediaServer.Models;

/// <summary>
/// Configuration class for initializing the RTSP server with all available options.
/// Provides a simplified way to configure server settings compared to constructor parameters.
/// </summary>
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
}
