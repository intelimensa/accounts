using Microsoft.AspNetCore.Identity;

namespace Intelimensa.Accounts.Models;

public class ApplicationUser : IdentityUser
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
