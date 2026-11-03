using Intelimensa.Accounts.Data;
using Microsoft.EntityFrameworkCore;

namespace Intelimensa.Accounts.Security;

public static class UserSessions
{
    /// <summary>
    /// Revokes every active refresh token of a user (AxoSync, Quarry and script sessions), so that after a password
    /// change nothing signed in with the old password can renew itself. Access tokens already issued live out their 15 minutes.
    /// </summary>
    public static async Task RevokeRefreshTokensAsync(ApplicationDbContext db, string userId, DateTimeOffset now)
    {
        var tokens = await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync();
        foreach (var token in tokens)
            token.RevokedAt = now;
        await db.SaveChangesAsync();
    }
}
