namespace BaluMediaServer.Services.Onvif;

/// <summary>
/// Device-wide context the ONVIF SOAP builders need to render responses: the device identity
/// (from <see cref="Models.OnvifOptions"/>), the three server ports, and a live IP-address
/// resolver (wired to the same interface-selection logic the RTSP SDP uses). Plain C#, no
/// Android dependencies, so the device/media services are unit-testable on the host.
/// </summary>
public sealed class OnvifDeviceContext
{
    public required string Manufacturer { get; init; }
    public required string Model { get; init; }
    public required string FirmwareVersion { get; init; }
    public required string SerialNumber { get; init; }
    public required string HardwareId { get; init; }

    /// <summary>RTSP server port (the stream URIs ONVIF advertises point here).</summary>
    public required int RtspPort { get; init; }
    /// <summary>MJPEG HTTP server port (serves the snapshot endpoint).</summary>
    public required int MjpegPort { get; init; }
    /// <summary>ONVIF SOAP/HTTP service port.</summary>
    public required int OnvifPort { get; init; }

    /// <summary>
    /// Returns the device's current LAN IPv4 address. Resolved live (not cached) so the URIs
    /// stay correct if the network interface changes. Wire this to
    /// <c>SdpGenerator.GetLocalIpAddress</c> so ONVIF and the RTSP SDP agree on the address.
    /// </summary>
    public required Func<string> GetIpAddress { get; init; }

    public string DeviceServiceUri => $"http://{GetIpAddress()}:{OnvifPort}/onvif/device_service";
    public string MediaServiceUri  => $"http://{GetIpAddress()}:{OnvifPort}/onvif/media_service";
}

/// <summary>
/// One ONVIF media profile, describing a single camera's video source + H.264 encoder
/// configuration and the URIs to reach it. Built by <c>Server</c> from the live encoder
/// resolution and bitrate. Plain C#, no Android dependencies.
/// </summary>
public sealed class OnvifProfile
{
    /// <summary>Profile token, e.g. <c>Profile_back</c>. Stable per camera so clients can re-reference it.</summary>
    public required string Token { get; init; }
    /// <summary>Human-readable profile name, e.g. <c>BackCamera</c>.</summary>
    public required string Name { get; init; }
    /// <summary>Short camera key, <c>back</c> or <c>front</c>; used to derive sub-tokens and the URIs.</summary>
    public required string CameraKey { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Fps { get; init; }
    /// <summary>Encoder bitrate in kbit/s (ONVIF expresses video bitrate in kbps).</summary>
    public required int BitrateKbps { get; init; }

    /// <summary>RTSP path for this camera, e.g. <c>/live/back</c>.</summary>
    public required string RtspPath { get; init; }
    /// <summary>HTTP snapshot path for this camera, e.g. <c>/snapshot/back.jpg</c>.</summary>
    public required string SnapshotPath { get; init; }

    public string VideoSourceToken       => $"VideoSource_{CameraKey}";
    public string VideoSourceConfigToken => $"VideoSourceConfig_{CameraKey}";
    public string EncoderConfigToken     => $"VideoEncoderConfig_{CameraKey}";
}
