namespace BaluMediaServer.Models;

/// <summary>
/// Represents a parsed RTSP request from a client.
/// Contains the method, URI, headers, and authentication information.
/// </summary>
public class RtspRequest
{
    /// <summary>
    /// Gets or sets the RTSP method (e.g., OPTIONS, DESCRIBE, SETUP, PLAY, TEARDOWN).
    /// </summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the request URI (e.g., rtsp://server:port/live/back).
    /// </summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the RTSP protocol version (e.g., RTSP/1.0).
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the dictionary of request headers.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>
    /// Gets or sets the request body content.
    /// </summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Gets the sequence number from the CSeq header.
    /// Returns 0 if the CSeq header is not present.
    /// </summary>
    public int CSeq => Headers.ContainsKey("CSeq") ? int.Parse(Headers["CSeq"]) : 0;

    /// <summary>
    /// Gets or sets the parsed authentication information from the Authorization header.
    /// </summary>
    public RtspAuth? Auth { get; set; }
}
