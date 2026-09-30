namespace Intelimensa.Accounts.Manufacturing;

public class ManufacturingOptions
{
    /// <summary>
    /// Device type (the <c>Config.Key</c> vocabulary, e.g. "ms2") to the 3-character product code
    /// used in serial numbers. A device type missing from this map can't be manufactured.
    /// </summary>
    public Dictionary<string, string> ProductCodes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
