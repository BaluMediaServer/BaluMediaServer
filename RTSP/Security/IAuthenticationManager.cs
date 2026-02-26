using BaluMediaServer.Models;

namespace BaluMediaServer.RTSP.Security;

/// <summary>
/// Interface for handling RTSP authentication (Basic and Digest).
/// </summary>
public interface IAuthenticationManager
{
    /// <summary>
    /// Checks if a request is authenticated.
    /// </summary>
    /// <param name="request">The RTSP request.</param>
    /// <returns>True if authenticated or auth not required.</returns>
    bool IsAuthenticated(RtspRequest request);

    /// <summary>
    /// Validates user credentials.
    /// </summary>
    /// <param name="auth">The authentication data.</param>
    /// <returns>True if credentials are valid.</returns>
    bool ValidateCredentials(RtspAuth auth);

    /// <summary>
    /// Generates a new nonce for Digest authentication.
    /// </summary>
    /// <returns>The generated nonce.</returns>
    string GenerateNonce();

    /// <summary>
    /// Sends an authentication required response.
    /// </summary>
    /// <param name="writer">The stream writer.</param>
    /// <param name="cseq">The CSeq number.</param>
    Task SendAuthenticationRequiredAsync(StreamWriter writer, int cseq);

    /// <summary>
    /// Adds a user for authentication.
    /// </summary>
    /// <param name="username">The username.</param>
    /// <param name="password">The password.</param>
    /// <returns>True if user was added.</returns>
    bool AddUser(string username, string password);

    /// <summary>
    /// Removes a user from authentication.
    /// </summary>
    /// <param name="username">The username.</param>
    /// <returns>True if user was removed.</returns>
    bool RemoveUser(string username);

    /// <summary>
    /// Updates an existing user's password.
    /// </summary>
    /// <param name="username">The username.</param>
    /// <param name="password">The new password.</param>
    /// <returns>True if user was updated.</returns>
    bool UpdateUser(string username, string password);

    /// <summary>
    /// Parses an Authorization header.
    /// </summary>
    /// <param name="authHeader">The Authorization header value.</param>
    /// <param name="method">The HTTP method.</param>
    /// <param name="uri">The request URI.</param>
    /// <returns>The parsed authentication data.</returns>
    RtspAuth? ParseAuthorization(string authHeader, string method, string uri);

    /// <summary>
    /// Gets or sets whether authentication is required.
    /// </summary>
    bool RequireAuthentication { get; set; }

    /// <summary>
    /// Gets the user dictionary.
    /// </summary>
    IReadOnlyDictionary<string, string> Users { get; }
}
