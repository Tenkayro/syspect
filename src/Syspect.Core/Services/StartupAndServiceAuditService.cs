using System.Management;
using System.ServiceProcess;
using Microsoft.Win32;
using Syspect.Core.Models;

namespace Syspect.Core.Services;

/// <summary>
/// Audits and safely manages Startup apps (Run, Startup folders, UWP), third-party Services, and Scheduled Tasks.
/// </summary>
public sealed class StartupAndServiceAuditService
{
    private static readonly HashSet<string> ProtectedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "SharedAccess", "hns", "WSLService", "mpssvc", "BFE", "RpcSs", "DcomLaunch",
        "SamSs", "LanmanServer", "LanmanWorkstation", "NVDisplay.ContainerLocalSystem",
        "NvContainerLocalSystem", "WinDefend", "SecurityHealthService", "WdNisSvc",
        "AudioSrv", "AudioEndpointBuilder", "PlugPlay", "Power", "EventLog", "Schedule",
        "Dhcp", "Dnscache", "nsi", "ProfSvc", "UserManager", "Winmgmt", "sppsvc",
        "TrustedInstaller", "wuauserv", "UsoSvc", "GamingServices", "GamingServicesNet",
        "GameInputRedistService", "XboxService", "CodexSandboxService.OpenAI.Codex"
    };

    private static readonly Dictionary<string, string> CleanupCandidateServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AsusUpdateCheck"] = "ASUS Armoury Crate leftover — safe to disable.",
        ["ONLYOFFICE Update Service"] = "Background updater — safe to set to Manual.",
        ["Bonjour Service"] = "Apple mDNS service (only needed for iTunes/AirPrint) — safe to disable.",
        ["DiagTrack"] = "Windows telemetry service — safe to disable.",
        ["MapsBroker"] = "Downloaded Maps Manager — keep Manual.",
        ["CscService"] = "Offline Files service — safe to disable."
    };

    private static readonly Dictionary<string, string> CautionServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Antares Central Services"] = "Audio/DAW licensing service (Antares) — keep enabled if plugins are used.",
        ["NTKDaemonService"] = "Native Instruments daemon — required for NI audio plugins/Kontakt.",
        ["PaceLicenseDServices"] = "PACE/iLok licensing service — do not disable if DAW plugins use iLok.",
        ["VMAuthdService"] = "VMware authorization service — start manually before launching VMs if changed.",
        ["VMnetDHCP"] = "VMware DHCP service — required for bridged/NAT VM networking.",
        ["VMUSBArbService"] = "VMware USB arbitration — required for USB passthrough in VMs.",
        ["VMware NAT Service"] = "VMware NAT service — required for NAT VM networking.",
        ["Tailscale"] = "Tailscale VPN daemon — set to Manual only if VPN is used on demand.",
        ["StorSvc"] = "Windows Storage Service — used by Microsoft Store and Xbox app installs."
    };

    private static readonly HashSet<string> TrackedSystemServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "AsusUpdateCheck", "DiagTrack", "SharedAccess", "hns", "CscService", "MapsBroker", "StorSvc"
    };

    public Task<IReadOnlyList<StartupItem>> GetStartupItemsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<StartupItem>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = new List<StartupItem>();

            ReadRegistryRun(items, Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
                "HKCU Run", StartupSource.RegistryUserRun, "HKCU");

            ReadRegistryRun(items, Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
                "HKLM Run", StartupSource.RegistryMachineRun, "HKLM");

            ReadRegistryRun(items, Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32",
                "HKLM Run (32-bit)", StartupSource.RegistryMachineWow64Run, "HKLM");

            ReadStartupFolder(items,
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Registry.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
                "Startup Folder (User)", StartupSource.StartupFolderUser, "HKCU");

            ReadStartupFolder(items,
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                Registry.LocalMachine,
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
                "Startup Folder (All Users)", StartupSource.StartupFolderAllUsers, "HKLM");

            ReadUwpStartupApps(items);

            return items
                .OrderByDescending(i => i.IsEnabled)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken);
    }

    public Task<(bool Success, string Message)> SetStartupItemEnabledAsync(StartupItem item, bool enable)
    {
        return Task.Run(() =>
        {
            if (!item.CanToggle)
            {
                return (false, "This entry is policy-managed and cannot be toggled.");
            }

            try
            {
                if (item.Source == StartupSource.PackagedUwpApp)
                {
                    using var key = Registry.CurrentUser.OpenSubKey(item.ApprovalKeyPath, writable: true);
                    if (key is null) return (false, "UWP startup registry key not found.");

                    key.SetValue("State", enable ? 2 : 1, RegistryValueKind.DWord);
                    return (true, enable ? $"Enabled {item.Name}." : $"Disabled {item.Name}.");
                }

                var root = item.RegistryHiveOrPath.Equals("HKLM", StringComparison.OrdinalIgnoreCase)
                    ? Registry.LocalMachine
                    : Registry.CurrentUser;

                using var approvalKey = root.CreateSubKey(item.ApprovalKeyPath, writable: true);
                var data = new byte[12];
                data[0] = enable ? (byte)0x02 : (byte)0x03;
                if (!enable)
                {
                    BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
                }

                approvalKey.SetValue(item.ValueName, data, RegistryValueKind.Binary);
                return (true, enable ? $"Enabled {item.Name}." : $"Disabled {item.Name}.");
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}");
            }
        });
    }

    public Task<IReadOnlyList<ServiceEntry>> GetServicesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ServiceEntry>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = new List<ServiceEntry>();
            var services = ServiceController.GetServices();

            try
            {
                foreach (var sc in services)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string imagePath = string.Empty;
                    int? startCode = null;
                    int delayed = 0;

                    try
                    {
                        using var regKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{sc.ServiceName}");
                        if (regKey is not null)
                        {
                            imagePath = regKey.GetValue("ImagePath")?.ToString() ?? string.Empty;
                            if (regKey.GetValue("Start") is int st) startCode = st;
                            if (regKey.GetValue("DelayedAutoStart") is int d) delayed = d;
                        }
                    }
                    catch { }

                    bool isMicrosoftCore = IsCoreWindowsBinary(imagePath);
                    if (isMicrosoftCore && !TrackedSystemServices.Contains(sc.ServiceName))
                    {
                        continue;
                    }

                    string startMode = startCode switch
                    {
                        2 when delayed == 1 => "DelayedAuto",
                        2 => "Auto",
                        3 => "Manual",
                        4 => "Disabled",
                        _ => sc.StartType.ToString()
                    };

                    var (tier, note) = ClassifyService(sc.ServiceName);
                    bool canModify = tier != SafetyTier.Protected &&
                                     !sc.ServiceName.Equals("CodexSandboxService.OpenAI.Codex", StringComparison.OrdinalIgnoreCase);

                    list.Add(new ServiceEntry(
                        Name: sc.ServiceName,
                        DisplayName: sc.DisplayName,
                        StartMode: startMode,
                        State: sc.Status.ToString(),
                        ImagePath: imagePath,
                        SafetyTier: tier,
                        CategoryNote: note,
                        IsMicrosoftBinary: isMicrosoftCore,
                        CanModify: canModify));
                }
            }
            finally
            {
                foreach (var sc in services) sc.Dispose();
            }

            return list
                .OrderBy(s => s.SafetyTier)
                .ThenByDescending(s => s.IsRunning)
                .ThenBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken);
    }

    public async Task<(bool Success, string Message)> SetServiceStartModeAsync(string serviceName, string scStartMode, CancellationToken cancellationToken = default)
    {
        if (ProtectedServices.Contains(serviceName))
        {
            return (false, $"Service '{serviceName}' is protected and cannot be modified.");
        }

        var (code, output) = await ProcessRunner.RunAsync("sc.exe", $"config \"{serviceName}\" start= {scStartMode}", cancellationToken: cancellationToken).ConfigureAwait(false);
        return code == 0
            ? (true, $"Set startup type of '{serviceName}' to '{scStartMode}'.")
            : (false, $"Error (code {code}): {output.Trim()}");
    }

    public Task<(bool Success, string Message)> SetServiceRunningStateAsync(string serviceName, bool start)
    {
        if (!start && ProtectedServices.Contains(serviceName))
        {
            return Task.FromResult((false, $"Service '{serviceName}' is protected and will not be stopped."));
        }

        return SetServiceRunningStateCoreAsync(serviceName, start);
    }

    internal async Task<(bool Success, string Message)> RestartProtectedContainerServiceAsync(string serviceName)
    {
        var (stopOk, stopMsg) = await SetServiceRunningStateCoreAsync(serviceName, start: false).ConfigureAwait(false);
        var (startOk, startMsg) = await SetServiceRunningStateCoreAsync(serviceName, start: true).ConfigureAwait(false);
        return startOk
            ? (true, $"{serviceName} restarted.")
            : (false, $"{serviceName}: Stop={stopOk} ({stopMsg}), Start={startOk} ({startMsg})");
    }

    private static Task<(bool Success, string Message)> SetServiceRunningStateCoreAsync(string serviceName, bool start)
    {
        return Task.Run(() =>
        {
            try
            {
                using var sc = new ServiceController(serviceName);
                if (start)
                {
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                    }
                    return (true, $"Started service '{serviceName}'.");
                }
                else
                {
                    if (sc.Status != ServiceControllerStatus.Stopped && sc.CanStop)
                    {
                        sc.Stop();
                        sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    }
                    return (true, $"Stopped service '{serviceName}'.");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Error for '{serviceName}': {ex.Message}");
            }
        });
    }

    public Task<IReadOnlyList<ScheduledTaskEntry>> GetScheduledTasksAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ScheduledTaskEntry>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = new List<ScheduledTaskEntry>();

            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\Microsoft\Windows\TaskScheduler",
                "SELECT TaskPath, TaskName, State, Actions, Triggers, Settings FROM MSFT_ScheduledTask");

            foreach (ManagementObject obj in searcher.Get())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var taskPath = WmiHelper.GetString(obj, "TaskPath", @"\");
                if (taskPath.Equals(@"\Microsoft", StringComparison.OrdinalIgnoreCase) ||
                    taskPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var taskName = WmiHelper.GetString(obj, "TaskName");
                if (string.IsNullOrWhiteSpace(taskName))
                {
                    continue;
                }

                var normalizedFolder = taskPath.TrimEnd('\\');
                var fullPath = string.IsNullOrEmpty(normalizedFolder)
                    ? $@"\{taskName}"
                    : $@"{normalizedFolder}\{taskName}";

                var stateCode = WmiHelper.GetNullableInt(obj, "State") ?? 0;
                var state = stateCode switch
                {
                    1 => "Disabled",
                    2 => "Queued",
                    3 => "Ready",
                    4 => "Running",
                    _ => stateCode.ToString()
                };

                bool enabled = stateCode != 1;
                if (obj["Settings"] is ManagementBaseObject settings)
                {
                    try
                    {
                        if (settings["Enabled"] is bool settingsEnabled)
                        {
                            enabled = settingsEnabled;
                        }
                    }
                    catch { }
                }

                var triggerNames = new List<string>();
                if (obj["Triggers"] is ManagementBaseObject[] triggers)
                {
                    foreach (var trig in triggers)
                    {
                        if (trig is null) continue;
                        var trigClass = trig.ClassPath.ClassName;
                        triggerNames.Add(trigClass switch
                        {
                            "MSFT_TaskTimeTrigger" => "Schedule",
                            "MSFT_TaskDailyTrigger" => "Daily",
                            "MSFT_TaskWeeklyTrigger" => "Weekly",
                            "MSFT_TaskMonthlyTrigger" or "MSFT_TaskMonthlyDOWTrigger" => "Monthly",
                            "MSFT_TaskIdleTrigger" => "Idle",
                            "MSFT_TaskBootTrigger" => "Boot",
                            "MSFT_TaskLogonTrigger" => "Logon",
                            "MSFT_TaskRegistrationTrigger" => "Registration",
                            _ => "Event"
                        });
                    }
                }

                string execPath = string.Empty;
                string args = string.Empty;
                if (obj["Actions"] is ManagementBaseObject[] actions)
                {
                    foreach (var act in actions)
                    {
                        if (act is null) continue;
                        var candidateExec = WmiHelper.GetString(act, "Execute");
                        if (act.ClassPath.ClassName.Equals("MSFT_TaskExecAction", StringComparison.OrdinalIgnoreCase) ||
                            !string.IsNullOrWhiteSpace(candidateExec))
                        {
                            execPath = candidateExec;
                            args = WmiHelper.GetString(act, "Arguments");
                            break;
                        }
                    }
                }

                var expandedPath = string.IsNullOrWhiteSpace(execPath)
                    ? string.Empty
                    : Environment.ExpandEnvironmentVariables(execPath.Trim().Trim('"'));
                var resolvedTarget = ResolveExecutablePath(execPath) ?? expandedPath;
                bool isMissing = IsExecutableMissing(expandedPath);
                bool isCandidate = isMissing || fullPath.Contains("Ubisoft Connect Background Update", StringComparison.OrdinalIgnoreCase);

                string note = isMissing
                    ? "Target executable no longer exists (orphaned task — safe to delete)."
                    : fullPath.Contains("Ubisoft Connect Background Update", StringComparison.OrdinalIgnoreCase)
                        ? "Unnecessary background updater — Ubisoft updates when launched."
                        : string.Empty;

                list.Add(new ScheduledTaskEntry(
                    TaskPath: taskPath,
                    TaskName: taskName,
                    FullPath: fullPath,
                    Enabled: enabled,
                    State: state,
                    Triggers: string.Join(", ", triggerNames.Distinct()),
                    Action: string.IsNullOrWhiteSpace(args) ? execPath : $"{execPath} {args}",
                    TargetPath: resolvedTarget,
                    IsMissingTarget: isMissing,
                    IsCleanupCandidate: isCandidate,
                    Note: note));
            }

            return list
                .OrderByDescending(t => t.IsMissingTarget)
                .ThenByDescending(t => t.Enabled)
                .ThenBy(t => t.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken);
    }

    public async Task<(bool Success, string Message)> SetTaskEnabledAsync(string fullPath, bool enabled)
    {
        var flag = enabled ? "/ENABLE" : "/DISABLE";
        var (code, output) = await ProcessRunner.RunAsync("schtasks.exe", $"/Change /TN \"{fullPath}\" {flag}").ConfigureAwait(false);
        return code == 0
            ? (true, enabled ? $"Enabled task '{fullPath}'." : $"Disabled task '{fullPath}'.")
            : (false, $"Error (code {code}): {output.Trim()}");
    }

    public async Task<(bool Success, string Message)> DeleteTaskAsync(string fullPath)
    {
        var (code, output) = await ProcessRunner.RunAsync("schtasks.exe", $"/Delete /TN \"{fullPath}\" /F").ConfigureAwait(false);
        return code == 0
            ? (true, $"Deleted task '{fullPath}'.")
            : (false, $"Error deleting task (code {code}): {output.Trim()}");
    }

    private static void ReadRegistryRun(
        List<StartupItem> items,
        RegistryKey root,
        string runPath,
        string approvalPath,
        string locationLabel,
        StartupSource source,
        string hiveName)
    {
        try
        {
            using var runKey = root.OpenSubKey(runPath);
            if (runKey is null) return;

            using var approvalKey = root.OpenSubKey(approvalPath);
            foreach (var valueName in runKey.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName)) continue;
                var cmd = runKey.GetValue(valueName)?.ToString() ?? string.Empty;
                var isEnabled = IsApprovedEnabled(approvalKey?.GetValue(valueName) as byte[]);
                items.Add(new StartupItem(
                    Id: $"{hiveName}:{runPath}:{valueName}",
                    Name: valueName,
                    Command: cmd,
                    LocationLabel: locationLabel,
                    Source: source,
                    IsEnabled: isEnabled,
                    RegistryHiveOrPath: hiveName,
                    ApprovalKeyPath: approvalPath,
                    ValueName: valueName,
                    CanToggle: true,
                    ExecutablePath: ResolveExecutablePath(cmd)));
            }
        }
        catch { }
    }

    private static void ReadStartupFolder(
        List<StartupItem> items,
        string folderPath,
        RegistryKey root,
        string approvalPath,
        string locationLabel,
        StartupSource source,
        string hiveName)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;

        try
        {
            using var approvalKey = root.OpenSubKey(approvalPath);
            foreach (var file in Directory.GetFiles(folderPath))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var isEnabled = IsApprovedEnabled(approvalKey?.GetValue(name) as byte[]);
                items.Add(new StartupItem(
                    Id: $"{hiveName}:Folder:{name}",
                    Name: Path.GetFileNameWithoutExtension(name),
                    Command: file,
                    LocationLabel: locationLabel,
                    Source: source,
                    IsEnabled: isEnabled,
                    RegistryHiveOrPath: hiveName,
                    ApprovalKeyPath: approvalPath,
                    ValueName: name,
                    CanToggle: true,
                    ExecutablePath: file));
            }
        }
        catch { }
    }

    private static void ReadUwpStartupApps(List<StartupItem> items)
    {
        const string rootPath = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
        try
        {
            using var baseKey = Registry.CurrentUser.OpenSubKey(rootPath);
            if (baseKey is null) return;

            foreach (var pkgName in baseKey.GetSubKeyNames())
            {
                using var pkgKey = baseKey.OpenSubKey(pkgName);
                if (pkgKey is null) continue;

                foreach (var subName in pkgKey.GetSubKeyNames())
                {
                    using var taskKey = pkgKey.OpenSubKey(subName);
                    if (taskKey?.GetValue("State") is not int state) continue;

                    bool enabled = state is 2 or 4;
                    bool canToggle = state is not (3 or 4);
                    var friendlyPkg = pkgName.Split('_')[0];
                    if (friendlyPkg.Contains('.'))
                    {
                        friendlyPkg = friendlyPkg[(friendlyPkg.IndexOf('.') + 1)..];
                    }

                    items.Add(new StartupItem(
                        Id: $"UWP:{pkgName}:{subName}",
                        Name: friendlyPkg,
                        Command: $"{pkgName} ({subName})",
                        LocationLabel: "Store App (UWP)",
                        Source: StartupSource.PackagedUwpApp,
                        IsEnabled: enabled,
                        RegistryHiveOrPath: "HKCU",
                        ApprovalKeyPath: $@"{rootPath}\{pkgName}\{subName}",
                        ValueName: "State",
                        CanToggle: canToggle,
                        PackageFamilyName: pkgName));
                }
            }
        }
        catch { }
    }

    private static bool IsApprovedEnabled(byte[]? raw) =>
        raw is null || raw.Length == 0 || (raw[0] & 1) == 0;

    private static bool IsCoreWindowsBinary(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return true;
        var clean = imagePath.Trim().Trim('"');
        return clean.StartsWith(@"C:\Windows\system32\svchost.exe", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith(@"C:\Windows\System32\", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith(@"C:\Windows\servicing\", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith(@"%SystemRoot%\system32\", StringComparison.OrdinalIgnoreCase) ||
               clean.StartsWith(@"C:\ProgramData\Microsoft\Windows Defender\", StringComparison.OrdinalIgnoreCase);
    }

    private static (SafetyTier Tier, string Note) ClassifyService(string serviceName)
    {
        if (ProtectedServices.Contains(serviceName))
        {
            return serviceName switch
            {
                "SharedAccess" => (SafetyTier.Protected, "Protected — actively used by WSL/Host Network Service (do not disable!)."),
                "hns" or "WSLService" => (SafetyTier.Protected, "Protected — WSL networking and subsystem service."),
                "NVDisplay.ContainerLocalSystem" or "NvContainerLocalSystem" => (SafetyTier.Protected, "Protected — NVIDIA display driver and system tray container."),
                _ => (SafetyTier.Protected, "System/Gaming core service — protected.")
            };
        }

        if (CleanupCandidateServices.TryGetValue(serviceName, out var cleanupNote))
        {
            return (SafetyTier.CleanupCandidate, cleanupNote);
        }

        if (CautionServices.TryGetValue(serviceName, out var cautionNote))
        {
            return (SafetyTier.Caution, cautionNote);
        }

        return (SafetyTier.Standard, "Third-party service.");
    }

    private static bool IsExecutableMissing(string expandedPath)
    {
        if (string.IsNullOrWhiteSpace(expandedPath) || expandedPath.Length < 3 || expandedPath[1] != ':') return false;
        if (expandedPath.Contains(@"\Program Files\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            return !File.Exists(expandedPath);
        }
        catch
        {
            return false;
        }
    }

    private static readonly string[] CommandExtensions = [".exe", ".lnk", ".msc", ".bat", ".cmd", ".com", ".ico"];

    public static string? ResolveExecutablePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (expanded.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
        {
            expanded = expanded[4..];
        }
        if (expanded.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                expanded[12..]);
        }

        if (expanded.StartsWith('"'))
        {
            var endQuote = expanded.IndexOf('"', 1);
            if (endQuote > 1)
            {
                var candidate = expanded[1..endQuote].Trim();
                var remainingArgs = expanded[(endQuote + 1)..].Trim();
                return ResolveSquirrelOrDirect(candidate, remainingArgs);
            }
        }

        if (SafeFileExists(expanded))
        {
            return expanded;
        }

        foreach (var ext in CommandExtensions)
        {
            var searchIndex = 0;
            while (searchIndex < expanded.Length)
            {
                var match = expanded.IndexOf(ext, searchIndex, StringComparison.OrdinalIgnoreCase);
                if (match < 0) break;

                var afterExt = match + ext.Length;
                var validBoundary = afterExt == expanded.Length ||
                                    char.IsWhiteSpace(expanded[afterExt]) ||
                                    expanded[afterExt] == ',';
                if (validBoundary)
                {
                    var candidate = expanded[..afterExt].Trim();
                    var remainingArgs = expanded[afterExt..].Trim();
                    if (SafeFileExists(candidate))
                    {
                        return ResolveSquirrelOrDirect(candidate, remainingArgs);
                    }
                }

                searchIndex = afterExt;
            }
        }

        var spaceIdx = expanded.IndexOfAny([' ', '\t']);
        var firstToken = (spaceIdx > 0 ? expanded[..spaceIdx] : expanded).Trim('"');
        if (SafeFileExists(firstToken))
        {
            return firstToken;
        }

        if (!string.IsNullOrWhiteSpace(firstToken) && !Path.IsPathRooted(firstToken))
        {
            var searchDirs = new List<string>
            {
                Environment.SystemDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            };

            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(pathVar))
            {
                searchDirs.AddRange(pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
            }

            foreach (var dir in searchDirs)
            {
                try
                {
                    var direct = Path.Combine(dir.Trim(), firstToken);
                    if (SafeFileExists(direct)) return direct;

                    if (!Path.HasExtension(firstToken))
                    {
                        var withExe = Path.Combine(dir.Trim(), firstToken + ".exe");
                        if (SafeFileExists(withExe)) return withExe;
                    }
                }
                catch
                {
                    // Skip malformed PATH directories.
                }
            }
        }

        return null;
    }

    private static string? ResolveSquirrelOrDirect(string path, string remainingArgs)
    {
        if (!SafeFileExists(path))
        {
            return null;
        }

        if (Path.GetFileName(path).Equals("Update.exe", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(remainingArgs))
        {
            var rootDir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(rootDir))
            {
                var targetName = ExtractArgValue(remainingArgs, "--processStart");
                if (!string.IsNullOrWhiteSpace(targetName))
                {
                    try
                    {
                        foreach (var appDir in Directory.GetDirectories(rootDir, "app-*")
                            .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                        {
                            var targetExe = Path.Combine(appDir, Path.GetFileName(targetName));
                            if (SafeFileExists(targetExe))
                            {
                                return targetExe;
                            }
                        }
                    }
                    catch { }
                }

                var appIco = Path.Combine(rootDir, "app.ico");
                if (SafeFileExists(appIco))
                {
                    return appIco;
                }
            }
        }

        return path;
    }

    private static string? ExtractArgValue(string args, string flagName)
    {
        var idx = args.IndexOf(flagName, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var rest = args[(idx + flagName.Length)..].TrimStart(' ', '\t', '=');
        if (string.IsNullOrWhiteSpace(rest)) return null;

        if (rest.StartsWith('"'))
        {
            var end = rest.IndexOf('"', 1);
            return end > 1 ? rest[1..end] : null;
        }

        var endSpace = rest.IndexOfAny([' ', '\t']);
        return endSpace > 0 ? rest[..endSpace] : rest;
    }

    private static bool SafeFileExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
