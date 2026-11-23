namespace Syspect.Core.Models;

/// <summary>
/// High-level hardware and operating system overview of the local PC.
/// </summary>
public sealed record SystemOverview(
    string OsCaption,
    string OsBuild,
    string MachineModel,
    string Motherboard,
    string BiosVersion,
    string BiosDate,
    string CpuName,
    string GpuName,
    string GpuDriverVersion,
    int TotalRamGb,
    string UptimeText,
    bool IsAdmin);
