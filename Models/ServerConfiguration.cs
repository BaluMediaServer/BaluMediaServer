namespace BaluMediaServer.Models;

public class ServerConfiguration
{
    public int Port { get; set; } = 7778;
    public int MaxClients { get; set; } = 10;
    public Dictionary<string, string> Users { get; set; } = new();
    public int MjpegServerQuality { get; set; } = 80;
    public int MjpegServerPort { get; set; } = 8089;
    public bool AuthRequired { get; set; } = true;
    public bool FrontCameraEnabled { get; set; } = true;
    public bool BackCameraEnabled { get; set; } = true;
    public bool StartMjpegServer { get; set; } = true;
    public string BaseAddress { get; set; } = "0.0.0.0";
    public VideoProfile PrimaryProfile { get; set; } = new();
    public VideoProfile SecondaryProfile { get; set; } = new();

    // HTTPS configuration for MJPEG server
    public bool UseHttps { get; set; } = false;
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
}