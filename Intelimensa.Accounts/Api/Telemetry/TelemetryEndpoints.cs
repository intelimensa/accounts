using System.Security.Claims;
using Intelimensa.Accounts.Data;
using Intelimensa.Accounts.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Api.Telemetry;

/// <summary>
/// Session-end telemetry ingestion from the AxoSync client -- see CLAUDE.md's study-participation
/// design. Gated on the calling account having an active (non-withdrawn) <see cref="StudyParticipation"/>;
/// non-participants get 403 rather than having events silently accepted and dropped.
/// </summary>
public static class TelemetryEndpoints
{
    public static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/telemetry")
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser());

        group.MapPost("/batch", SubmitBatchAsync);

        return app;
    }

    private static async Task<IResult> SubmitBatchAsync(
        TelemetryBatchRequest request,
        ClaimsPrincipal user,
        ApplicationDbContext db)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null)
            return Results.Unauthorized();

        var account = await db.Accounts
            .Include(a => a.StudyParticipation)
            .FirstOrDefaultAsync(a => a.UserId == userId);
        if (account is null)
            return Results.Unauthorized();

        if (account.StudyParticipation is not { IsActive: true })
            return Results.Forbid();

        var accountDevice = await db.AccountDevices
            .FirstOrDefaultAsync(ad => ad.Id == request.AccountDeviceId && ad.AccountId == account.Id);
        if (accountDevice is null)
            return Results.BadRequest("Unknown device registration.");

        if (!accountDevice.IsActive)
            return Results.Forbid();

        if (request.Sessions.Count == 0)
            return Results.NoContent();

        var recordedAt = DateTimeOffset.UtcNow;
        db.TelemetryEvents.AddRange(request.Sessions.Select(s => new TelemetryEvent
        {
            AccountId = account.Id,
            AccountDeviceId = accountDevice.Id,
            SessionStartedAt = s.SessionStartedAt,
            SessionEndedAt = s.SessionEndedAt,
            DifficultyLevel = s.DifficultyLevel,
            Accuracy = s.Accuracy,
            SuccessfulCommands = s.SuccessfulCommands,
            RecordedAt = recordedAt,
        }));

        await db.SaveChangesAsync();

        return Results.NoContent();
    }
}
