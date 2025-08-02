namespace BaluMediaServer.Models;

public class RtspRequest
{
    public string Method { get; set; } = string.Empty;
    public string Uri { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public Dictionary<string, string> Headers { get; set; } = new();
    public string Body { get; set; } = string.Empty;
    public int CSeq => Headers.ContainsKey("CSeq") ? int.Parse(Headers["CSeq"]) : 0;
    public RtspAuth? Auth { get; set; }
}