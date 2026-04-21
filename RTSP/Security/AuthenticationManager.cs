using System.Collections.Concurrent;
using System.Text;
using Android.Util;
using BaluMediaServer.Models;

namespace BaluMediaServer.RTSP.Security;

/// <summary>
/// Manages RTSP authentication using Basic and Digest schemes.
/// Handles nonce generation, validation, and credential verification.
/// </summary>
public class AuthenticationManager : IAuthenticationManager
{
    private readonly ConcurrentDictionary<string, string> _users = new();
    private readonly ConcurrentDictionary<string, string> _nonceCache = new();
    private readonly TimeSpan _nonceExpiry = TimeSpan.FromMinutes(5);

    /// <inheritdoc/>
    public bool RequireAuthentication { get; set; } = true;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> Users => _users;

    /// <inheritdoc/>
    public bool IsAuthenticated(RtspRequest request)
    {
        if (!RequireAuthentication)
            return true;

        if (request.Auth == null)
            return false;

        return ValidateCredentials(request.Auth);
    }

    /// <inheritdoc/>
    public bool ValidateCredentials(RtspAuth auth)
    {
        if (auth == null || string.IsNullOrEmpty(auth.Username))
            return false;

        // Check if user exists
        if (!_users.TryGetValue(auth.Username, out var validPassword))
            return false;

        if (auth.Type == AuthType.Basic)
        {
            return auth.Password == validPassword;
        }
        else if (auth.Type == AuthType.Digest)
        {
            return ValidateDigestAuth(auth, validPassword);
        }

        return false;
    }

    /// <inheritdoc/>
    public string GenerateNonce()
    {
        var bytes = new byte[16];
        using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        var nonce = Convert.ToBase64String(bytes);

        // Store nonce with timestamp
        if (!_nonceCache.ContainsKey(nonce))
            _nonceCache[nonce] = DateTime.UtcNow.Add(_nonceExpiry).ToString("O");

        // Clean old nonces
        CleanExpiredNonces();

        return nonce;
    }

    /// <inheritdoc/>
    public async Task SendAuthenticationRequiredAsync(StreamWriter writer, int cseq)
    {
        var nonce = GenerateNonce();

        await writer.WriteLineAsync("RTSP/1.0 401 Unauthorized").ConfigureAwait(false);
        await writer.WriteLineAsync($"CSeq: {cseq}").ConfigureAwait(false);
        await writer.WriteLineAsync($"WWW-Authenticate: Digest realm=\"RTSP Server\", nonce=\"{nonce}\", algorithm=MD5").ConfigureAwait(false);
        await writer.WriteLineAsync("Allow: OPTIONS, DESCRIBE, SETUP, PLAY, TEARDOWN").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public bool AddUser(string username, string password) => _users.TryAdd(username, password);

    /// <inheritdoc/>
    public bool RemoveUser(string username) => _users.TryRemove(username, out _);

    /// <inheritdoc/>
    public bool UpdateUser(string username, string password) => RemoveUser(username) && AddUser(username, password);

    /// <inheritdoc/>
    public RtspAuth? ParseAuthorization(string authHeader, string method, string uri)
    {
        if (string.IsNullOrWhiteSpace(authHeader))
            return null;

        var auth = new RtspAuth
        {
            Method = method,
            Uri = uri,
            RawHeader = authHeader
        };

        if (authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            auth.Type = AuthType.Basic;
            var base64 = authHeader.Substring(6).Trim();

            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                var colonIndex = decoded.IndexOf(':');
                if (colonIndex > 0)
                {
                    auth.Username = decoded.Substring(0, colonIndex);
                    auth.Password = decoded.Substring(colonIndex + 1);
                }
            }
            catch (Exception ex)
            {
                BaluLogger.Error("[AuthManager]", $"Failed to decode Basic auth: {ex.Message}");
                return null;
            }
        }
        else if (authHeader.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
        {
            auth.Type = AuthType.Digest;
            var digestParams = ParseDigestAuth(authHeader.Substring(7));
            auth.DigestParams = digestParams;

            auth.Username = digestParams.GetValueOrDefault("username")?.Trim('"');
            auth.Realm = digestParams.GetValueOrDefault("realm")?.Trim('"');
            auth.Nonce = digestParams.GetValueOrDefault("nonce")?.Trim('"');
        }

        return auth;
    }

    private Dictionary<string, string> ParseDigestAuth(string digest)
    {
        var parameters = new Dictionary<string, string>();
        var parts = digest.Split(',');

        foreach (var part in parts)
        {
            var eqIndex = part.IndexOf('=');
            if (eqIndex > 0)
            {
                var key = part.Substring(0, eqIndex).Trim();
                var value = part.Substring(eqIndex + 1).Trim();
                parameters[key] = value;
            }
        }

        return parameters;
    }

    private bool ValidateDigestAuth(RtspAuth auth, string password)
    {
        if (string.IsNullOrEmpty(auth.Username) || string.IsNullOrEmpty(auth.Nonce))
            return false;

        try
        {
            // Check if this is a nonce we generated
            if (!_nonceCache.ContainsKey(auth.Nonce))
            {
                BaluLogger.Debug("[AuthManager]", $"Unknown nonce: {auth.Nonce}");
                return false;
            }

            // Check if nonce is expired
            if (!IsNonceValid(auth.Nonce))
            {
                BaluLogger.Debug("[AuthManager]", "Nonce expired");
                _nonceCache.TryRemove(auth.Nonce, out _);
                return false;
            }

            // Get the response from the parsed digest parameters
            var providedResponse = auth.DigestParams?.GetValueOrDefault("response")?.Trim('"');

            if (string.IsNullOrEmpty(providedResponse))
            {
                BaluLogger.Debug("[AuthManager]", "No response in digest auth");
                return false;
            }

            // Calculate expected response
            using var md5 = System.Security.Cryptography.MD5.Create();

            // HA1 = MD5(username:realm:password)
            var realm = auth.Realm ?? "RTSP Server";
            var ha1Input = $"{auth.Username}:{realm}:{password}";
            var ha1Bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(ha1Input));
            var ha1 = BitConverter.ToString(ha1Bytes).Replace("-", "").ToLower();

            // HA2 = MD5(method:uri)
            var ha2Input = $"{auth.Method}:{auth.Uri}";
            var ha2Bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(ha2Input));
            var ha2 = BitConverter.ToString(ha2Bytes).Replace("-", "").ToLower();

            // Check if qop is present
            var qop = auth.DigestParams?.GetValueOrDefault("qop")?.Trim('"');
            var nc = auth.DigestParams?.GetValueOrDefault("nc")?.Trim('"');
            var cnonce = auth.DigestParams?.GetValueOrDefault("cnonce")?.Trim('"');

            string expectedResponse;
            if (!string.IsNullOrEmpty(qop) && qop == "auth")
            {
                // With qop: Response = MD5(HA1:nonce:nc:cnonce:qop:HA2)
                var responseInput = $"{ha1}:{auth.Nonce}:{nc}:{cnonce}:{qop}:{ha2}";
                var responseBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(responseInput));
                expectedResponse = BitConverter.ToString(responseBytes).Replace("-", "").ToLower();
            }
            else
            {
                // Without qop: Response = MD5(HA1:nonce:HA2)
                var responseInput = $"{ha1}:{auth.Nonce}:{ha2}";
                var responseBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(responseInput));
                expectedResponse = BitConverter.ToString(responseBytes).Replace("-", "").ToLower();
            }

            var isValid = expectedResponse.Equals(providedResponse, StringComparison.OrdinalIgnoreCase);

            if (isValid)
            {
                BaluLogger.Debug("[AuthManager]", $"User {auth.Username} authenticated successfully");
            }
            else
            {
                BaluLogger.Debug("[AuthManager]", $"Authentication failed for user {auth.Username}");
            }

            return isValid;
        }
        catch (Exception ex)
        {
            BaluLogger.Error("[AuthManager]", $"Error validating digest auth: {ex.Message}");
            return false;
        }
    }

    private bool IsNonceValid(string nonce)
    {
        if (!_nonceCache.TryGetValue(nonce, out var expiryStr))
            return false;

        if (DateTimeOffset.TryParse(expiryStr, out var expiry))
        {
            return DateTimeOffset.UtcNow < expiry;
        }
        return false;
    }

    private void CleanExpiredNonces()
    {
        var now = DateTime.UtcNow;
        var expiredNonces = _nonceCache
            .Where(kvp => DateTime.TryParse(kvp.Value, out var expiry) && expiry < now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var nonce in expiredNonces)
            _nonceCache.TryRemove(nonce, out _);
    }
}
