using System.Security.Cryptography;
using System.Text;
using Intelimensa.Accounts.Models;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.Identity;

namespace Intelimensa.Accounts.Security;

/// <summary>
/// Replaces ASP.NET Core Identity's default PBKDF2 hasher with Argon2id, per CLAUDE.md's
/// explicit requirement. Encodes cost parameters into the stored hash string so they can be
/// tuned later without invalidating existing hashes -- <see cref="VerifyHashedPassword"/> flags
/// hashes stored under stale parameters as needing a rehash.
/// </summary>
public class Argon2idPasswordHasher : IPasswordHasher<ApplicationUser>
{
    private const int SaltSize = 16;
    private const int HashSize = 32;

    // Cost parameters -- RFC 9106's second recommended option (roughly), tuned for a small
    // research cohort rather than high request volume. Revisit if login latency matters more.
    private const int MemorySizeKiB = 65536; // 64 MiB
    private const int Iterations = 3;
    private const int Parallelism = 2;

    public string HashPassword(ApplicationUser user, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = ComputeHash(password, salt, MemorySizeKiB, Iterations, Parallelism, HashSize);
        return Encode(MemorySizeKiB, Iterations, Parallelism, salt, hash);
    }

    public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
    {
        if (!TryDecode(hashedPassword, out var memorySizeKiB, out var iterations, out var parallelism, out var salt, out var expectedHash))
            return PasswordVerificationResult.Failed;

        var actualHash = ComputeHash(providedPassword, salt, memorySizeKiB, iterations, parallelism, expectedHash.Length);

        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            return PasswordVerificationResult.Failed;

        var isOutdated = memorySizeKiB != MemorySizeKiB || iterations != Iterations || parallelism != Parallelism;
        return isOutdated ? PasswordVerificationResult.SuccessRehashNeeded : PasswordVerificationResult.Success;
    }

    private static byte[] ComputeHash(string password, byte[] salt, int memorySizeKiB, int iterations, int parallelism, int hashSize)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memorySizeKiB,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon2.GetBytes(hashSize);
    }

    private static string Encode(int memorySizeKiB, int iterations, int parallelism, byte[] salt, byte[] hash)
        => $"$argon2id$v=19$m={memorySizeKiB},t={iterations},p={parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";

    private static bool TryDecode(
        string encoded,
        out int memorySizeKiB,
        out int iterations,
        out int parallelism,
        out byte[] salt,
        out byte[] hash)
    {
        memorySizeKiB = iterations = parallelism = 0;
        salt = hash = [];

        try
        {
            // $argon2id$v=19$m=65536,t=3,p=2$<salt>$<hash>
            var parts = encoded.Split('$', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 5 || parts[0] != "argon2id" || !parts[1].StartsWith("v="))
                return false;

            var costFields = parts[2].Split(',');
            if (costFields.Length != 3)
                return false;

            memorySizeKiB = int.Parse(costFields[0]["m=".Length..]);
            iterations = int.Parse(costFields[1]["t=".Length..]);
            parallelism = int.Parse(costFields[2]["p=".Length..]);
            salt = Convert.FromBase64String(parts[3]);
            hash = Convert.FromBase64String(parts[4]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
