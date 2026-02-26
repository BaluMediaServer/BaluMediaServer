namespace BaluMediaServer.Models;

/// <summary>
/// Represents authentication credentials and parameters for RTSP authentication.
/// Supports both Basic and Digest authentication schemes.
/// </summary>
public class RtspAuth
{
    /// <summary>
    /// Gets or sets the username provided by the client.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Gets or sets the password provided by the client (for Basic auth) or the response hash (for Digest auth).
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets the authentication realm.
    /// </summary>
    public string? Realm { get; set; }

    /// <summary>
    /// Gets or sets the server-generated nonce for Digest authentication.
    /// </summary>
    public string? Nonce { get; set; }

    /// <summary>
    /// Gets or sets the HTTP/RTSP method used in Digest authentication calculation.
    /// </summary>
    public string? Method { get; set; }

    /// <summary>
    /// Gets or sets the URI used in Digest authentication calculation.
    /// </summary>
    public string? Uri { get; set; }

    /// <summary>
    /// Gets or sets the authentication type (None, Basic, or Digest).
    /// </summary>
    public AuthType Type { get; set; }

    /// <summary>
    /// Gets or sets the raw Authorization header value for debugging purposes.
    /// </summary>
    public string? RawHeader { get; set; }

    /// <summary>
    /// Gets or sets the parsed Digest authentication parameters.
    /// Contains key-value pairs like response, nc, cnonce, qop, etc.
    /// </summary>
    public Dictionary<string, string>? DigestParams { get; set; }
}
