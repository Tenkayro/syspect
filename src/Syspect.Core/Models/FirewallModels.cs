namespace Syspect.Core.Models;

public sealed record FirewallProfileStatus(
    string Name,
    bool Enabled,
    string InboundPolicy,
    string OutboundPolicy,
    bool NotifyOnListen)
{
    public string StatusDisplay => Enabled ? "ON" : "OFF";
}

public sealed record FirewallRuleItem(
    string Name,
    string Group,
    string Profiles,
    string Protocol,
    string LocalPort,
    string RemoteAddress,
    string Program,
    bool IsOrphaned,
    bool IsDownloadsPath,
    bool IsCleanupCandidate)
{
    public string TagLabel => IsDownloadsPath
        ? "Downloads Leftover"
        : IsCleanupCandidate
            ? "Legacy Rule"
            : IsOrphaned
                ? "Orphaned"
                : "Active";
}

public sealed record FirewallAuditResult(
    IReadOnlyList<FirewallProfileStatus> Profiles,
    string NetworkInterfaceName,
    string NetworkCategory,
    bool RdpEnabled,
    bool WinRmRunning,
    bool RemoteRegistryEnabled,
    IReadOnlyList<string> CustomSmbShares,
    IReadOnlyList<FirewallRuleItem> OrphanedRules,
    IReadOnlyList<FirewallRuleItem> PublicActiveDevRules,
    int TotalEnabledInboundAllowRules);
