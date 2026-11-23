using System.Management;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Syspect.Core.Models;

namespace Syspect.Core.Services;

/// <summary>
/// Audits Windows Firewall profiles, network categories, remote access vectors, and orphaned/public rules.
/// </summary>
public sealed partial class FirewallAuditService
{
    private static readonly HashSet<string> CleanupRuleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "anydesk.exe", "adb.exe", "songify.exe", "ACSETUP",
        "ArmouryHtmlDebugServer", "ArmourySocketServer", "Framework Service", "Tailscale-Process"
    };

    [GeneratedRegex(@"(python|node|java|openjdk|anydesk|teamviewer|rustdesk|localsend|streamdash|opencode|tailscale|ngrok|cloudflared|caddy|nginx|apache|horizon-server)", RegexOptions.IgnoreCase)]
    private static partial Regex DevOrRemoteRuleRegex();

    public async Task<FirewallAuditResult> AuditAsync(CancellationToken cancellationToken = default)
    {
        var profilesTask = GetFirewallProfilesAsync(cancellationToken);
        var rulesTask = GetInboundAllowRulesAsync(cancellationToken);

        await Task.WhenAll(profilesTask, rulesTask).ConfigureAwait(false);

        var (ifName, netCategory) = GetActiveNetworkCategory();
        var (rdpEnabled, winRmRunning, remoteRegEnabled, customShares) = GetRemoteVectorStatus();

        var allRules = rulesTask.Result;
        var orphaned = allRules
            .Where(r => r.IsOrphaned)
            .OrderByDescending(r => r.IsDownloadsPath)
            .ThenByDescending(r => r.IsCleanupCandidate)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var publicDevRules = allRules
            .Where(r => !r.IsOrphaned &&
                        (r.Profiles.Contains("Öffentlich", StringComparison.OrdinalIgnoreCase) ||
                         r.Profiles.Contains("Public", StringComparison.OrdinalIgnoreCase)) &&
                        DevOrRemoteRuleRegex().IsMatch($"{r.Name} {r.Program}"))
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FirewallAuditResult(
            Profiles: profilesTask.Result,
            NetworkInterfaceName: ifName,
            NetworkCategory: netCategory,
            RdpEnabled: rdpEnabled,
            WinRmRunning: winRmRunning,
            RemoteRegistryEnabled: remoteRegEnabled,
            CustomSmbShares: customShares,
            OrphanedRules: orphaned,
            PublicActiveDevRules: publicDevRules,
            TotalEnabledInboundAllowRules: allRules.Count);
    }

    public async Task<(bool Success, string Message)> DeleteRuleAsync(FirewallRuleItem rule, CancellationToken cancellationToken = default)
    {
        var escapedName = rule.Name.Replace("\"", "\\\"");
        var args = !string.IsNullOrWhiteSpace(rule.Program) &&
                   !rule.Program.Equals("Any", StringComparison.OrdinalIgnoreCase) &&
                   !rule.Program.Equals("Beliebig", StringComparison.OrdinalIgnoreCase)
            ? $"advfirewall firewall delete rule name=\"{escapedName}\" dir=in program=\"{rule.Program}\""
            : $"advfirewall firewall delete rule name=\"{escapedName}\" dir=in";

        var (code, output) = await ProcessRunner.RunAutoUtf8OrOemAsync("netsh.exe", args, cancellationToken).ConfigureAwait(false);
        return (code == 0, output.Trim());
    }

    private static async Task<IReadOnlyList<FirewallProfileStatus>> GetFirewallProfilesAsync(CancellationToken cancellationToken)
    {
        var (_, output) = await ProcessRunner.RunAutoUtf8OrOemAsync("netsh.exe", "advfirewall show allprofiles", cancellationToken).ConfigureAwait(false);
        var list = new List<FirewallProfileStatus>();

        string? currentName = null;
        bool enabled = false;
        string inbound = "";
        string outbound = "";
        bool notify = false;

        void CommitCurrent()
        {
            if (currentName is not null)
            {
                list.Add(new FirewallProfileStatus(currentName, enabled, inbound, outbound, notify));
            }
        }

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Domänenprofil", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Domain Profile", StringComparison.OrdinalIgnoreCase))
            {
                CommitCurrent();
                currentName = "Domain";
                enabled = false; inbound = ""; outbound = ""; notify = false;
            }
            else if (line.StartsWith("Privates Profil", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Private Profile", StringComparison.OrdinalIgnoreCase))
            {
                CommitCurrent();
                currentName = "Private";
                enabled = false; inbound = ""; outbound = ""; notify = false;
            }
            else if (line.StartsWith("Öffentliches Profil", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Public Profile", StringComparison.OrdinalIgnoreCase))
            {
                CommitCurrent();
                currentName = "Public";
                enabled = false; inbound = ""; outbound = ""; notify = false;
            }
            else if (currentName is not null)
            {
                if (line.StartsWith("Status", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("State", StringComparison.OrdinalIgnoreCase))
                {
                    enabled = line.EndsWith("EIN", StringComparison.OrdinalIgnoreCase) ||
                              line.EndsWith("ON", StringComparison.OrdinalIgnoreCase);
                }
                else if (line.StartsWith("Firewallrichtlinie", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("Firewall Policy", StringComparison.OrdinalIgnoreCase))
                {
                    inbound = line.Contains("BlockInbound", StringComparison.OrdinalIgnoreCase) ||
                              line.Contains("Eingehend blockieren", StringComparison.OrdinalIgnoreCase)
                        ? "Block Inbound"
                        : "Allow Inbound";
                    outbound = line.Contains("AllowOutbound", StringComparison.OrdinalIgnoreCase) ||
                               line.Contains("Ausgehend zulassen", StringComparison.OrdinalIgnoreCase)
                        ? "Allow Outbound"
                        : "Block Outbound";
                }
                else if (line.StartsWith("Eingehende Benutzerbenachrichtigung", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("Inbound User Notification", StringComparison.OrdinalIgnoreCase))
                {
                    notify = line.Contains("Aktivieren", StringComparison.OrdinalIgnoreCase) ||
                             line.Contains("Enable", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        CommitCurrent();
        return list;
    }

    private static async Task<IReadOnlyList<FirewallRuleItem>> GetInboundAllowRulesAsync(CancellationToken cancellationToken)
    {
        var (_, output) = await ProcessRunner.RunAutoUtf8OrOemAsync("netsh.exe", "advfirewall firewall show rule name=all dir=in verbose", cancellationToken).ConfigureAwait(false);
        var rules = new List<FirewallRuleItem>();

        var blocks = Regex.Split(output, @"(?:\r?\n){2,}");
        foreach (var block in blocks)
        {
            if (!block.Contains("Regelname:", StringComparison.Ordinal) &&
                !block.Contains("Rule Name:", StringComparison.Ordinal))
            {
                continue;
            }

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawLine in block.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var colonIdx = rawLine.IndexOf(':');
                if (colonIdx <= 0) continue;

                fields[rawLine[..colonIdx].Trim()] = rawLine[(colonIdx + 1)..].Trim();
            }

            var enabledVal = GetAny(fields, "Aktiviert", "Enabled");
            var actionVal = GetAny(fields, "Aktion", "Action");
            if (!IsTrueVal(enabledVal) || !IsAllowVal(actionVal)) continue;

            var name = GetAny(fields, "Regelname", "Rule Name");
            var group = GetAny(fields, "Gruppierung", "Grouping");
            var profiles = NormalizeProfiles(GetAny(fields, "Profile", "Profiles"));
            var proto = NormalizeAny(GetAny(fields, "Protokoll", "Protocol"));
            var localPort = NormalizeAny(GetAny(fields, "Lokaler Port", "LocalPort"));
            var remoteIp = NormalizeAny(GetAny(fields, "Remote-IP", "RemoteIP"));
            var rawProgram = NormalizeAny(GetAny(fields, "Programm", "Program"));

            var expandedProgram = string.IsNullOrWhiteSpace(rawProgram) ||
                                  rawProgram.Equals("Any", StringComparison.OrdinalIgnoreCase) ||
                                  rawProgram.Equals("System", StringComparison.OrdinalIgnoreCase)
                ? rawProgram
                : Environment.ExpandEnvironmentVariables(rawProgram);

            bool isOrphaned = IsProgramMissingOnDisk(expandedProgram);
            bool isDownloads = !string.IsNullOrEmpty(expandedProgram) &&
                               expandedProgram.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase);
            bool isCandidate = CleanupRuleNames.Contains(name);

            rules.Add(new FirewallRuleItem(
                Name: name,
                Group: group,
                Profiles: profiles,
                Protocol: proto,
                LocalPort: localPort,
                RemoteAddress: remoteIp,
                Program: expandedProgram,
                IsOrphaned: isOrphaned,
                IsDownloadsPath: isDownloads,
                IsCleanupCandidate: isCandidate));
        }

        return rules;
    }

    private static string NormalizeProfiles(string raw) => raw
        .Replace("Domäne", "Domain", StringComparison.OrdinalIgnoreCase)
        .Replace("Privat", "Private", StringComparison.OrdinalIgnoreCase)
        .Replace("Öffentlich", "Public", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeAny(string raw) =>
        raw.Equals("Beliebig", StringComparison.OrdinalIgnoreCase) ? "Any" : raw;

    private static bool IsProgramMissingOnDisk(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || path[1] != ':') return false;
        if (path.Contains(@"\Program Files\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static (string InterfaceName, string Category) GetActiveNetworkCategory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\StandardCimv2",
                "SELECT Name, InterfaceAlias, NetworkCategory FROM MSFT_NetConnectionProfile");
            foreach (ManagementObject obj in searcher.Get())
            {
                var iface = WmiHelper.GetString(obj, "InterfaceAlias");
                var name = WmiHelper.GetString(obj, "Name");
                var catNum = WmiHelper.GetNullableInt(obj, "NetworkCategory");
                var category = catNum switch
                {
                    0 => "Public (Guest)",
                    1 => "Private",
                    2 => "Domain",
                    _ => "Unknown"
                };
                return ($"{iface} ({name})", category);
            }
        }
        catch
        {
            // Fallback below.
        }

        return ("Ethernet", "Unknown");
    }

    private static (bool RdpEnabled, bool WinRmRunning, bool RemoteRegistryEnabled, IReadOnlyList<string> CustomShares) GetRemoteVectorStatus()
    {
        bool rdpEnabled = false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Terminal Server");
            if (key?.GetValue("fDenyTSConnections") is int i) rdpEnabled = i == 0;
        }
        catch
        {
            // Ignore registry error.
        }

        bool winRmRunning = false;
        bool remoteRegEnabled = false;
        try
        {
            using var winrm = new ServiceController("WinRM");
            winRmRunning = winrm.Status == ServiceControllerStatus.Running;
        }
        catch { }

        try
        {
            using var rr = new ServiceController("RemoteRegistry");
            remoteRegEnabled = rr.StartType != ServiceStartMode.Disabled;
        }
        catch { }

        var customShares = new List<string>();
        try
        {
            using var smbSearcher = new ManagementObjectSearcher(
                @"\\.\root\Microsoft\Windows\SMB",
                "SELECT Name, Path FROM MSFT_SmbShare");
            foreach (ManagementObject share in smbSearcher.Get())
            {
                var name = WmiHelper.GetString(share, "Name");
                if (!name.Equals("ADMIN$", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("C$", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("IPC$", StringComparison.OrdinalIgnoreCase))
                {
                    customShares.Add($"{name} ({WmiHelper.GetString(share, "Path")})");
                }
            }
        }
        catch { }

        return (rdpEnabled, winRmRunning, remoteRegEnabled, customShares);
    }

    private static string GetAny(Dictionary<string, string> dict, string key1, string key2)
    {
        if (dict.TryGetValue(key1, out var v1)) return v1;
        if (dict.TryGetValue(key2, out var v2)) return v2;
        return string.Empty;
    }

    private static bool IsTrueVal(string val) =>
        val.Equals("Ja", StringComparison.OrdinalIgnoreCase) ||
        val.Equals("Yes", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowVal(string val) =>
        val.Equals("Zulassen", StringComparison.OrdinalIgnoreCase) ||
        val.Equals("Allow", StringComparison.OrdinalIgnoreCase);
}
