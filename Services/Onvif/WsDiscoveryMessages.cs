using System.Xml.Linq;

namespace BaluMediaServer.Services.Onvif;

/// <summary>
/// Builds and parses WS-Discovery (2005/04) SOAP messages for ONVIF auto-discovery: Hello (sent
/// on startup), Bye (sent on shutdown), and ProbeMatch (sent in reply to a client Probe). Plain C#
/// (System.Xml.Linq only) — no Android deps — so the message content is unit-testable on the host.
/// The <see cref="WsDiscoveryService"/> shell handles the UDP multicast transport.
/// </summary>
public static class WsDiscoveryMessages
{
    public const string Soap = "http://www.w3.org/2003/05/soap-envelope";
    public const string Wsa = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    public const string Disco = "http://schemas.xmlsoap.org/ws/2005/04/discovery";

    public const string DiscoveryTo = "urn:schemas-xmlsoap-org:ws:2005:04:discovery";
    public const string AnonymousTo = "http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous";

    public const string ActionProbe        = Disco + "/Probe";
    public const string ActionProbeMatches = Disco + "/ProbeMatches";
    public const string ActionHello        = Disco + "/Hello";
    public const string ActionBye          = Disco + "/Bye";

    /// <summary>ONVIF device types advertised in discovery (NVT = Network Video Transmitter).</summary>
    public const string DeviceTypes = "dn:NetworkVideoTransmitter tds:Device";

    /// <summary>Builds a Hello message announcing the device (multicast on startup).</summary>
    public static string BuildHello(string deviceUuid, string xaddr, IReadOnlyList<string> scopes, string messageId) =>
        Envelope(ActionHello, DiscoveryTo, messageId, relatesTo: null,
            $"<d:Hello>{EndpointBody(deviceUuid, xaddr, scopes)}</d:Hello>");

    /// <summary>Builds a Bye message announcing the device is leaving (multicast on shutdown).</summary>
    public static string BuildBye(string deviceUuid, string xaddr, IReadOnlyList<string> scopes, string messageId) =>
        Envelope(ActionBye, DiscoveryTo, messageId, relatesTo: null,
            $"<d:Bye>{EndpointBody(deviceUuid, xaddr, scopes)}</d:Bye>");

    /// <summary>Builds a ProbeMatches reply to a client Probe (unicast back to the prober).</summary>
    public static string BuildProbeMatch(string deviceUuid, string xaddr, IReadOnlyList<string> scopes, string relatesToMessageId, string responseMessageId) =>
        Envelope(ActionProbeMatches, AnonymousTo, responseMessageId, relatesTo: relatesToMessageId,
            $"<d:ProbeMatches><d:ProbeMatch>{EndpointBody(deviceUuid, xaddr, scopes)}</d:ProbeMatch></d:ProbeMatches>");

    /// <summary>
    /// Parses an incoming discovery datagram, returning the WS-Addressing Action and MessageID.
    /// Used by the service to recognise a Probe and echo its MessageID in the ProbeMatch RelatesTo.
    /// </summary>
    public static (string Action, string MessageId) Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return ("", "");
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return ("", ""); }

        var header = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Header");
        string Field(string name) => header?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() ?? "";
        return (Field("Action"), Field("MessageID"));
    }

    private static string EndpointBody(string deviceUuid, string xaddr, IReadOnlyList<string> scopes) =>
        $"<a:EndpointReference><a:Address>urn:uuid:{OnvifSoap.Escape(deviceUuid)}</a:Address></a:EndpointReference>" +
        $"<d:Types>{DeviceTypes}</d:Types>" +
        $"<d:Scopes>{OnvifSoap.Escape(string.Join(" ", scopes))}</d:Scopes>" +
        $"<d:XAddrs>{OnvifSoap.Escape(xaddr)}</d:XAddrs>" +
        "<d:MetadataVersion>1</d:MetadataVersion>";

    private static string Envelope(string action, string to, string messageId, string? relatesTo, string body)
    {
        var relatesXml = string.IsNullOrEmpty(relatesTo) ? "" : $"<a:RelatesTo>{OnvifSoap.Escape(relatesTo)}</a:RelatesTo>";
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            $"<s:Envelope xmlns:s=\"{Soap}\" xmlns:a=\"{Wsa}\" xmlns:d=\"{Disco}\" " +
            $"xmlns:dn=\"{OnvifSoap.Dn}\" xmlns:tds=\"{OnvifSoap.Tds}\">" +
            "<s:Header>" +
            $"<a:MessageID>urn:uuid:{OnvifSoap.Escape(messageId)}</a:MessageID>" +
            relatesXml +
            $"<a:To>{OnvifSoap.Escape(to)}</a:To>" +
            $"<a:Action>{action}</a:Action>" +
            "</s:Header>" +
            $"<s:Body>{body}</s:Body></s:Envelope>";
    }
}
