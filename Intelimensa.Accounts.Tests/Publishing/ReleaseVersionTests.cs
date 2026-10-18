using Intelimensa.Accounts.Publishing;
using Xunit;

namespace Intelimensa.Accounts.Tests.Publishing;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.2", "1.0.0-alpha.10")]
    [InlineData("1.0.0-alpha.2", "1.0.0-alpha.99999999999999999999")]            // too big for a long
    [InlineData("1.0.0-alpha.99999999999999999998", "1.0.0-alpha.99999999999999999999")]
    [InlineData("1.0.0-alpha.99999999999999999999", "1.0.0-alpha.beta")]         // numeric below alphanumeric
    [InlineData("1.0.0", "1.0.1")]
    public void Orders_lower_before_higher(string lower, string higher)
    {
        Assert.True(ReleaseVersion.TryParse(lower, out var a));
        Assert.True(ReleaseVersion.TryParse(higher, out var b));
        Assert.True(a < b);
        Assert.True(b > a);
    }
}
