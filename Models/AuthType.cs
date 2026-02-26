namespace BaluMediaServer.Models;

/// <summary>
/// Specifies the authentication type used for RTSP client connections.
/// </summary>
public enum AuthType
{
    /// <summary>
    /// No authentication required.
    /// </summary>
    None,

    /// <summary>
    /// Basic authentication with Base64-encoded credentials.
    /// Less secure as credentials are easily decoded.
    /// </summary>
    Basic,

    /// <summary>
    /// Digest authentication with MD5 hash-based challenge-response.
    /// More secure than Basic as passwords are never sent in plain text.
    /// </summary>
    Digest
}
