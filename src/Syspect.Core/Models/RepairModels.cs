namespace Syspect.Core.Models;

public enum RepairStepId
{
    SsdCheck,
    ChkdskScan,
    DismCheckHealth,
    DismScanHealth,
    DismRestoreHealth,
    SfcScannow,
    DismComponentCleanup,
    OptionalCleanup
}

public enum RepairStepState
{
    Pending,
    Running,
    Succeeded,
    Warning,
    Failed,
    Skipped
}

public sealed record RepairStepDefinition(
    RepairStepId Id,
    string Title,
    string Description,
    bool DefaultSelected);

public sealed record RepairStepResult(
    RepairStepId Id,
    string Title,
    int? ExitCode,
    RepairStepState State,
    string Summary,
    TimeSpan Duration)
{
    public string ExitCodeDisplay => ExitCode.HasValue ? ExitCode.Value.ToString() : "-";
    public string DurationDisplay => Duration.TotalSeconds >= 60
        ? $"{(int)Duration.TotalMinutes}m {Duration.Seconds:D2}s"
        : $"{Duration.TotalSeconds:F1}s";
}

public sealed record RepairLiveTelemetry(
    string? PhaseText = null,
    double? StepPercent = null,
    TimeSpan? StepElapsed = null,
    long? DownloadReceivedBytes = null,
    long? DownloadTotalBytes = null,
    double? DownloadPercent = null,
    double? DownloadBytesPerSec = null,
    TimeSpan? DownloadEta = null,
    double? SystemNetworkDownBytesPerSec = null,
    string? ActiveWorkerText = null);

public sealed record RepairProgressUpdate(
    RepairStepId? StepId,
    RepairStepState? StepState,
    double? Percent,
    string? LogLine,
    RepairStepResult? CompletedResult,
    string? SavedLogPath = null,
    RepairLiveTelemetry? Telemetry = null);
