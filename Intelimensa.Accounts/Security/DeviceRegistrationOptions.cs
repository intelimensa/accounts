namespace Intelimensa.Accounts.Security;

public class DeviceRegistrationOptions
{
    /// <summary>
    /// When true (the default), registering a new account/unit pairing requires the unit's
    /// registration code. Turning this off is a deliberate, reversible relaxation: codes are still
    /// generated, stored and flashed regardless, so it can be switched back on without touching units.
    /// </summary>
    public bool RequireRegistrationCode { get; set; } = true;
}
