using Microsoft.Extensions.Options;

namespace Intelimensa.Accounts.Manufacturing;

public class ManufacturingOptions
{
    /// <summary>
    /// Device type (the <c>Config.Key</c> vocabulary, e.g. "ms2") to the 4-character product code
    /// used in serial numbers. A device type missing from this map can't be manufactured.
    /// </summary>
    public Dictionary<string, string> ProductCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The allowed region/variant characters (one alphanumeric character each) mapped to a
    /// human-readable meaning. A new variant has to be declared here before a station can use it,
    /// so an operator typo can't mint permanently mislabeled serials.
    /// </summary>
    public Dictionary<string, string> RegionCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>First sequence number issued for a new product+region (obscures unit counts).</summary>
    public int SequenceStart { get; set; } = 3_000_000;

    /// <summary>
    /// Each new serial advances the sequence by a random step of 1..this value, so serials don't
    /// reveal exactly how many units exist.
    /// </summary>
    public int MaxSequenceStep { get; set; } = 8;
}

public class ManufacturingOptionsValidator : IValidateOptions<ManufacturingOptions>
{
    public ValidateOptionsResult Validate(string? name, ManufacturingOptions options)
    {
        var errors = new List<string>();

        foreach (var (type, code) in options.ProductCodes)
            if (!SerialNumber.IsValidProductCode(code))
                errors.Add($"Manufacturing:ProductCodes:{type} '{code}' must be 4 uppercase alphanumeric characters.");

        foreach (var (code, _) in options.RegionCodes)
            if (!SerialNumber.IsValidRegionCode(code.ToUpperInvariant()) || code != code.ToUpperInvariant())
                errors.Add($"Manufacturing:RegionCodes:{code} must be 1 uppercase alphanumeric character.");

        if (options.SequenceStart is < 0 or > SerialNumber.MaxSequence)
            errors.Add("Manufacturing:SequenceStart is out of range.");
        if (options.MaxSequenceStep < 1)
            errors.Add("Manufacturing:MaxSequenceStep must be at least 1.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
