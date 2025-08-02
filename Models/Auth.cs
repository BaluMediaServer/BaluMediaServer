namespace BaluMediaServer.Models;

public class RtspAuth
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Realm { get; set; }
    public string? Nonce { get; set; }
    public string? Method { get; set; }
    public string? Uri { get; set; }
    public AuthType Type { get; set; }
    public string? RawHeader { get; set; }
    public Dictionary<string, string>? DigestParams { get; set; }
}