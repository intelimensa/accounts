using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Intelimensa.Accounts.ConfigDelivery;

/// <summary>
/// What a lease says. Times are UTC, whole seconds, written as <see cref="OfflineLease.TimeFormat"/>; the
/// response carries them as those exact strings so a client rebuilds the signed text without reformatting
/// a date.
/// </summary>
public record LeaseTerms(
    int AccountId,
    Guid ClientInstallId,
    string SerialNumber,
    string ConfigKey,
    string BlobSha256,
    DateTimeOffset IssuedAt,
    DateTimeOffset NotAfter);

/// <summary>
/// The signed offline lease: until <c>notAfter</c>, this install may use its cached wrapped key for this
/// unit's config without asking the server. ECDSA P-256 / SHA-256, DER signature in base64, over this UTF-8
/// text (each line ends in <c>\n</c>, the last too), in the style of the release statement:
/// <code>
/// intelimensa-config-lease-v1
/// &lt;account id&gt;
/// &lt;client install id, lowercase with dashes&gt;
/// &lt;serial number, dashless&gt;
/// &lt;config key&gt;
/// &lt;blob sha256, lowercase hex&gt;
/// &lt;issued at&gt;
/// &lt;not after&gt;
/// </code>
/// The private key is <c>Configs:LeaseSigningKey</c>; AxoSync embeds the public key. Pinned by
/// <c>testdata/config-delivery-vectors.json</c>.
/// </summary>
public static class OfflineLease
{
    public const string Tag = "intelimensa-config-lease-v1";
    public const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public static string FormatTime(DateTimeOffset time) =>
        time.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);

    public static string Statement(LeaseTerms terms) =>
        $"{Tag}\n{terms.AccountId.ToString(CultureInfo.InvariantCulture)}\n{terms.ClientInstallId:D}\n{terms.SerialNumber}\n" +
        $"{terms.ConfigKey}\n{terms.BlobSha256}\n{FormatTime(terms.IssuedAt)}\n{FormatTime(terms.NotAfter)}\n";

    public static string Sign(ECDsa key, LeaseTerms terms) =>
        Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(Statement(terms)), HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));

    public static bool Verify(string publicKeySpki, string statement, string signature)
    {
        try
        {
            using var key = P256.ImportSigningKey(publicKeySpki);
            return key.VerifyData(Encoding.UTF8.GetBytes(statement), Convert.FromBase64String(signature),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return false;
        }
    }

    /// <summary>Truncates to whole seconds, the precision the statement carries.</summary>
    public static DateTimeOffset ToSeconds(DateTimeOffset time) =>
        new(time.UtcTicks - time.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
}
