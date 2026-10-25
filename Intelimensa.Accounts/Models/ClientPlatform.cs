namespace Intelimensa.Accounts.Models;

/// <summary>
/// What kind of system a <see cref="ClientInstall"/> runs on. Distinct from <see cref="ReleasePlatform"/>,
/// which is per build and carries the CPU architecture too. Sent by name.
/// </summary>
public enum ClientPlatform
{
    Windows,
    MacOS,
    Linux,
    iOS,
    Android,
    Browser,
}
