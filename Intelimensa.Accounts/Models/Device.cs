namespace Intelimensa.Accounts.Models;

/// <summary>One registered client machine for an <see cref="Account"/>.</summary>
public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public int AccountId { get; set; }

    public Account? Account { get; set; }

    /// <summary>
    /// Populated later if/when device-key wrapping is built; the baseline envelope-encryption
    /// transit model (trust TLS + authenticated session) doesn't need it yet.
    /// </summary>
    public string? PublicKey { get; set; }

    public DevicePlatform Platform { get; set; }

    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastSeenAt { get; set; }

    public bool Revoked { get; set; }
}
