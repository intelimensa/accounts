namespace Intelimensa.Accounts.Models;

/// <summary>
/// The OS/architecture targets an AxoSync build can be published for. Distinct from
/// <see cref="DevicePlatform"/> (what a registered client machine reports about itself) -- this
/// is per-build, so it carries the CPU architecture too.
/// </summary>
public enum ReleasePlatform
{
    WindowsX64,
    MacOSArm64,
    MacOSX64,
    LinuxX64,
}

public static class ReleasePlatformExtensions
{
    public static string DisplayName(this ReleasePlatform platform) => platform switch
    {
        ReleasePlatform.WindowsX64 => "Windows (x64)",
        ReleasePlatform.MacOSArm64 => "macOS (Apple Silicon)",
        ReleasePlatform.MacOSX64 => "macOS (Intel)",
        ReleasePlatform.LinuxX64 => "Linux (x64)",
        _ => platform.ToString(),
    };
}
