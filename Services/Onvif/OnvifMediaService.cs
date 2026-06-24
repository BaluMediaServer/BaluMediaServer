using System.Xml.Linq;

namespace BaluMediaServer.Services.Onvif;

/// <summary>
/// Builds ONVIF Media-service (<c>trt</c>) SOAP response bodies: profiles, video sources/encoder
/// configurations, and the stream/snapshot URIs. Profiles are supplied by a live delegate so the
/// advertised resolution/bitrate always reflect the current encoder state.
///
/// Each method returns the inner XML of <c>soap:Body</c>; <see cref="Handle"/> returns <c>null</c>
/// for an unsupported operation or an unknown profile token (the server emits a SOAP fault).
/// Plain C# (no Android deps) so URI composition and profile XML are unit-testable on the host.
/// </summary>
public sealed class OnvifMediaService
{
    private readonly OnvifDeviceContext _ctx;
    private readonly Func<IReadOnlyList<OnvifProfile>> _getProfiles;

    public OnvifMediaService(OnvifDeviceContext ctx, Func<IReadOnlyList<OnvifProfile>> getProfiles)
    {
        _ctx = ctx;
        _getProfiles = getProfiles;
    }

    /// <summary>Dispatches a media-service operation. Returns the body-inner XML, or null if unsupported/unknown token.</summary>
    public string? Handle(string operation, XElement? body)
    {
        var profiles = _getProfiles();
        switch (operation)
        {
            case "GetProfiles":
                return $"<trt:GetProfilesResponse>{string.Concat(profiles.Select(p => BuildProfile(p, "trt:Profiles")))}</trt:GetProfilesResponse>";

            case "GetProfile":
            {
                var p = FindByProfileToken(profiles, body);
                return p is null ? null : $"<trt:GetProfileResponse>{BuildProfile(p, "trt:Profile")}</trt:GetProfileResponse>";
            }

            case "GetVideoSources":
                return $"<trt:GetVideoSourcesResponse>{string.Concat(profiles.Select(BuildVideoSource))}</trt:GetVideoSourcesResponse>";

            case "GetVideoSourceConfigurations":
                return $"<trt:GetVideoSourceConfigurationsResponse>{string.Concat(profiles.Select(p => BuildSourceConfig(p, "trt:Configurations")))}</trt:GetVideoSourceConfigurationsResponse>";

            case "GetVideoEncoderConfigurations":
                return $"<trt:GetVideoEncoderConfigurationsResponse>{string.Concat(profiles.Select(p => BuildEncoderConfig(p, "trt:Configurations")))}</trt:GetVideoEncoderConfigurationsResponse>";

            case "GetVideoEncoderConfiguration":
            {
                var p = FindByConfigToken(profiles, body);
                return p is null ? null : $"<trt:GetVideoEncoderConfigurationResponse>{BuildEncoderConfig(p, "trt:Configuration")}</trt:GetVideoEncoderConfigurationResponse>";
            }

            case "GetStreamUri":
            {
                var p = FindByProfileToken(profiles, body);
                return p is null ? null : BuildMediaUri("GetStreamUri", StreamUri(p));
            }

            case "GetSnapshotUri":
            {
                var p = FindByProfileToken(profiles, body);
                return p is null ? null : BuildMediaUri("GetSnapshotUri", SnapshotUri(p));
            }

            default:
                return null;
        }
    }

    /// <summary>RTSP stream URI for a profile, e.g. <c>rtsp://192.168.1.5:7778/live/back</c>.</summary>
    public string StreamUri(OnvifProfile p) => $"rtsp://{_ctx.GetIpAddress()}:{_ctx.RtspPort}{p.RtspPath}";

    /// <summary>HTTP snapshot URI for a profile, e.g. <c>http://192.168.1.5:8089/snapshot/back.jpg</c>.</summary>
    public string SnapshotUri(OnvifProfile p) => $"http://{_ctx.GetIpAddress()}:{_ctx.MjpegPort}{p.SnapshotPath}";

    private static OnvifProfile? FindByProfileToken(IReadOnlyList<OnvifProfile> profiles, XElement? body)
    {
        var token = ParamValue(body, "ProfileToken");
        if (string.IsNullOrEmpty(token)) return null;
        return profiles.FirstOrDefault(p => p.Token == token);
    }

    private static OnvifProfile? FindByConfigToken(IReadOnlyList<OnvifProfile> profiles, XElement? body)
    {
        var token = ParamValue(body, "ConfigurationToken");
        if (string.IsNullOrEmpty(token)) return null;
        return profiles.FirstOrDefault(p => p.EncoderConfigToken == token);
    }

    private static string? ParamValue(XElement? body, string localName) =>
        body?.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value?.Trim();

    private static string BuildMediaUri(string op, string uri) =>
        $"<trt:{op}Response><trt:MediaUri>" +
        $"<tt:Uri>{OnvifSoap.Escape(uri)}</tt:Uri>" +
        "<tt:InvalidAfterConnect>false</tt:InvalidAfterConnect>" +
        "<tt:InvalidAfterReboot>false</tt:InvalidAfterReboot>" +
        "<tt:Timeout>PT0S</tt:Timeout>" +
        $"</trt:MediaUri></trt:{op}Response>";

    private string BuildProfile(OnvifProfile p, string element) =>
        $"<{element} token=\"{OnvifSoap.Escape(p.Token)}\" fixed=\"true\">" +
        $"<tt:Name>{OnvifSoap.Escape(p.Name)}</tt:Name>" +
        BuildSourceConfig(p, "tt:VideoSourceConfiguration") +
        BuildEncoderConfig(p, "tt:VideoEncoderConfiguration") +
        $"</{element}>";

    private static string BuildVideoSource(OnvifProfile p) =>
        $"<trt:VideoSources token=\"{OnvifSoap.Escape(p.VideoSourceToken)}\">" +
        $"<tt:Framerate>{p.Fps}</tt:Framerate>" +
        $"<tt:Resolution><tt:Width>{p.Width}</tt:Width><tt:Height>{p.Height}</tt:Height></tt:Resolution>" +
        "</trt:VideoSources>";

    private static string BuildSourceConfig(OnvifProfile p, string element) =>
        $"<{element} token=\"{OnvifSoap.Escape(p.VideoSourceConfigToken)}\">" +
        $"<tt:Name>{OnvifSoap.Escape(p.VideoSourceConfigToken)}</tt:Name>" +
        "<tt:UseCount>1</tt:UseCount>" +
        $"<tt:SourceToken>{OnvifSoap.Escape(p.VideoSourceToken)}</tt:SourceToken>" +
        $"<tt:Bounds x=\"0\" y=\"0\" width=\"{p.Width}\" height=\"{p.Height}\"/>" +
        $"</{element}>";

    private static string BuildEncoderConfig(OnvifProfile p, string element) =>
        $"<{element} token=\"{OnvifSoap.Escape(p.EncoderConfigToken)}\">" +
        $"<tt:Name>{OnvifSoap.Escape(p.EncoderConfigToken)}</tt:Name>" +
        "<tt:UseCount>1</tt:UseCount>" +
        "<tt:Encoding>H264</tt:Encoding>" +
        $"<tt:Resolution><tt:Width>{p.Width}</tt:Width><tt:Height>{p.Height}</tt:Height></tt:Resolution>" +
        "<tt:Quality>5</tt:Quality>" +
        "<tt:RateControl>" +
        $"<tt:FrameRateLimit>{p.Fps}</tt:FrameRateLimit>" +
        "<tt:EncodingInterval>1</tt:EncodingInterval>" +
        $"<tt:BitrateLimit>{p.BitrateKbps}</tt:BitrateLimit>" +
        "</tt:RateControl>" +
        "<tt:H264><tt:GovLength>30</tt:GovLength><tt:H264Profile>Baseline</tt:H264Profile></tt:H264>" +
        "<tt:Multicast><tt:Address><tt:Type>IPv4</tt:Type><tt:IPv4Address>0.0.0.0</tt:IPv4Address></tt:Address>" +
        "<tt:Port>0</tt:Port><tt:TTL>1</tt:TTL><tt:AutoStart>false</tt:AutoStart></tt:Multicast>" +
        "<tt:SessionTimeout>PT60S</tt:SessionTimeout>" +
        $"</{element}>";
}
