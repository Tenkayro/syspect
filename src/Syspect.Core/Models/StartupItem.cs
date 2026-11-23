namespace Syspect.Core.Models;

public enum StartupSource
{
    RegistryUserRun,
    RegistryMachineRun,
    RegistryMachineWow64Run,
    StartupFolderUser,
    StartupFolderAllUsers,
    PackagedUwpApp
}

public sealed record StartupItem(
    string Id,
    string Name,
    string Command,
    string LocationLabel,
    StartupSource Source,
    bool IsEnabled,
    string RegistryHiveOrPath,
    string ApprovalKeyPath,
    string ValueName,
    bool CanToggle,
    string? ExecutablePath = null,
    string? PackageFamilyName = null)
{
    public string StatusLabel => IsEnabled ? "Enabled" : "Disabled";
}
