using System.Xml.Linq;
using BaluMediaServer.Services.Onvif;
using FluentAssertions;
using Xunit;

namespace BaluMediaServer.Tests.Unit.Services.Onvif;

/// <summary>
/// Unit tests for the Android-free ONVIF protocol logic: SOAP parsing/enveloping, WS-UsernameToken
/// digest validation, Device/Media response building, and WS-Discovery messages. The transport
/// shells (OnvifServer/WsDiscoveryService) are platform code and are exercised manually on-device.
/// </summary>
public class OnvifSoapTests
{
    private const string GetProfilesRequest =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" +
        "<s:Header><a:Action xmlns:a=\"http://www.w3.org/2005/08/addressing\">x</a:Action></s:Header>" +
        "<s:Body><trt:GetProfiles xmlns:trt=\"http://www.onvif.org/ver10/media/wsdl\"/></s:Body>" +
        "</s:Envelope>";

    [Fact]
    public void ParseOperation_returns_body_operation_local_name()
    {
        var (op, body, header) = OnvifSoap.ParseOperation(GetProfilesRequest);
        op.Should().Be("GetProfiles");
        body.Should().NotBeNull();
        header.Should().NotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body/></s:Envelope>")]
    public void ParseOperation_returns_empty_for_invalid_or_bodyless(string xml)
    {
        var (op, _, _) = OnvifSoap.ParseOperation(xml);
        op.Should().BeEmpty();
    }

    [Fact]
    public void Envelope_is_well_formed_and_wraps_fragment()
    {
        var xml = OnvifSoap.Envelope("<tds:GetScopesResponse/>");
        var doc = XDocument.Parse(xml); // throws if malformed
        doc.Root!.Name.LocalName.Should().Be("Envelope");
        doc.Descendants().Should().Contain(e => e.Name.LocalName == "GetScopesResponse");
    }

    [Fact]
    public void Fault_is_well_formed_and_carries_subcode()
    {
        var xml = OnvifSoap.Fault("Sender not authorized", "NotAuthorized");
        var doc = XDocument.Parse(xml);
        doc.Descendants().Should().Contain(e => e.Name.LocalName == "Fault");
        xml.Should().Contain("NotAuthorized");
    }

    [Fact]
    public void Escape_encodes_xml_metacharacters()
        => OnvifSoap.Escape("a&b<c>\"").Should().Be("a&amp;b&lt;c&gt;&quot;");
}

public class OnvifSecurityTests
{
    // Known-answer vector computed independently (python hashlib): SHA1(nonce ++ created ++ password).
    private const string NonceB64 = "AAAAAAAAAAAAAAAAAAAAAA=="; // 16 zero bytes
    private const string Created = "2024-01-01T00:00:00Z";
    private const string Password = "test123";
    private const string ExpectedDigest = "O+Js2nw+ZaqHKaPru8MXp/b4PJ4=";

    [Fact]
    public void ComputeDigest_matches_known_vector()
    {
        var digest = OnvifSecurity.ComputeDigest(new byte[16], Created, Password);
        digest.Should().Be(ExpectedDigest);
    }

    [Fact]
    public void Validate_accepts_correct_digest_and_rejects_wrong_password()
    {
        var token = new OnvifSecurity.UsernameToken("admin", ExpectedDigest, IsDigest: true, NonceB64, Created);
        OnvifSecurity.Validate(token, Password).Should().BeTrue();
        OnvifSecurity.Validate(token, "wrong").Should().BeFalse();
    }

    [Fact]
    public void Validate_handles_plaintext_password()
    {
        var token = new OnvifSecurity.UsernameToken("admin", "secret", IsDigest: false, "", "");
        OnvifSecurity.Validate(token, "secret").Should().BeTrue();
        OnvifSecurity.Validate(token, "nope").Should().BeFalse();
    }

    [Fact]
    public void IsAuthorized_allows_GetSystemDateAndTime_without_token()
    {
        var sec = new OnvifSecurity(requireAuth: true, getPassword: _ => Password);
        sec.IsAuthorized("GetSystemDateAndTime", header: null).Should().BeTrue();
    }

    [Fact]
    public void IsAuthorized_denies_protected_op_without_token()
    {
        var sec = new OnvifSecurity(requireAuth: true, getPassword: _ => Password);
        sec.IsAuthorized("GetProfiles", header: null).Should().BeFalse();
    }

    [Fact]
    public void IsAuthorized_accepts_protected_op_with_valid_digest()
    {
        var sec = new OnvifSecurity(requireAuth: true, getPassword: u => u == "admin" ? Password : null);
        sec.IsAuthorized("GetProfiles", BuildSecurityHeader("admin", ExpectedDigest, NonceB64, Created)).Should().BeTrue();
    }

    [Fact]
    public void IsAuthorized_allows_everything_when_auth_disabled()
    {
        var sec = new OnvifSecurity(requireAuth: false, getPassword: _ => null);
        sec.IsAuthorized("GetProfiles", header: null).Should().BeTrue();
    }

    private static XElement BuildSecurityHeader(string user, string passwordDigest, string nonceB64, string created)
    {
        XNamespace s = OnvifSoap.Soap;
        XNamespace wsse = OnvifSoap.Wsse;
        XNamespace wsu = OnvifSoap.Wsu;
        return new XElement(s + "Header",
            new XElement(wsse + "Security",
                new XElement(wsse + "UsernameToken",
                    new XElement(wsse + "Username", user),
                    new XElement(wsse + "Password", new XAttribute("Type", OnvifSecurity.PasswordDigestType), passwordDigest),
                    new XElement(wsse + "Nonce", nonceB64),
                    new XElement(wsu + "Created", created))));
    }
}

public class OnvifDeviceServiceTests
{
    private static OnvifDeviceContext Ctx() => OnvifTestData.Ctx();

    [Fact]
    public void GetDeviceInformation_includes_identity()
    {
        var inner = new OnvifDeviceService(Ctx()).Handle("GetDeviceInformation", DateTime.UtcNow)!;
        inner.Should().Contain("Balu").And.Contain("SN123").And.Contain("balu-1");
        XDocument.Parse(OnvifSoap.Envelope(inner)); // well-formed
    }

    [Fact]
    public void GetSystemDateAndTime_reflects_supplied_time()
    {
        var inner = new OnvifDeviceService(Ctx()).Handle("GetSystemDateAndTime", new DateTime(2031, 7, 2, 13, 5, 9, DateTimeKind.Utc))!;
        inner.Should().Contain("<tt:Year>2031</tt:Year>").And.Contain("<tt:Hour>13</tt:Hour>");
    }

    [Fact]
    public void Handle_returns_null_for_unsupported_operation()
        => new OnvifDeviceService(Ctx()).Handle("Reboot", DateTime.UtcNow).Should().BeNull();

    [Fact]
    public void Scopes_include_model_name_and_streaming_profile()
    {
        var scopes = OnvifDeviceService.BuildScopes(Ctx());
        scopes.Should().Contain("onvif://www.onvif.org/Profile/Streaming");
        scopes.Should().Contain(s => s.Contains("/name/BaluMediaServer"));
    }
}

public class OnvifMediaServiceTests
{
    private static OnvifMediaService Media() =>
        new(OnvifTestData.Ctx(), () => OnvifTestData.Profiles());

    private static XElement Body(string innerXml) => XElement.Parse($"<Body>{innerXml}</Body>");

    [Fact]
    public void GetStreamUri_composes_rtsp_url_for_profile()
    {
        var inner = Media().Handle("GetStreamUri", Body("<GetStreamUri><ProfileToken>Profile_back</ProfileToken></GetStreamUri>"))!;
        inner.Should().Contain("rtsp://192.168.1.50:7778/live/back");
        XDocument.Parse(OnvifSoap.Envelope(inner));
    }

    [Fact]
    public void GetSnapshotUri_composes_http_url_for_profile()
    {
        var inner = Media().Handle("GetSnapshotUri", Body("<GetSnapshotUri><ProfileToken>Profile_front</ProfileToken></GetSnapshotUri>"))!;
        inner.Should().Contain("http://192.168.1.50:8089/snapshot/front.jpg");
    }

    [Fact]
    public void GetStreamUri_returns_null_for_unknown_token()
        => Media().Handle("GetStreamUri", Body("<GetStreamUri><ProfileToken>nope</ProfileToken></GetStreamUri>")).Should().BeNull();

    [Fact]
    public void GetProfiles_emits_one_profile_per_camera_with_resolution()
    {
        var inner = Media().Handle("GetProfiles", null)!;
        var doc = XDocument.Parse(OnvifSoap.Envelope(inner));
        var profiles = doc.Descendants().Where(e => e.Name.LocalName == "Profiles").ToList();
        profiles.Should().HaveCount(2);
        inner.Should().Contain("token=\"Profile_back\"").And.Contain("token=\"Profile_front\"");
        inner.Should().Contain("<tt:Width>1920</tt:Width>").And.Contain("<tt:Height>1080</tt:Height>");
    }

    [Fact]
    public void GetVideoEncoderConfiguration_matches_by_config_token()
    {
        var inner = Media().Handle("GetVideoEncoderConfiguration",
            Body("<GetVideoEncoderConfiguration><ConfigurationToken>VideoEncoderConfig_back</ConfigurationToken></GetVideoEncoderConfiguration>"))!;
        inner.Should().Contain("VideoEncoderConfig_back").And.Contain("<tt:Encoding>H264</tt:Encoding>");
    }
}

public class WsDiscoveryMessagesTests
{
    private static readonly string[] Scopes =
    {
        "onvif://www.onvif.org/Profile/Streaming",
        "onvif://www.onvif.org/name/BaluMediaServer",
    };

    [Fact]
    public void ProbeMatch_contains_types_scopes_xaddr_and_relatesto()
    {
        var xml = WsDiscoveryMessages.BuildProbeMatch(
            deviceUuid: "1111-2222",
            xaddr: "http://192.168.1.50:8090/onvif/device_service",
            scopes: Scopes,
            relatesToMessageId: "urn:uuid:probe-id",
            responseMessageId: "resp-id");

        var doc = XDocument.Parse(xml); // well-formed
        xml.Should().Contain("NetworkVideoTransmitter");
        xml.Should().Contain("http://192.168.1.50:8090/onvif/device_service");
        xml.Should().Contain("onvif://www.onvif.org/Profile/Streaming");
        doc.Descendants().Should().Contain(e => e.Name.LocalName == "RelatesTo" && e.Value == "urn:uuid:probe-id");
        doc.Descendants().Should().Contain(e => e.Name.LocalName == "ProbeMatch");
    }

    [Fact]
    public void Hello_and_Bye_carry_their_actions()
    {
        WsDiscoveryMessages.BuildHello("u", "x", Scopes, "m").Should().Contain(WsDiscoveryMessages.ActionHello);
        WsDiscoveryMessages.BuildBye("u", "x", Scopes, "m").Should().Contain(WsDiscoveryMessages.ActionBye);
    }

    [Fact]
    public void Parse_extracts_action_and_message_id_from_probe()
    {
        var probe =
            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:a=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\">" +
            "<s:Header><a:MessageID>urn:uuid:abc</a:MessageID>" +
            $"<a:Action>{WsDiscoveryMessages.ActionProbe}</a:Action></s:Header>" +
            "<s:Body/></s:Envelope>";
        var (action, messageId) = WsDiscoveryMessages.Parse(probe);
        action.Should().Be(WsDiscoveryMessages.ActionProbe);
        messageId.Should().Be("urn:uuid:abc");
    }
}

/// <summary>Shared fixtures for the ONVIF service tests.</summary>
internal static class OnvifTestData
{
    public static OnvifDeviceContext Ctx() => new()
    {
        Manufacturer = "Balu",
        Model = "BaluMediaServer",
        FirmwareVersion = "1.6.0",
        SerialNumber = "SN123",
        HardwareId = "balu-1",
        RtspPort = 7778,
        MjpegPort = 8089,
        OnvifPort = 8090,
        GetIpAddress = () => "192.168.1.50",
    };

    public static IReadOnlyList<OnvifProfile> Profiles() => new[]
    {
        new OnvifProfile
        {
            Token = "Profile_back", Name = "BackCamera", CameraKey = "back",
            Width = 1920, Height = 1080, Fps = 30, BitrateKbps = 4000,
            RtspPath = "/live/back", SnapshotPath = "/snapshot/back.jpg",
        },
        new OnvifProfile
        {
            Token = "Profile_front", Name = "FrontCamera", CameraKey = "front",
            Width = 1280, Height = 720, Fps = 30, BitrateKbps = 2000,
            RtspPath = "/live/front", SnapshotPath = "/snapshot/front.jpg",
        },
    };
}
