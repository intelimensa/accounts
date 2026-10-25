namespace Intelimensa.Accounts.Api.Auth;

/// <summary>
/// <paramref name="Install"/> registers (or re-identifies) the AxoSync install signing in; the request must
/// then carry an install proof by <see cref="ClientInstallRequest.SigningKey"/>. Clients that aren't AxoSync
/// (Quarry, the publish scripts) leave it out and get tokens bound to no install.
/// </summary>
public record LoginRequest(string Email, string Password, ClientInstallRequest? Install = null);

/// <summary>
/// The install's two public keys (base64 SPKI, P-256), its platform by name (<c>ClientPlatform</c>), and what to
/// call it on the participant's account page.
/// </summary>
public record ClientInstallRequest(
    string SigningKey,
    string AgreementKey,
    string Platform,
    string? Name = null,
    string? AppVersion = null,
    int? Slot = null);

public record RefreshRequest(string RefreshToken);

public record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    bool IsStudyParticipant);
