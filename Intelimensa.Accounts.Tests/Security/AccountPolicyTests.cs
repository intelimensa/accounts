using Intelimensa.Accounts.Models;
using Intelimensa.Accounts.Security;

namespace Intelimensa.Accounts.Tests.Security;

public class AccountPolicyTests
{
    private static Account With(AccountStatus status, DateTimeOffset? expiresAt = null) =>
        new() { UserId = "u", Status = status, ExpiresAt = expiresAt };

    [Fact]
    public void ActiveAccountIsUsable() => Assert.True(AccountPolicy.IsUsable(With(AccountStatus.Active)));

    [Fact]
    public void NewAccountsStartPendingAndUnusable()
    {
        var account = new Account { UserId = "u" };
        Assert.Equal(AccountStatus.Pending, account.Status);
        Assert.False(AccountPolicy.IsUsable(account));
    }

    [Theory]
    [InlineData(AccountStatus.Pending)]
    [InlineData(AccountStatus.Revoked)]
    public void OnlyActiveIsUsable(AccountStatus status) => Assert.False(AccountPolicy.IsUsable(With(status)));

    [Fact]
    public void ExpiredAccountIsNotUsable() =>
        Assert.False(AccountPolicy.IsUsable(With(AccountStatus.Active, DateTimeOffset.UtcNow.AddDays(-1))));

    [Fact]
    public void StatusValuesStayStable()
    {
        // Stored as integers: existing rows depend on these.
        Assert.Equal(0, (int)AccountStatus.Active);
        Assert.Equal(1, (int)AccountStatus.Revoked);
        Assert.Equal(2, (int)AccountStatus.Pending);
    }
}
