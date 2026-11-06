using Intelimensa.Accounts.Manufacturing;

namespace Intelimensa.Accounts.Tests.Manufacturing;

public class StepTagOptionsTests
{
    private static ManufacturingOptions Options(params (string Step, string Tag)[] steps)
    {
        var options = new ManufacturingOptions();
        options.StepTags.Clear();
        foreach (var (step, tag) in steps)
            options.StepTags[step] = tag;
        return options;
    }

    private static bool Valid(ManufacturingOptions options) =>
        new ManufacturingOptionsValidator().Validate(null, options).Succeeded;

    [Fact]
    public void The_defaults_name_a_tag_for_electrodes_and_for_mounting_and_are_valid()
    {
        var options = new ManufacturingOptions();
        Assert.Equal("electrode-set", options.StepTags["ElectrodesAssigned"]);
        Assert.Equal("mounting-clip", options.StepTags["MountingAssigned"]);
        Assert.True(Valid(options));
    }

    [Fact]
    public void A_step_tag_can_be_blank_for_no_requirement_or_a_valid_tag()
    {
        Assert.True(Valid(Options(("ElectrodesAssigned", ""), ("MountingAssigned", "clip"))));
        Assert.True(Valid(Options(("electrodesassigned", "x1"))));
    }

    [Theory]
    [InlineData("QcPassed", "x")]          // only the two assembly steps take a tag
    [InlineData("Nonsense", "x")]
    [InlineData("ElectrodesAssigned", "two words")]
    [InlineData("ElectrodesAssigned", "Upper")] // configuration holds stored (lowercase) tags
    public void A_bad_step_tag_setting_is_refused_at_startup(string step, string tag) =>
        Assert.False(Valid(Options((step, tag))));
}
