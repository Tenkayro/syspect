namespace Syspect.Core.Models;

public sealed record DriverEntry(
    string DeviceName,
    string DeviceClass,
    string ProviderName,
    string DriverVersion,
    string DriverDate,
    string InfName,
    string DeviceId,
    string HardwareIds,
    int ProblemCode,
    string Status,
    bool IsProblemDevice,
    bool HasGenericDriverHint,
    string HintMessage)
{
    public bool HasAnyIssue => IsProblemDevice || HasGenericDriverHint;
    public string StatusLabel => IsProblemDevice
        ? $"Error (Code {ProblemCode})"
        : HasGenericDriverHint
            ? "Generic Driver"
            : "OK";
}

public sealed record DriverAuditResult(
    IReadOnlyList<DriverEntry> ProblemDevices,
    IReadOnlyList<DriverEntry> KeyDrivers,
    int GenericHintCount);
