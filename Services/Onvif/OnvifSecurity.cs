using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace BaluMediaServer.Services.Onvif;

/// <summary>
/// Validates ONVIF WS-Security <c>UsernameToken</c> credentials against the server's existing
/// user store. Supports both <c>PasswordDigest</c> (the common case:
/// <c>Base64(SHA1(nonce + created + password))</c>) and <c>PasswordText</c>. Reuses the plaintext
/// passwords held by the RTSP <c>AuthenticationManager</c> via the supplied <c>getPassword</c>
/// delegate — no new credential state.
///
/// Plain C# (no Android deps) so the digest logic is unit-testable on the host.
/// </summary>
public sealed class OnvifSecurity
{
    public const string PasswordDigestType =
        "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";

    private readonly bool _requireAuth;
    private readonly Func<string, string?> _getPassword;

    /// <summary>
    /// Operations callable without authentication. <c>GetSystemDateAndTime</c> is PRE_AUTH per
    /// the ONVIF spec and clients call it first to sync their clock before building the digest.
    /// </summary>
    private static readonly HashSet<string> AnonymousOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetSystemDateAndTime", "GetWsdlUrl",
    };

    /// <param name="requireAuth">When false, all operations are allowed (mirrors <c>ServerConfiguration.AuthRequired</c>).</param>
    /// <param name="getPassword">Returns the plaintext password for a username, or null if unknown.</param>
    public OnvifSecurity(bool requireAuth, Func<string, string?> getPassword)
    {
        _requireAuth = requireAuth;
        _getPassword = getPassword;
    }

    /// <summary>
    /// Returns true if <paramref name="operation"/> may proceed given the SOAP <paramref name="header"/>.
    /// PRE_AUTH operations and (when auth is disabled) everything are always allowed.
    /// </summary>
    public bool IsAuthorized(string operation, XElement? header)
    {
        if (!_requireAuth) return true;
        if (AnonymousOps.Contains(operation)) return true;

        var token = ExtractToken(header);
        if (token is null) return false;

        var password = _getPassword(token.Username);
        if (password is null) return false;

        return Validate(token, password);
    }

    /// <summary>A parsed WS-Security UsernameToken.</summary>
    public sealed record UsernameToken(string Username, string Password, bool IsDigest, string Nonce, string Created);

    /// <summary>
    /// Extracts the <c>UsernameToken</c> from a SOAP header, or null if there isn't one.
    /// Namespace-agnostic on local names so it tolerates the various WS-Security namespace
    /// revisions clients emit.
    /// </summary>
    public static UsernameToken? ExtractToken(XElement? header)
    {
        if (header is null) return null;

        var tokenEl = header.Descendants().FirstOrDefault(e => e.Name.LocalName == "UsernameToken");
        if (tokenEl is null) return null;

        string Local(string name) =>
            tokenEl.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() ?? "";

        var username = Local("Username");
        if (string.IsNullOrEmpty(username)) return null;

        var passwordEl = tokenEl.Elements().FirstOrDefault(e => e.Name.LocalName == "Password");
        var password = passwordEl?.Value?.Trim() ?? "";
        var type = passwordEl?.Attributes().FirstOrDefault(a => a.Name.LocalName == "Type")?.Value ?? "";
        // Default to digest when a Nonce/Created pair is present and no explicit text type is given.
        var nonce = Local("Nonce");
        var created = Local("Created");
        bool isDigest = type.EndsWith("#PasswordDigest", StringComparison.OrdinalIgnoreCase)
                        || (string.IsNullOrEmpty(type) && (nonce.Length > 0 || created.Length > 0));

        return new UsernameToken(username, password, isDigest, nonce, created);
    }

    /// <summary>Validates a parsed token against the user's plaintext password.</summary>
    public static bool Validate(UsernameToken token, string password)
    {
        if (!token.IsDigest)
        {
            // PasswordText — constant-time-ish direct comparison.
            return FixedEquals(token.Password, password);
        }

        byte[] nonceBytes;
        try { nonceBytes = Convert.FromBase64String(token.Nonce); }
        catch { return false; }

        var expected = ComputeDigest(nonceBytes, token.Created, password);
        return FixedEquals(token.Password, expected);
    }

    /// <summary>
    /// Computes the ONVIF password digest: <c>Base64(SHA1(nonce ++ created ++ password))</c>,
    /// where <c>created</c> and <c>password</c> are UTF-8 encoded.
    /// </summary>
    public static string ComputeDigest(byte[] nonce, string created, string password)
    {
        var createdBytes = Encoding.UTF8.GetBytes(created);
        var passwordBytes = Encoding.UTF8.GetBytes(password);

        var buffer = new byte[nonce.Length + createdBytes.Length + passwordBytes.Length];
        Buffer.BlockCopy(nonce, 0, buffer, 0, nonce.Length);
        Buffer.BlockCopy(createdBytes, 0, buffer, nonce.Length, createdBytes.Length);
        Buffer.BlockCopy(passwordBytes, 0, buffer, nonce.Length + createdBytes.Length, passwordBytes.Length);

        var hash = SHA1.HashData(buffer);
        return Convert.ToBase64String(hash);
    }

    private static bool FixedEquals(string a, string b)
    {
        // Length leak is acceptable here; avoid early-out on content.
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
