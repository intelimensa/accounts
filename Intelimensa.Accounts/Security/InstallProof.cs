using System.Security.Cryptography;
using System.Text;
using Intelimensa.Accounts.ConfigDelivery;

namespace Intelimensa.Accounts.Security;

/// <summary>
/// Proof that a request comes from a registered AxoSync install: an ECDSA P-256 / SHA-256 signature by the
/// install's signing key, DER then base64 in the <c>X-Install-Proof</c> header, over this UTF-8 text (each
/// line ends in <c>\n</c>, the last too):
/// <code>
/// intelimensa-install-proof-v1
/// &lt;HTTP method, upper case&gt;
/// &lt;path and query, exactly as sent&gt;
/// &lt;lowercase hex SHA-256 of the body (of nothing, when there is none)&gt;
/// &lt;nonce, from X-Install-Nonce&gt;
/// </code>
/// The nonce is one the server issued (<see cref="InstallNonces"/>), sent back in the request's own
/// <c>X-Install-Nonce</c> header, so the client's clock plays no part. Pinned by
/// <c>testdata/config-delivery-vectors.json</c>.
/// </summary>
public static class InstallProof
{
    public const string Tag = "intelimensa-install-proof-v1";
    public const string ProofHeader = "X-Install-Proof";
    public const string NonceHeader = "X-Install-Nonce";

    public static string Statement(string method, string pathAndQuery, string bodySha256, string nonce) =>
        $"{Tag}\n{method.ToUpperInvariant()}\n{pathAndQuery}\n{bodySha256}\n{nonce}\n";

    public static string BodySha256(ReadOnlySpan<byte> body) =>
        Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();

    public static bool Verify(string signingKeySpki, string statement, string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature)) return false;
        try
        {
            using var key = P256.ImportSigningKey(signingKeySpki);
            return key.VerifyData(Encoding.UTF8.GetBytes(statement), Convert.FromBase64String(signature.Trim()),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks a request's proof against a signing key: the nonce must be one this server issued and still
    /// fresh, and the signature must cover this method, path, body and nonce. The body must be buffered
    /// (<see cref="InstallNonceMiddleware"/> does that for every API request carrying a proof).
    /// </summary>
    public static async Task<ProofCheck> CheckAsync(HttpRequest request, string signingKeySpki, InstallNonces nonces)
    {
        var signature = request.Headers[ProofHeader].ToString();
        var nonce = request.Headers[NonceHeader].ToString();
        if (signature.Length == 0 || nonce.Length == 0) return ProofCheck.Missing;
        if (!nonces.IsValid(nonce, DateTimeOffset.UtcNow)) return ProofCheck.StaleNonce;

        var statement = Statement(request.Method, request.Path.ToUriComponent() + request.QueryString.ToUriComponent(),
            await ReadBodySha256Async(request), nonce);
        return Verify(signingKeySpki, statement, signature) ? ProofCheck.Valid : ProofCheck.Invalid;
    }

    private static async Task<string> ReadBodySha256Async(HttpRequest request)
    {
        if (!request.Body.CanSeek) return BodySha256([]);
        request.Body.Position = 0;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[16384];
        int read;
        while ((read = await request.Body.ReadAsync(buffer)) > 0)
            sha.AppendData(buffer, 0, read);
        request.Body.Position = 0;
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}

public enum ProofCheck { Valid, Missing, StaleNonce, Invalid }

public static class ProofCheckExtensions
{
    /// <summary>The <c>error</c> code a client gets for a failed check.</summary>
    public static string ErrorCode(this ProofCheck check) => check switch
    {
        ProofCheck.Missing => "install_proof_required",
        ProofCheck.StaleNonce => "install_nonce_stale",
        _ => "install_proof_invalid",
    };
}
