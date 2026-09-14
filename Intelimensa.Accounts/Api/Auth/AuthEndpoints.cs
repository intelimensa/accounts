using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Api.Auth;

/// <summary>
/// The JSON API AxoSync's desktop client talks to. See
/// axosync/notes/accounts-service-integration.md for the client-side design written against this
/// contract.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        group.MapPost("/login", LoginAsync);
        group.MapPost("/refresh", RefreshAsync);
        group.MapPost("/logout", LogoutAsync);

        return app;
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ApplicationDbContext db,
        TokenService tokens)
    {
        var user = await userManager.FindByEmailAsync(request.Email);
        if (user is null)
            return Results.Unauthorized();

        var passwordCheck = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!passwordCheck.Succeeded)
            return Results.Unauthorized();

        var account = await db.Accounts.FirstOrDefaultAsync(a => a.UserId == user.Id);
        if (!IsAccountUsable(account))
            return Results.Unauthorized();

        var (accessToken, accessExpiresAt) = tokens.CreateAccessToken(user);
        var (refreshToken, refreshExpiresAt) = await tokens.CreateRefreshTokenAsync(user.Id);

        return Results.Ok(new TokenResponse(accessToken, accessExpiresAt, refreshToken, refreshExpiresAt));
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        TokenService tokens)
    {
        var rotated = await tokens.ValidateAndRotateRefreshTokenAsync(request.RefreshToken);
        if (rotated is null)
            return Results.Unauthorized();

        var (userId, refreshToken, refreshExpiresAt) = rotated.Value;

        var user = await userManager.FindByIdAsync(userId);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.UserId == userId);
        if (user is null || !IsAccountUsable(account))
            return Results.Unauthorized();

        var (accessToken, accessExpiresAt) = tokens.CreateAccessToken(user);

        return Results.Ok(new TokenResponse(accessToken, accessExpiresAt, refreshToken, refreshExpiresAt));
    }

    private static async Task<IResult> LogoutAsync(RefreshRequest request, TokenService tokens)
    {
        await tokens.RevokeAsync(request.RefreshToken);
        return Results.NoContent();
    }

    private static bool IsAccountUsable(Account? account) =>
        account is not null &&
        account.Status == AccountStatus.Active &&
        (account.ExpiresAt is null || account.ExpiresAt > DateTimeOffset.UtcNow);
}
