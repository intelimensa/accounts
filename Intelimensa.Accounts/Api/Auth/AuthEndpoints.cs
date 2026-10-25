using Intelimensa.Accounts.ConfigDelivery;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Api.Auth;

/// <summary>
/// The JSON API AxoSync's desktop client talks to. See
/// axosync/notes/accounts-service-integration.md for the client-side design written against this
/// contract, and docs/config-management.md (section 1) for client installs and install proofs.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", LoginAsync);
        group.MapPost("/refresh", RefreshAsync);
        group.MapPost("/logout", LogoutAsync);
        // For a client that has no nonce yet: every /api response carries one (InstallNonceMiddleware).
        group.MapGet("/nonce", () => Results.NoContent());

        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpRequest http,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ApplicationDbContext db,
        TokenService tokens,
        ClientInstallRegistry installs,
        InstallNonces nonces)
    {
        // An install proves it holds the signing key it registers, before anything else is looked at.
        if (request.Install is { } install)
        {
            if (!P256.IsPublicKey(install.SigningKey))
                return Results.BadRequest(new { error = "install_invalid" });
            var proof = await InstallProof.CheckAsync(http, install.SigningKey, nonces);
            if (proof != ProofCheck.Valid)
                return Results.Json(new { error = proof.ErrorCode() }, statusCode: StatusCodes.Status401Unauthorized);
        }

        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
            return Results.Unauthorized();

        var passwordCheck = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!passwordCheck.Succeeded)
            return Results.Unauthorized();

        var account = await db.Accounts
            .Include(a => a.StudyParticipation)
            .FirstOrDefaultAsync(a => a.UserId == user.Id);
        if (!AccountPolicy.IsUsable(account))
            return Results.Unauthorized();

        Guid? installId = null;
        if (request.Install is not null)
        {
            var registration = await installs.RegisterAsync(account!, user.Email!, request.Install, DateTimeOffset.UtcNow);
            switch (registration.Error)
            {
                case InstallRegistrationError.LimitReached:
                    return Results.Conflict(new { error = "install_limit" });
                case InstallRegistrationError.Invalid:
                    return Results.BadRequest(new { error = "install_invalid" });
            }
            installId = registration.Install!.Id;
        }

        var (accessToken, accessExpiresAt) = tokens.CreateAccessToken(user, await userManager.GetRolesAsync(user), installId);
        var (refreshToken, refreshExpiresAt) = await tokens.CreateRefreshTokenAsync(user.Id, installId);

        return Results.Ok(new TokenResponse(
            accessToken, accessExpiresAt, refreshToken, refreshExpiresAt,
            account!.StudyParticipation is { IsActive: true }));
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        HttpRequest http,
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        TokenService tokens,
        ClientInstallRegistry installs,
        InstallNonces nonces,
        ILoggerFactory loggers)
    {
        var existing = await tokens.FindActiveRefreshTokenAsync(request.RefreshToken);
        if (existing is null)
            return Results.Unauthorized();

        // A token bound to an install is only renewed for that install: a copied refresh token is useless alone.
        if (existing.ClientInstallId is { } installId)
        {
            var install = await installs.FindUsableAsync(installId, DateTimeOffset.UtcNow);
            if (install is null)
                return Results.Json(new { error = "install_revoked" }, statusCode: StatusCodes.Status401Unauthorized);
            var proof = await InstallProof.CheckAsync(http, install.SigningKey, nonces);
            if (proof != ProofCheck.Valid)
            {
                if (proof == ProofCheck.Invalid)
                    loggers.CreateLogger("Intelimensa.Accounts.Auth")
                        .LogWarning("Refresh token of install {InstallId} presented with a bad install proof", installId);
                return Results.Json(new { error = proof.ErrorCode() }, statusCode: StatusCodes.Status401Unauthorized);
            }
        }

        var user = await userManager.FindByIdAsync(existing.UserId);
        var account = await db.Accounts
            .Include(a => a.StudyParticipation)
            .FirstOrDefaultAsync(a => a.UserId == existing.UserId);
        if (user is null || !AccountPolicy.IsUsable(account))
            return Results.Unauthorized();

        var (refreshToken, refreshExpiresAt) = await tokens.RotateAsync(existing);
        var (accessToken, accessExpiresAt) = tokens.CreateAccessToken(user, await userManager.GetRolesAsync(user), existing.ClientInstallId);

        return Results.Ok(new TokenResponse(
            accessToken, accessExpiresAt, refreshToken, refreshExpiresAt,
            account!.StudyParticipation is { IsActive: true }));
    }

    private static async Task<IResult> LogoutAsync(RefreshRequest request, TokenService tokens)
    {
        await tokens.RevokeAsync(request.RefreshToken);
        return Results.NoContent();
    }
}
