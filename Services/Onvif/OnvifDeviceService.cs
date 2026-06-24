namespace BaluMediaServer.Services.Onvif;

/// <summary>
/// Builds ONVIF Device-service (<c>tds</c>) SOAP response bodies. Each method returns the inner
/// XML of <c>soap:Body</c>; the caller wraps it with <see cref="OnvifSoap.Envelope"/>.
/// <see cref="Handle"/> dispatches by operation name and returns <c>null</c> for unsupported ops
/// (the server then emits a SOAP fault).
///
/// Plain C# (no Android deps) so responses are unit-testable on the host. Time is passed in rather
/// than read from the clock, keeping <c>GetSystemDateAndTime</c> deterministic under test.
/// </summary>
public sealed class OnvifDeviceService
{
    private readonly OnvifDeviceContext _ctx;

    /// <summary>The fixed discovery scopes advertised by this device.</summary>
    public IReadOnlyList<string> Scopes { get; }

    public OnvifDeviceService(OnvifDeviceContext ctx)
    {
        _ctx = ctx;
        Scopes = BuildScopes(ctx);
    }

    /// <summary>
    /// Builds the fixed discovery scopes for a device. Shared by <c>GetScopes</c> and the
    /// WS-Discovery Hello/ProbeMatch messages so both advertise an identical scope set.
    /// </summary>
    public static IReadOnlyList<string> BuildScopes(OnvifDeviceContext ctx) => new[]
    {
        "onvif://www.onvif.org/type/video_encoder",
        "onvif://www.onvif.org/type/NetworkVideoTransmitter",
        "onvif://www.onvif.org/Profile/Streaming",
        $"onvif://www.onvif.org/name/{Uri.EscapeDataString(ctx.Model)}",
        $"onvif://www.onvif.org/hardware/{Uri.EscapeDataString(ctx.HardwareId)}",
        "onvif://www.onvif.org/location/unknown",
    };

    /// <summary>Dispatches a device-service operation. Returns the body-inner XML, or null if unsupported.</summary>
    public string? Handle(string operation, DateTime utcNow) => operation switch
    {
        "GetSystemDateAndTime"   => GetSystemDateAndTime(utcNow),
        "GetDeviceInformation"   => GetDeviceInformation(),
        "GetCapabilities"        => GetCapabilities(),
        "GetServices"            => GetServices(),
        "GetServiceCapabilities" => GetServiceCapabilities(),
        "GetScopes"              => GetScopes(),
        _ => null,
    };

    private static string GetSystemDateAndTime(DateTime utcNow) =>
        "<tds:GetSystemDateAndTimeResponse><tds:SystemDateAndTime>" +
        "<tt:DateTimeType>Manual</tt:DateTimeType>" +
        "<tt:DaylightSavings>false</tt:DaylightSavings>" +
        "<tt:TimeZone><tt:TZ>UTC0</tt:TZ></tt:TimeZone>" +
        "<tt:UTCDateTime>" +
        $"<tt:Time><tt:Hour>{utcNow.Hour}</tt:Hour><tt:Minute>{utcNow.Minute}</tt:Minute><tt:Second>{utcNow.Second}</tt:Second></tt:Time>" +
        $"<tt:Date><tt:Year>{utcNow.Year}</tt:Year><tt:Month>{utcNow.Month}</tt:Month><tt:Day>{utcNow.Day}</tt:Day></tt:Date>" +
        "</tt:UTCDateTime>" +
        "</tds:SystemDateAndTime></tds:GetSystemDateAndTimeResponse>";

    private string GetDeviceInformation() =>
        "<tds:GetDeviceInformationResponse>" +
        $"<tds:Manufacturer>{OnvifSoap.Escape(_ctx.Manufacturer)}</tds:Manufacturer>" +
        $"<tds:Model>{OnvifSoap.Escape(_ctx.Model)}</tds:Model>" +
        $"<tds:FirmwareVersion>{OnvifSoap.Escape(_ctx.FirmwareVersion)}</tds:FirmwareVersion>" +
        $"<tds:SerialNumber>{OnvifSoap.Escape(_ctx.SerialNumber)}</tds:SerialNumber>" +
        $"<tds:HardwareId>{OnvifSoap.Escape(_ctx.HardwareId)}</tds:HardwareId>" +
        "</tds:GetDeviceInformationResponse>";

    private string GetCapabilities() =>
        "<tds:GetCapabilitiesResponse><tds:Capabilities>" +
        "<tt:Device>" +
        $"<tt:XAddr>{OnvifSoap.Escape(_ctx.DeviceServiceUri)}</tt:XAddr>" +
        "<tt:System><tt:DiscoveryResolve>false</tt:DiscoveryResolve><tt:DiscoveryBye>true</tt:DiscoveryBye>" +
        "<tt:RemoteDiscovery>false</tt:RemoteDiscovery><tt:SystemBackup>false</tt:SystemBackup>" +
        "<tt:SystemLogging>false</tt:SystemLogging><tt:FirmwareUpgrade>false</tt:FirmwareUpgrade></tt:System>" +
        "</tt:Device>" +
        "<tt:Media>" +
        $"<tt:XAddr>{OnvifSoap.Escape(_ctx.MediaServiceUri)}</tt:XAddr>" +
        "<tt:StreamingCapabilities>" +
        "<tt:RTPMulticast>false</tt:RTPMulticast>" +
        "<tt:RTP_TCP>true</tt:RTP_TCP>" +
        "<tt:RTP_RTSP_TCP>true</tt:RTP_RTSP_TCP>" +
        "</tt:StreamingCapabilities>" +
        "</tt:Media>" +
        "</tds:Capabilities></tds:GetCapabilitiesResponse>";

    private string GetServices() =>
        "<tds:GetServicesResponse>" +
        "<tds:Service>" +
        $"<tds:Namespace>{OnvifSoap.Tds}</tds:Namespace>" +
        $"<tds:XAddr>{OnvifSoap.Escape(_ctx.DeviceServiceUri)}</tds:XAddr>" +
        "<tds:Version><tt:Major>2</tt:Major><tt:Minor>60</tt:Minor></tds:Version>" +
        "</tds:Service>" +
        "<tds:Service>" +
        $"<tds:Namespace>{OnvifSoap.Trt}</tds:Namespace>" +
        $"<tds:XAddr>{OnvifSoap.Escape(_ctx.MediaServiceUri)}</tds:XAddr>" +
        "<tds:Version><tt:Major>2</tt:Major><tt:Minor>60</tt:Minor></tds:Version>" +
        "</tds:Service>" +
        "</tds:GetServicesResponse>";

    private static string GetServiceCapabilities() =>
        "<tds:GetServiceCapabilitiesResponse><tds:Capabilities>" +
        "<tds:Network IPFilter=\"false\" ZeroConfiguration=\"false\" IPVersion6=\"false\" DynDNS=\"false\" />" +
        "<tds:Security TLS1.1=\"false\" TLS1.2=\"false\" OnboardKeyGeneration=\"false\" " +
        "AccessPolicyConfig=\"false\" DefaultAccessPolicy=\"false\" Dot1X=\"false\" " +
        "RemoteUserHandling=\"false\" X.509Token=\"false\" SAMLToken=\"false\" " +
        "KerberosToken=\"false\" UsernameToken=\"true\" HttpDigest=\"false\" RELToken=\"false\" />" +
        "<tds:System DiscoveryResolve=\"false\" DiscoveryBye=\"true\" RemoteDiscovery=\"false\" " +
        "SystemBackup=\"false\" SystemLogging=\"false\" FirmwareUpgrade=\"false\" />" +
        "</tds:Capabilities></tds:GetServiceCapabilitiesResponse>";

    private string GetScopes()
    {
        var items = string.Concat(Scopes.Select(s =>
            "<tds:Scopes><tt:ScopeDef>Fixed</tt:ScopeDef>" +
            $"<tt:ScopeItem>{OnvifSoap.Escape(s)}</tt:ScopeItem></tds:Scopes>"));
        return $"<tds:GetScopesResponse>{items}</tds:GetScopesResponse>";
    }
}
