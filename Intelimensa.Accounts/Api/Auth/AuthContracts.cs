namespace Intelimensa.Accounts.Api.Auth;

public record LoginRequest(string Email, string Password);

public record RefreshRequest(string RefreshToken);

public record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);
