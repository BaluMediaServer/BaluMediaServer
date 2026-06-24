using System.Xml.Linq;

namespace BaluMediaServer.Services.Onvif;

/// <summary>
/// SOAP 1.2 helpers for the ONVIF services: XML namespace constants, request-operation parsing,
/// and response envelope / fault construction. ONVIF uses document/literal SOAP, so the operation
/// name is simply the local-name of the first element child of <c>soap:Body</c>.
///
/// Plain C# (System.Xml.Linq only) — no Android dependencies — so it is unit-testable on the host.
/// </summary>
public static class OnvifSoap
{
    // SOAP 1.2
    public const string Soap = "http://www.w3.org/2003/05/soap-envelope";
    // WS-Addressing (used by WS-Discovery)
    public const string Wsa = "http://www.w3.org/2005/08/addressing";
    // WS-Discovery
    public const string Wsdd = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
    // WS-Security
    public const string Wsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    public const string Wsu  = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";
    // ONVIF services
    public const string Tds = "http://www.onvif.org/ver10/device/wsdl";   // device
    public const string Trt = "http://www.onvif.org/ver10/media/wsdl";    // media
    public const string Tt  = "http://www.onvif.org/ver10/schema";        // common schema types
    public const string Tds1 = "http://www.onvif.org/ver10/device/wsdl";
    public const string Dn  = "http://www.onvif.org/ver10/network/wsdl";  // NetworkVideoTransmitter

    private static readonly XNamespace SoapNs = Soap;

    /// <summary>
    /// Parses an incoming SOAP request and returns the operation name (the local-name of the
    /// first element child of the SOAP Body), the Body element, and the Header element (or null).
    /// Returns <c>op = ""</c> when the document has no Body / no operation element.
    /// </summary>
    public static (string Operation, XElement? Body, XElement? Header) ParseOperation(string soapXml)
    {
        if (string.IsNullOrWhiteSpace(soapXml))
            return ("", null, null);

        XDocument doc;
        try { doc = XDocument.Parse(soapXml); }
        catch { return ("", null, null); }

        var envelope = doc.Root;
        if (envelope is null)
            return ("", null, null);

        var header = envelope.Element(SoapNs + "Header");
        var body = envelope.Element(SoapNs + "Body");
        var op = body?.Elements().FirstOrDefault();
        return (op?.Name.LocalName ?? "", body, header);
    }

    /// <summary>
    /// Wraps a response body fragment in a SOAP 1.2 envelope, declaring the ONVIF namespace
    /// prefixes (<c>tds</c>, <c>trt</c>, <c>tt</c>) so the fragment can use them. The fragment
    /// is the inner XML of <c>soap:Body</c> (e.g. <c>&lt;tds:GetDeviceInformationResponse&gt;…</c>).
    /// </summary>
    public static string Envelope(string bodyInnerXml) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        $"<s:Envelope xmlns:s=\"{Soap}\" xmlns:tds=\"{Tds}\" xmlns:trt=\"{Trt}\" xmlns:tt=\"{Tt}\">" +
        "<s:Body>" + bodyInnerXml + "</s:Body></s:Envelope>";

    /// <summary>
    /// Builds a SOAP 1.2 fault envelope. <paramref name="subcode"/> is an optional ONVIF
    /// subcode local-name (e.g. <c>NotAuthorized</c>, <c>ActionNotSupported</c>).
    /// </summary>
    public static string Fault(string reason, string subcode = "")
    {
        var subcodeXml = string.IsNullOrEmpty(subcode)
            ? ""
            : $"<s:Subcode><s:Value>ter:{subcode}</s:Value></s:Subcode>";
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            $"<s:Envelope xmlns:s=\"{Soap}\" xmlns:ter=\"http://www.onvif.org/ver10/error\">" +
            "<s:Body><s:Fault>" +
            $"<s:Code><s:Value>s:Sender</s:Value>{subcodeXml}</s:Code>" +
            $"<s:Reason><s:Text xml:lang=\"en\">{Escape(reason)}</s:Text></s:Reason>" +
            "</s:Fault></s:Body></s:Envelope>";
    }

    /// <summary>XML-escapes a text value for safe inclusion in an element body or attribute.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");
    }
}
