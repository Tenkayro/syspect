using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Syspect.Core.Models;

namespace Syspect.Core.Services;

/// <summary>
/// Orchestrates SSD checks, chkdsk /scan, DISM, SFC, optional cleanup, and log persistence.
/// </summary>
public sealed partial class HealthRepairService
{
    public static string DefaultAppLogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "syspect",
        "logs");

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*%")]
    private static partial Regex PercentRegex();

    private readonly SystemInfoService _systemInfoService;
    private readonly QuickCleanupService _quickCleanupService;

    public HealthRepairService(SystemInfoService systemInfoService, QuickCleanupService quickCleanupService)
    {
        _systemInfoService = systemInfoService;
        _quickCleanupService = quickCleanupService;
    }

    public static IReadOnlyList<RepairStepDefinition> GetDefaultSteps() =>
    [
        new(RepairStepId.SsdCheck, "SSD & S.M.A.R.T.", "Checks NVMe/SSD health, temperature, and wear counters", true),
        new(RepairStepId.ChkdskScan, "Filesystem (chkdsk C: /scan)", "Online VSS snapshot scan without false-positive bitmap errors", true),
        new(RepairStepId.DismCheckHealth, "DISM CheckHealth", "Fast pre-check of Windows Component Store health", true),
        new(RepairStepId.DismScanHealth, "DISM ScanHealth", "Deep integrity scan of the WinSxS component store", true),
        new(RepairStepId.DismRestoreHealth, "DISM RestoreHealth", "Repairs corrupt Windows packages via Windows Update", true),
        new(RepairStepId.SfcScannow, "System Files (sfc /scannow)", "Verifies and repairs protected Windows system files", true),
        new(RepairStepId.DismComponentCleanup, "DISM StartComponentCleanup", "Removes superseded update files from WinSxS", true),
        new(RepairStepId.OptionalCleanup, "Optional Cleanup", "Disable DiagTrack + remove orphaned firewall rules/tasks/temp", false)
    ];

    public async Task<IReadOnlyList<RepairStepResult>> RunPipelineAsync(
        IReadOnlyList<RepairStepId> selectedSteps,
        IProgress<RepairProgressUpdate>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DefaultAppLogDirectory);
        var logFilePath = Path.Combine(DefaultAppLogDirectory, $"syspect-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var logBuilder = new StringBuilder();
        logBuilder.AppendLine($"syspect Repair Log — Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        logBuilder.AppendLine(new string('=', 60));

        void EmitLine(string text)
        {
            logBuilder.AppendLine(text);
            progress?.Report(new RepairProgressUpdate(null, null, null, text, null));
        }

        var results = new List<RepairStepResult>();
        int? lastRestoreExitCode = null;
        int totalSteps = Math.Max(1, selectedSteps.Count);

        for (int idx = 0; idx < selectedSteps.Count; idx++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stepId = selectedSteps[idx];
            var def = GetDefaultSteps().First(d => d.Id == stepId);
            double basePercent = (idx * 100.0) / totalSteps;

            progress?.Report(new RepairProgressUpdate(stepId, RepairStepState.Running, basePercent, $"\n=== {def.Title} ===", null));
            logBuilder.AppendLine($"\n=== {def.Title} ===");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            RepairStepResult stepResult;

            try
            {
                if (stepId == RepairStepId.DismComponentCleanup && lastRestoreExitCode is not null and not 0)
                {
                    sw.Stop();
                    EmitLine($"Skipped because DISM RestoreHealth exited with code {lastRestoreExitCode}.");
                    stepResult = new RepairStepResult(stepId, def.Title, null, RepairStepState.Skipped, "Skipped (RestoreHealth != 0)", sw.Elapsed);
                }
                else
                {
                    stepResult = await ExecuteStepAsync(stepId, def.Title, basePercent, 100.0 / totalSteps, EmitLine, progress, cancellationToken).ConfigureAwait(false);
                    if (stepId == RepairStepId.DismRestoreHealth)
                    {
                        lastRestoreExitCode = stepResult.ExitCode;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                EmitLine("[CANCELLED]");
                stepResult = new RepairStepResult(stepId, def.Title, null, RepairStepState.Skipped, "Cancelled by user", sw.Elapsed);
                results.Add(stepResult);
                progress?.Report(new RepairProgressUpdate(stepId, stepResult.State, null, null, stepResult));
                break;
            }
            catch (Exception ex)
            {
                sw.Stop();
                EmitLine($"[ERROR] {ex.Message}");
                stepResult = new RepairStepResult(stepId, def.Title, 1, RepairStepState.Failed, ex.Message, sw.Elapsed);
            }

            results.Add(stepResult);
            progress?.Report(new RepairProgressUpdate(stepId, stepResult.State, ((idx + 1) * 100.0) / totalSteps, null, stepResult));
        }

        logBuilder.AppendLine("\n" + new string('=', 60));
        logBuilder.AppendLine("Summary:");
        foreach (var r in results)
        {
            logBuilder.AppendLine($"  {r.Title,-34} | Code: {r.ExitCodeDisplay,-4} | {r.State} ({r.DurationDisplay}) | {r.Summary}");
        }

        await File.WriteAllTextAsync(logFilePath, logBuilder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), CancellationToken.None).ConfigureAwait(false);
        progress?.Report(new RepairProgressUpdate(null, null, 100, $"\nLog saved: {logFilePath}", null, logFilePath));
        return results;
    }

    public async Task<(bool Success, string Message)> ScheduleChkdskOnRebootAsync(CancellationToken cancellationToken = default)
    {
        var confirmChar = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase)
            ? "J\r\n"
            : "Y\r\n";

        var (code, output) = await ProcessRunner.RunAsync(
            "chkdsk.exe",
            "C: /f",
            ProcessRunner.GetAnsiEncoding(),
            stdinText: confirmChar,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return (code == 0 || output.Contains("neustart", StringComparison.OrdinalIgnoreCase) || output.Contains("restart", StringComparison.OrdinalIgnoreCase), output.Trim());
    }

    private async Task<RepairStepResult> ExecuteStepAsync(
        RepairStepId stepId,
        string title,
        double basePercent,
        double stepWeight,
        Action<string> emitLine,
        IProgress<RepairProgressUpdate>? progress,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (stepId == RepairStepId.SsdCheck)
        {
            var disks = await _systemInfoService.GetDiskHealthAsync(ct).ConfigureAwait(false);
            foreach (var d in disks)
            {
                emitLine($"{d.FriendlyName} ({d.BusType}/{d.MediaType}) — Health: {d.HealthStatus} ({d.OperationalStatus}), Temp: {d.TemperatureDisplay}, Wear: {d.WearDisplay}, {d.CapacityDisplay}");
            }
            sw.Stop();
            bool allHealthy = disks.Count > 0 && disks.All(d => d.IsHealthy);
            return new RepairStepResult(stepId, title, allHealthy ? 0 : 1, allHealthy ? RepairStepState.Succeeded : RepairStepState.Warning,
                allHealthy ? "All drives healthy" : "Drive health warning", sw.Elapsed);
        }

        if (stepId == RepairStepId.OptionalCleanup)
        {
            var cleanup = await _quickCleanupService.RunSafeOneClickCleanupAsync(ct).ConfigureAwait(false);
            foreach (var detail in cleanup.Details)
            {
                emitLine($" • {detail}");
            }
            sw.Stop();
            return new RepairStepResult(stepId, title, 0, RepairStepState.Succeeded, $"{cleanup.ActionsExecuted} action(s) completed", sw.Elapsed);
        }

        var (exe, args, encoding) = stepId switch
        {
            RepairStepId.ChkdskScan => ("chkdsk.exe", "C: /scan", ProcessRunner.GetAnsiEncoding()),
            RepairStepId.DismCheckHealth => ("DISM.exe", "/Online /Cleanup-Image /CheckHealth", ProcessRunner.GetOemEncoding()),
            RepairStepId.DismScanHealth => ("DISM.exe", "/Online /Cleanup-Image /ScanHealth", ProcessRunner.GetOemEncoding()),
            RepairStepId.DismRestoreHealth => ("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", ProcessRunner.GetOemEncoding()),
            RepairStepId.SfcScannow => ("sfc.exe", "/scannow", Encoding.Unicode),
            RepairStepId.DismComponentCleanup => ("DISM.exe", "/Online /Cleanup-Image /StartComponentCleanup", ProcessRunner.GetOemEncoding()),
            _ => throw new ArgumentOutOfRangeException(nameof(stepId))
        };

        if (stepId == RepairStepId.DismRestoreHealth)
        {
            emitLine("Note: DISM RestoreHealth commonly pauses around ~62% for 1-2 minutes while downloading/verifying payloads.");
        }

        var lastProgressTick = 0L;
        var (exitCode, _) = await ProcessRunner.RunAsync(
            exe,
            args,
            encoding,
            line =>
            {
                if (string.IsNullOrWhiteSpace(line)) return;

                var m = PercentRegex().Match(line);
                if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture, out var pct))
                {
                    var now = Environment.TickCount64;
                    if (now - lastProgressTick >= 200 || pct >= 100)
                    {
                        lastProgressTick = now;
                        progress?.Report(new RepairProgressUpdate(stepId, RepairStepState.Running, basePercent + (Math.Clamp(pct, 0, 100) / 100.0 * stepWeight), null, null));
                    }

                    if (pct < 100 && (line.TrimStart().StartsWith('[') || line.Contains("Phase:", StringComparison.Ordinal)))
                    {
                        return;
                    }
                }

                emitLine(line);
            },
            cancellationToken: ct).ConfigureAwait(false);

        sw.Stop();
        var state = exitCode == 0 ? RepairStepState.Succeeded : RepairStepState.Failed;
        var summary = exitCode == 0 ? "Completed successfully" : $"Exited with code {exitCode}";
        return new RepairStepResult(stepId, title, exitCode, state, summary, sw.Elapsed);
    }
}
