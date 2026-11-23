using System.Management;
using Syspect.Core.Models;

namespace Syspect.Core.Services;

/// <summary>
/// Queries hardware, BIOS, OS, and physical storage S.M.A.R.T. health via WMI/CIM.
/// </summary>
public sealed class SystemInfoService
{
    public Task<SystemOverview> GetSystemOverviewAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            string osCaption = "Windows";
            string osBuild = "";
            string uptimeText = "N/A";

            using (var osSearcher = new ManagementObjectSearcher("SELECT Caption, BuildNumber, LastBootUpTime FROM Win32_OperatingSystem"))
            {
                foreach (ManagementObject obj in osSearcher.Get())
                {
                    osCaption = WmiHelper.GetString(obj, "Caption", "Windows");
                    osBuild = WmiHelper.GetString(obj, "BuildNumber");
                    var bootRaw = WmiHelper.GetString(obj, "LastBootUpTime");
                    if (!string.IsNullOrEmpty(bootRaw))
                    {
                        try
                        {
                            var up = DateTime.Now - ManagementDateTimeConverter.ToDateTime(bootRaw);
                            uptimeText = up.Days > 0
                                ? $"{up.Days}d {up.Hours}h {up.Minutes}m"
                                : $"{up.Hours}h {up.Minutes}m";
                        }
                        catch
                        {
                            uptimeText = "N/A";
                        }
                    }
                    break;
                }
            }

            string machineModel = "";
            int totalRamGb = 0;
            using (var csSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem"))
            {
                foreach (ManagementObject obj in csSearcher.Get())
                {
                    machineModel = $"{WmiHelper.GetString(obj, "Manufacturer")} {WmiHelper.GetString(obj, "Model")}".Trim();
                    var memBytes = WmiHelper.GetNullableUlong(obj, "TotalPhysicalMemory");
                    if (memBytes.HasValue)
                    {
                        totalRamGb = (int)Math.Round(memBytes.Value / (1024.0 * 1024.0 * 1024.0));
                    }
                    break;
                }
            }

            string motherboard = "";
            using (var bbSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard"))
            {
                foreach (ManagementObject obj in bbSearcher.Get())
                {
                    motherboard = $"{WmiHelper.GetString(obj, "Manufacturer")} {WmiHelper.GetString(obj, "Product")}".Trim();
                    break;
                }
            }

            string biosVersion = "";
            string biosDate = "";
            using (var biosSearcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
            {
                foreach (ManagementObject obj in biosSearcher.Get())
                {
                    biosVersion = WmiHelper.GetString(obj, "SMBIOSBIOSVersion");
                    biosDate = WmiHelper.FormatDmtfDate(WmiHelper.GetString(obj, "ReleaseDate"));
                    break;
                }
            }

            string cpuName = "";
            using (var cpuSearcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
            {
                foreach (ManagementObject obj in cpuSearcher.Get())
                {
                    cpuName = WmiHelper.GetString(obj, "Name");
                    break;
                }
            }

            string gpuName = "";
            string gpuDriver = "";
            using (var gpuSearcher = new ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController"))
            {
                foreach (ManagementObject obj in gpuSearcher.Get())
                {
                    var name = WmiHelper.GetString(obj, "Name");
                    if (string.IsNullOrWhiteSpace(name) || name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrWhiteSpace(gpuName))
                        {
                            gpuName = name;
                            gpuDriver = WmiHelper.GetString(obj, "DriverVersion");
                        }
                        continue;
                    }

                    gpuName = name;
                    gpuDriver = WmiHelper.GetString(obj, "DriverVersion");
                    break;
                }
            }

            return new SystemOverview(
                OsCaption: osCaption,
                OsBuild: osBuild,
                MachineModel: machineModel,
                Motherboard: motherboard,
                BiosVersion: biosVersion,
                BiosDate: biosDate,
                CpuName: cpuName,
                GpuName: gpuName,
                GpuDriverVersion: gpuDriver,
                TotalRamGb: totalRamGb,
                UptimeText: uptimeText,
                IsAdmin: AdminHelper.IsAdministrator());
        }, cancellationToken);
    }

    public Task<IReadOnlyList<DiskHealthInfo>> GetDiskHealthAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DiskHealthInfo>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var countersById = new Dictionary<string, (int? Temp, int? Wear, ulong? ReadErr, ulong? WriteErr)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var relSearcher = new ManagementObjectSearcher(
                    @"\\.\root\Microsoft\Windows\Storage",
                    "SELECT DeviceId, Temperature, Wear, ReadErrorsTotal, WriteErrorsTotal FROM MSFT_StorageReliabilityCounter");
                foreach (ManagementObject rel in relSearcher.Get())
                {
                    var id = WmiHelper.GetString(rel, "DeviceId");
                    if (!string.IsNullOrEmpty(id))
                    {
                        countersById[id] = (
                            WmiHelper.GetNullableInt(rel, "Temperature"),
                            WmiHelper.GetNullableInt(rel, "Wear"),
                            WmiHelper.GetNullableUlong(rel, "ReadErrorsTotal"),
                            WmiHelper.GetNullableUlong(rel, "WriteErrorsTotal"));
                    }
                }
            }
            catch
            {
                // Reliability counters may require elevation.
            }

            double cFreeGb = 0;
            double cTotalGb = 0;
            try
            {
                var cDrive = new DriveInfo("C");
                if (cDrive.IsReady)
                {
                    cFreeGb = cDrive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0);
                    cTotalGb = cDrive.TotalSize / (1024.0 * 1024.0 * 1024.0);
                }
            }
            catch
            {
                // Ignore drive access errors.
            }

            var disks = new List<DiskHealthInfo>();
            using var diskSearcher = new ManagementObjectSearcher(
                @"\\.\root\Microsoft\Windows\Storage",
                "SELECT DeviceId, FriendlyName, MediaType, BusType, HealthStatus, OperationalStatus, FirmwareVersion, Size FROM MSFT_PhysicalDisk");

            foreach (ManagementObject disk in diskSearcher.Get())
            {
                var deviceId = WmiHelper.GetString(disk, "DeviceId");
                var friendlyName = WmiHelper.GetString(disk, "FriendlyName", "Unknown Disk");
                var sizeBytes = WmiHelper.GetNullableUlong(disk, "Size") ?? 0;
                var mediaType = MapMediaType(WmiHelper.GetNullableInt(disk, "MediaType"));
                var busType = MapBusType(WmiHelper.GetNullableInt(disk, "BusType"));
                var health = MapHealthStatus(WmiHelper.GetNullableInt(disk, "HealthStatus"));
                var opStatus = MapOperationalStatus(disk["OperationalStatus"]);
                var fw = WmiHelper.GetString(disk, "FirmwareVersion");

                countersById.TryGetValue(deviceId, out var c);

                disks.Add(new DiskHealthInfo(
                    DeviceId: deviceId,
                    FriendlyName: friendlyName,
                    MediaType: mediaType,
                    BusType: busType,
                    HealthStatus: health,
                    OperationalStatus: opStatus,
                    FirmwareVersion: fw,
                    SizeGb: Math.Round(sizeBytes / (1024.0 * 1024.0 * 1024.0), 1),
                    DriveLetter: deviceId == "0" ? "C:" : "",
                    FreeSpaceGb: deviceId == "0" ? cFreeGb : 0,
                    TotalSpaceGb: deviceId == "0" ? cTotalGb : 0,
                    TemperatureCelsius: c.Temp,
                    WearPercent: c.Wear,
                    ReadErrorsTotal: c.ReadErr,
                    WriteErrorsTotal: c.WriteErr));
            }

            return disks;
        }, cancellationToken);
    }

    private static string MapMediaType(int? value) => value switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "Unknown"
    };

    private static string MapBusType(int? value) => value switch
    {
        3 => "ATA",
        7 => "USB",
        11 => "SATA",
        17 => "NVMe",
        _ => value?.ToString() ?? "Unknown"
    };

    private static string MapHealthStatus(int? value) => value switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => "Unknown"
    };

    private static string MapOperationalStatus(object? raw)
    {
        if (raw is ushort[] arr && arr.Length > 0)
        {
            return arr[0] == 2 ? "OK" : $"Status {arr[0]}";
        }
        return "OK";
    }
}
