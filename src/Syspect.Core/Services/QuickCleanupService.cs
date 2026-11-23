using Syspect.Core.Models;

namespace Syspect.Core.Services;

/// <summary>
/// Safe one-click maintenance actions (DiagTrack disable, orphaned firewall/task cleanup, temp cleanup, NVIDIA tray restart).
/// </summary>
public sealed class QuickCleanupService
{
    private readonly StartupAndServiceAuditService _startupAndServiceAuditService;
    private readonly FirewallAuditService _firewallAuditService;

    public QuickCleanupService(
        StartupAndServiceAuditService startupAndServiceAuditService,
        FirewallAuditService firewallAuditService)
    {
        _startupAndServiceAuditService = startupAndServiceAuditService;
        _firewallAuditService = firewallAuditService;
    }

    public async Task<QuickCleanupSummary> RunSafeOneClickCleanupAsync(CancellationToken cancellationToken = default)
    {
        var details = new List<string>();
        int actions = 0;

        var (_, stopMsg) = await _startupAndServiceAuditService.SetServiceRunningStateAsync("DiagTrack", start: false).ConfigureAwait(false);
        var (diagOk, diagMsg) = await _startupAndServiceAuditService.SetServiceStartModeAsync("DiagTrack", "disabled", cancellationToken).ConfigureAwait(false);
        details.Add(diagOk ? "DiagTrack disabled." : $"DiagTrack: {diagMsg} ({stopMsg})");
        if (diagOk) actions++;

        var fw = await _firewallAuditService.AuditAsync(cancellationToken).ConfigureAwait(false);
        var targetRules = fw.OrphanedRules
            .Where(r => r.IsCleanupCandidate || r.IsDownloadsPath)
            .ToList();

        int deletedFw = 0;
        foreach (var rule in targetRules)
        {
            var (ok, _) = await _firewallAuditService.DeleteRuleAsync(rule, cancellationToken).ConfigureAwait(false);
            if (ok) deletedFw++;
        }
        details.Add($"Removed orphaned Downloads/legacy firewall rules: {deletedFw}");
        if (deletedFw > 0) actions++;

        var tasks = await _startupAndServiceAuditService.GetScheduledTasksAsync(cancellationToken).ConfigureAwait(false);
        int deletedTasks = 0;
        foreach (var t in tasks.Where(t => t.IsMissingTarget))
        {
            var (ok, _) = await _startupAndServiceAuditService.DeleteTaskAsync(t.FullPath).ConfigureAwait(false);
            if (ok) deletedTasks++;
        }
        details.Add($"Removed dead scheduled tasks (missing .exe): {deletedTasks}");
        if (deletedTasks > 0) actions++;

        int deletedTemp = CleanUserTempFiles();
        details.Add($"Removed stale user temp files: {deletedTemp}");
        if (deletedTemp > 0) actions++;

        return new QuickCleanupSummary(actions, details);
    }

    public async Task<(bool Success, string Message)> RestartNvidiaTrayAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (ok1, msg1) = await _startupAndServiceAuditService.RestartProtectedContainerServiceAsync("NVDisplay.ContainerLocalSystem").ConfigureAwait(false);
        var (ok2, msg2) = await _startupAndServiceAuditService.RestartProtectedContainerServiceAsync("NvContainerLocalSystem").ConfigureAwait(false);

        return (ok1 && ok2)
            ? (true, "NVIDIA container services restarted (tray icon should reappear).")
            : (false, $"Result: {msg1} | {msg2}");
    }

    private static int CleanUserTempFiles()
    {
        int removed = 0;
        var tempPath = Path.GetTempPath();
        if (!Directory.Exists(tempPath)) return 0;

        var cutoff = DateTime.Now.AddHours(-1);
        foreach (var file in Directory.EnumerateFiles(tempPath))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch { }
        }
        return removed;
    }
}
