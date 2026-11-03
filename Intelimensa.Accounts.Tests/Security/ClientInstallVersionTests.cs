using Intelimensa.Accounts.Models;

namespace Intelimensa.Accounts.Tests.Security;

public class ClientInstallVersionTests
{
    private static ClientInstall Install(string? version) =>
        new() { SigningKey = "s", AgreementKey = "a", KeyThumbprint = "t", AppVersion = version, AccountId = 1 };

    [Fact]
    public void ANewVersionReplacesTheOldOne()
    {
        var install = Install("0.1.0-alpha.11");
        Assert.True(install.RecordVersion(" 0.1.0-alpha.15 "));
        Assert.Equal("0.1.0-alpha.15", install.AppVersion);
        Assert.False(install.RecordVersion("0.1.0-alpha.15"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankVersionsKeepWhatWeHave(string? reported)
    {
        var install = Install("0.1.0-alpha.15");
        Assert.False(install.RecordVersion(reported));
        Assert.Equal("0.1.0-alpha.15", install.AppVersion);
    }

    [Fact]
    public void OversizedVersionsAreIgnored()
    {
        var install = Install("1.0.0");
        Assert.False(install.RecordVersion(new string('9', 51)));
        Assert.Equal("1.0.0", install.AppVersion);
    }
}
