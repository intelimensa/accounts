namespace Intelimensa.Accounts.Models;

/// <summary>
/// The audit trail of config delivery: one row each time a config's key was wrapped for an install, with
/// what it was issued to and until when it may be used offline. Never holds a key.
/// </summary>
public class ConfigKeyIssuance
{
    public long Id { get; set; }

    public int AccountId { get; set; }

    public Guid ClientInstallId { get; set; }

    public Guid AccountDeviceId { get; set; }

    public int ConfigId { get; set; }

    public required string BlobSha256 { get; set; }

    /// <summary>The build the key was wrapped for.</summary>
    public required string AppVersion { get; set; }

    public required string AppPlatform { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset LeaseNotAfter { get; set; }
}
