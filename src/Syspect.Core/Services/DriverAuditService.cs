using System.Management;
using Syspect.Core.Models;

namespace Syspect.Core.Services;

/// <summary>
/// Audits PnP device error codes and key signed drivers, flagging generic inbox drivers on vendor hardware.
/// </summary>
public sealed class DriverAuditService
{
    private static readonly HashSet<string> KeyClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "DISPLAY", "NET", "MEDIA", "SCSIADAPTER", "HDC",
        "SECURITYDEVICES", "SYSTEM", "CAMERA", "MONITOR", "BLUETOOTH", "FIRMWARE"
    };

    public Task<DriverAuditResult> AuditAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entityHardwareIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var problemDevices = new List<DriverEntry>();

            using (var pnpSearcher = new ManagementObjectSearcher(
                "SELECT Name, PNPClass, Status, ConfigManagerErrorCode, Present, DeviceID, HardwareID, Manufacturer FROM Win32_PnPEntity"))
            {
                foreach (ManagementObject obj in pnpSearcher.Get())
                {
                    var deviceId = WmiHelper.GetString(obj, "DeviceID");
                    var hwIds = string.Join("; ", WmiHelper.GetStringArray(obj, "HardwareID").Take(2));
                    if (!string.IsNullOrWhiteSpace(deviceId))
                    {
                        entityHardwareIds[deviceId] = hwIds;
                    }

                    var errCode = WmiHelper.GetNullableInt(obj, "ConfigManagerErrorCode") ?? 0;
                    var present = obj["Present"] as bool? ?? true;
                    if (present && errCode != 0 && errCode != 45)
                    {
                        problemDevices.Add(new DriverEntry(
                            DeviceName: WmiHelper.GetString(obj, "Name", "Unknown Device"),
                            DeviceClass: WmiHelper.GetString(obj, "PNPClass", "Unknown"),
                            ProviderName: WmiHelper.GetString(obj, "Manufacturer"),
                            DriverVersion: string.Empty,
                            DriverDate: string.Empty,
                            InfName: string.Empty,
                            DeviceId: deviceId,
                            HardwareIds: hwIds,
                            ProblemCode: errCode,
                            Status: WmiHelper.GetString(obj, "Status", "Error"),
                            IsProblemDevice: true,
                            HasGenericDriverHint: false,
                            HintMessage: $"Device Manager reports error code {errCode}."));
                    }
                }
            }

            var keyDrivers = new List<DriverEntry>();
            int genericHintCount = 0;

            using (var drvSearcher = new ManagementObjectSearcher(
                "SELECT DeviceClass, DeviceName, DriverProviderName, DriverVersion, DriverDate, InfName, DeviceID FROM Win32_PnPSignedDriver"))
            {
                foreach (ManagementObject obj in drvSearcher.Get())
                {
                    var deviceName = WmiHelper.GetString(obj, "DeviceName");
                    var deviceClass = WmiHelper.GetString(obj, "DeviceClass");
                    if (string.IsNullOrWhiteSpace(deviceName) || !KeyClasses.Contains(deviceClass))
                    {
                        continue;
                    }

                    var provider = WmiHelper.GetString(obj, "DriverProviderName");
                    var infName = WmiHelper.GetString(obj, "InfName");
                    var deviceId = WmiHelper.GetString(obj, "DeviceID");

                    if (deviceClass.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) &&
                        provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) &&
                        !deviceName.Contains("Firmware", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    entityHardwareIds.TryGetValue(deviceId, out var hwIds);
                    hwIds ??= string.Empty;

                    var (hasHint, hintMessage) = EvaluateGenericDriverHint(deviceClass, deviceName, provider, infName, deviceId, hwIds);
                    if (hasHint)
                    {
                        genericHintCount++;
                    }

                    keyDrivers.Add(new DriverEntry(
                        DeviceName: deviceName,
                        DeviceClass: deviceClass,
                        ProviderName: provider,
                        DriverVersion: WmiHelper.GetString(obj, "DriverVersion"),
                        DriverDate: WmiHelper.FormatDmtfDate(WmiHelper.GetString(obj, "DriverDate")),
                        InfName: infName,
                        DeviceId: deviceId,
                        HardwareIds: hwIds,
                        ProblemCode: 0,
                        Status: "OK",
                        IsProblemDevice: false,
                        HasGenericDriverHint: hasHint,
                        HintMessage: hintMessage));
                }
            }

            var ordered = keyDrivers
                .OrderByDescending(d => d.HasGenericDriverHint)
                .ThenBy(d => d.DeviceClass, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new DriverAuditResult(problemDevices, ordered, genericHintCount);
        }, cancellationToken);
    }

    private static (bool HasHint, string Message) EvaluateGenericDriverHint(
        string deviceClass,
        string deviceName,
        string provider,
        string infName,
        string deviceId,
        string hwIds)
    {
        if (deviceClass.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) &&
            (deviceId.Contains("VEN_10EC", StringComparison.OrdinalIgnoreCase) || hwIds.Contains("VEN_10EC", StringComparison.OrdinalIgnoreCase)) &&
            provider.Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "Onboard Realtek audio uses the generic Microsoft HD Audio driver. Only needed if motherboard analog/optical jacks are used (external USB DACs remain unaffected).");
        }

        if (deviceClass.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase) &&
            (deviceName.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || infName.Equals("display.inf", StringComparison.OrdinalIgnoreCase)))
        {
            return (true, "Microsoft Basic Display Adapter is active — verify dedicated GPU driver installation.");
        }

        return (false, string.Empty);
    }
}
