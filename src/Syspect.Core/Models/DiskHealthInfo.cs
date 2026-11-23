namespace Syspect.Core.Models;

/// <summary>
/// Physical SSD/HDD health and S.M.A.R.T. reliability metrics.
/// </summary>
public sealed record DiskHealthInfo(
    string DeviceId,
    string FriendlyName,
    string MediaType,
    string BusType,
    string HealthStatus,
    string OperationalStatus,
    string FirmwareVersion,
    double SizeGb,
    string DriveLetter,
    double FreeSpaceGb,
    double TotalSpaceGb,
    int? TemperatureCelsius,
    int? WearPercent,
    ulong? ReadErrorsTotal,
    ulong? WriteErrorsTotal)
{
    public string TemperatureDisplay => TemperatureCelsius.HasValue ? $"{TemperatureCelsius.Value} °C" : "N/A";
    public string WearDisplay => WearPercent.HasValue ? $"{WearPercent.Value}%" : "N/A";
    public string ErrorsDisplay => ReadErrorsTotal.HasValue || WriteErrorsTotal.HasValue
        ? $"Read: {ReadErrorsTotal ?? 0} / Write: {WriteErrorsTotal ?? 0}"
        : "Admin required";
    public string CapacityDisplay => TotalSpaceGb > 0
        ? $"{FreeSpaceGb:F0} GB free of {TotalSpaceGb:F0} GB"
        : $"{SizeGb:F0} GB";
    public bool IsHealthy => string.Equals(HealthStatus, "Healthy", StringComparison.OrdinalIgnoreCase);
}
