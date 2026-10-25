using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace Intelimensa.Accounts.Security;

/// <summary>
/// The nonces install proofs sign (<see cref="InstallProof"/>). Stateless: a nonce is the time it was
/// issued plus an HMAC of that time, so the server stores nothing and any nonce it issued in the last
/// <see cref="Lifetime"/> is accepted. Every API response carries a fresh one in <c>X-Install-Nonce</c>
/// (<see cref="InstallNonceMiddleware"/>), and <c>GET /api/auth/nonce</c> exists for a client that has
/// none yet. A captured request can be replayed unchanged within the lifetime; it would only repeat
/// exactly what the install already asked for.
/// <para>The HMAC key is derived from <c>Jwt:SigningKey</c>, so there is no extra secret to manage; losing
/// it only voids nonces in flight.</para>
/// </summary>
public class InstallNonces
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private const int TimeSize = 8;
    private const int MacSize = 16;

    private readonly byte[] _key;

    public InstallNonces(IConfiguration configuration)
    {
        var jwtKey = Convert.FromBase64String(configuration["Jwt:SigningKey"]
            ?? throw new InvalidOperationException("Jwt:SigningKey is not configured."));
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256, jwtKey, 32, info: Encoding.UTF8.GetBytes("intelimensa-install-nonce-v1"));
    }

    public string Issue(DateTimeOffset now)
    {
        var bytes = new byte[TimeSize + MacSize];
        BinaryPrimitives.WriteInt64BigEndian(bytes, now.ToUnixTimeSeconds());
        Mac(bytes.AsSpan(0, TimeSize)).AsSpan(0, MacSize).CopyTo(bytes.AsSpan(TimeSize));
        return WebEncoders.Base64UrlEncode(bytes);
    }

    public bool IsValid(string nonce, DateTimeOffset now)
    {
        byte[] bytes;
        try
        {
            bytes = WebEncoders.Base64UrlDecode(nonce);
        }
        catch (FormatException)
        {
            return false;
        }
        if (bytes.Length != TimeSize + MacSize) return false;
        if (!CryptographicOperations.FixedTimeEquals(Mac(bytes.AsSpan(0, TimeSize)).AsSpan(0, MacSize), bytes.AsSpan(TimeSize)))
            return false;

        var issued = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(bytes));
        return issued <= now + TimeSpan.FromSeconds(5) && now - issued <= Lifetime;
    }

    private byte[] Mac(ReadOnlySpan<byte> time) => HMACSHA256.HashData(_key, time);
}

/// <summary>
/// For every <c>/api</c> request: buffers the body when the request carries an install proof (the proof
/// covers the body's hash, and the endpoint still has to read it), and adds a fresh nonce to the response.
/// </summary>
public class InstallNonceMiddleware(RequestDelegate next, InstallNonces nonces)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api")) return next(context);

        if (context.Request.Headers.ContainsKey(InstallProof.ProofHeader))
            context.Request.EnableBuffering();

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[InstallProof.NonceHeader] = nonces.Issue(DateTimeOffset.UtcNow);
            return Task.CompletedTask;
        });
        return next(context);
    }
}
