using System.Security.Cryptography;
using System.Text;
using Intelimensa.Accounts.Models;

namespace Intelimensa.Accounts.Publishing;

/// <summary>
/// The offline signature that lets AxoSync trust a build without trusting this server. The
/// publisher signs a canonical statement (version, platform, SHA-256, size) with a private key that
/// never leaves their machine; the matching public key is compiled into AxoSync. A compromised
/// server can then withhold updates but cannot make clients install something it made up.
/// </summary>
/// <remarks>
/// The statement text and the algorithm (ECDSA P-256, SHA-256, DER-encoded signature, base64) are a
/// contract with the AxoSync updater and the publish script -- change all three together, and
/// bump the <c>v1</c> tag.
/// </remarks>
public static class ReleaseSigning
{
    public static string Statement(string version, ReleasePlatform platform, string sha256, long sizeBytes) =>
        $"intelimensa-axosync-release-v1\n{version}\n{platform}\n{sha256}\n{sizeBytes}\n";

    /// <summary>Verifies against a base64 SubjectPublicKeyInfo (what <c>openssl pkey -pubout -outform DER</c> emits).</summary>
    public static bool Verify(string publicKeyBase64, string statement, string signatureBase64)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            return key.VerifyData(
                Encoding.UTF8.GetBytes(statement),
                Convert.FromBase64String(signatureBase64),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
