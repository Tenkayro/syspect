using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using Syspect.Core.Models;

namespace Syspect.Core.Services;

public enum CbsLiveEventKind
{
    Phase,
    DownloadProgress,
    RepairedFile,
    DetectedCorruption,
    StoreSummary,
    SfcVerifyBatch,
    SfcRepair,
    Warning,
    Cleanup
}

public sealed record CbsLiveEvent(
    CbsLiveEventKind Kind,
    string Message,
    long? DownloadedBytes = null,
    long? TotalBytes = null,
    double? DownloadPercent = null,
    bool EmitToConsole = true);

/// <summary>
/// Streams real-time background progress during long-running repair steps by tailing CBS.log,
/// measuring active Windows Update download throughput, sampling worker CPU activity, and emitting clean milestones.
/// </summary>
public sealed partial class RepairLiveMonitor : IAsyncDisposable
{
    [GeneratedRegex(@"DWLD:\s*Downloading update\s*\((\d+)\s+of\s+(\d+)\s+bytes\)(?:.*?overall\s*-\s*(\d+(?:\.\d+)?)\s*%)?", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadRegex();

    [GeneratedRegex(@"WULib DownloadProgress:\s*\[(\d+)\s*/\s*(\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex WuLibProgressRegex();

    [GeneratedRegex(@"\[SR\]\s*Repairing\s+(\d+)\s+components", RegexOptions.IgnoreCase)]
    private static partial Regex SfcRepairingRegex();

    [GeneratedRegex(@"\[SR\]\s*Verifying\s+(\d+)\s+components", RegexOptions.IgnoreCase)]
    private static partial Regex SfcVerifyingRegex();

    [GeneratedRegex(@"\[SR\]\s*Cannot repair member file\s+\[[^\]]*\]""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SfcCannotRepairRegex();

    [GeneratedRegex(@"\[SR\]\s*Repaired file\b[^\r\n]*?""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SfcRepairedFileRegex();

    [GeneratedRegex(@"Total Detected Corruption:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TotalDetectedRegex();

    [GeneratedRegex(@"Total Repaired Corruption:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TotalRepairedRegex();

    private readonly object _stateLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    private string? _phaseText;
    private string? _stdoutPhaseHint;
    private double? _stepPercent;
    private long _startedTickMs;
    private long _lastConsoleActivityTickMs;

    private long _cbsOffset;
    private long _lastDownloadLogTickMs;
    private double _lastDownloadLogPercent = -10.0;
    private long? _downloadReceivedBytes;
    private long? _downloadTotalBytes;
    private double? _downloadPercent;
    private double? _downloadBytesPerSec;
    private TimeSpan? _downloadEta;
    private readonly List<(long TickMs, long Bytes)> _downloadSamples = [];

    private int _sfcBatches;
    private long _lastSfcLogTickMs;
    private int _lastSfcLogBatch;
    private string? _lastEmittedCbsMessage;

    private long _prevNetBytes = -1;
    private long _prevNetTickMs;
    private double? _systemNetDownBps;
    private string? _activeWorkerText;
    private readonly Dictionary<int, (long TickMs, TimeSpan CpuTime, string Name)> _prevProcCpu = [];

    public static CbsLiveEvent? ParseCbsLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        // Filter out high-frequency metadata noise emitted by Servicing / MumX / SIL.
        if (line.Contains("TurboContainer", StringComparison.Ordinal) ||
            line.Contains("QueryFileHash", StringComparison.Ordinal) ||
            line.Contains("Direct SIL provider", StringComparison.Ordinal) ||
            line.Contains("Discovering MumX", StringComparison.Ordinal) ||
            line.Contains("Shim considered", StringComparison.Ordinal) ||
            line.Contains("GetOneSettings", StringComparison.Ordinal) ||
            line.Contains("CDeploymentSession: Deserialize", StringComparison.Ordinal))
        {
            return null;
        }

        var dlMatch = DownloadRegex().Match(line);
        if (dlMatch.Success &&
            long.TryParse(dlMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dlBytes) &&
            long.TryParse(dlMatch.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalBytes))
        {
            double pct;
            if (dlMatch.Groups[3].Success &&
                double.TryParse(dlMatch.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedPct))
            {
                pct = Math.Clamp(parsedPct, 0, 100);
            }
            else
            {
                pct = totalBytes > 0 ? Math.Clamp(dlBytes * 100.0 / totalBytes, 0, 100) : 0;
            }

            return new CbsLiveEvent(
                CbsLiveEventKind.DownloadProgress,
                $"Downloading Windows Update payload: {pct:0}% ({FormatBytes(dlBytes)} / {FormatBytes(totalBytes)})",
                DownloadedBytes: dlBytes,
                TotalBytes: totalBytes,
                DownloadPercent: pct,
                EmitToConsole: false);
        }

        var wuLibMatch = WuLibProgressRegex().Match(line);
        if (wuLibMatch.Success &&
            double.TryParse(wuLibMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var wuCur) &&
            double.TryParse(wuLibMatch.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var wuTotal) &&
            wuTotal > 0)
        {
            double pct = Math.Clamp(wuCur * 100.0 / wuTotal, 0, 100);
            return new CbsLiveEvent(
                CbsLiveEventKind.Phase,
                $"Preparing Windows Update download ({pct:0}%)...",
                DownloadPercent: pct,
                EmitToConsole: false);
        }

        if (line.Contains("Calling WindowsUpdateDownloadFromUUP", StringComparison.Ordinal))
        {
            return new CbsLiveEvent(
                CbsLiveEventKind.Phase,
                "Contacting Windows Update (UUP) for repair payloads...");
        }

        if (line.Contains("Acquirer generated ActionList path", StringComparison.Ordinal))
        {
            return new CbsLiveEvent(
                CbsLiveEventKind.Phase,
                "Resolved Windows Update repair manifest (ActionList.xml)");
        }

        if (line.Contains("Calling PostDownload on WUClient Acquirer", StringComparison.Ordinal))
        {
            return new CbsLiveEvent(
                CbsLiveEventKind.Phase,
                "Download finished — verifying & extracting repair payloads...");
        }

        if (line.Contains("Checking System Update Readiness.", StringComparison.Ordinal))
        {
            return new CbsLiveEvent(
                CbsLiveEventKind.Phase,
                "Checking System Update Readiness & WinSxS store...");
        }

        if (line.Contains("Checking Package Manifests and Catalogs", StringComparison.Ordinal))
        {
            return new CbsLiveEvent(CbsLiveEventKind.Phase, "Checking package manifests and catalogs...");
        }

        if (line.Contains("Checking Package Watchlist", StringComparison.Ordinal))
        {
            return new CbsLiveEvent(CbsLiveEventKind.Phase, "Checking package watchlist...");
        }

        if (line.Contains("Checking Component Watchlist", StringComparison.Ordinal))
        {
            return new CbsLiveEvent(CbsLiveEventKind.Phase, "Checking component watchlist...");
        }

        if (line.Contains("Checking Component Store", StringComparison.Ordinal))
        {
            return new CbsLiveEvent(CbsLiveEventKind.Phase, "Checking WinSxS component store...");
        }

        if (line.Contains("Checking Packages", StringComparison.Ordinal) &&
            !line.Contains("Checking Packages.", StringComparison.OrdinalIgnoreCase))
        {
            return new CbsLiveEvent(CbsLiveEventKind.Phase, "Checking installed Windows packages...");
        }

        if (line.Contains("CSI Payload Corrupt", StringComparison.Ordinal))
        {
            if (line.Contains("(Fixed)", StringComparison.Ordinal))
            {
                var item = ExtractPayloadTarget(line, "(Fixed)");
                return new CbsLiveEvent(
                    CbsLiveEventKind.RepairedFile,
                    string.IsNullOrWhiteSpace(item) ? "Repaired corrupt payload file" : $"Repaired corrupt payload: {item}");
            }

            var corruptItem = ExtractPayloadTarget(line, "CSI Payload Corrupt");
            return new CbsLiveEvent(
                CbsLiveEventKind.DetectedCorruption,
                string.IsNullOrWhiteSpace(corruptItem) ? "Detected corrupt payload file" : $"Detected corrupt payload: {corruptItem}");
        }

        var detectedMatch = TotalDetectedRegex().Match(line);
        if (detectedMatch.Success && int.TryParse(detectedMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var detectedCount))
        {
            return new CbsLiveEvent(
                CbsLiveEventKind.StoreSummary,
                $"Total detected corruption in component store: {detectedCount.ToString("N0", CultureInfo.InvariantCulture)}");
        }

        var repairedMatch = TotalRepairedRegex().Match(line);
        if (repairedMatch.Success && int.TryParse(repairedMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var repairedCount))
        {
            return new CbsLiveEvent(
                CbsLiveEventKind.StoreSummary,
                $"Total repaired corruption in component store: {repairedCount.ToString("N0", CultureInfo.InvariantCulture)}");
        }

        if (line.Contains("[SR]", StringComparison.Ordinal))
        {
            if (line.Contains("[SR] Verify complete", StringComparison.Ordinal))
            {
                return new CbsLiveEvent(
                    CbsLiveEventKind.SfcVerifyBatch,
                    "Completed SFC verification batch",
                    EmitToConsole: false);
            }

            var verifyMatch = SfcVerifyingRegex().Match(line);
            if (verifyMatch.Success && int.TryParse(verifyMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var vCount))
            {
                return new CbsLiveEvent(
                    CbsLiveEventKind.Phase,
                    $"Verifying {vCount.ToString("N0", CultureInfo.InvariantCulture)} protected Windows components...",
                    EmitToConsole: false);
            }

            var repairMatch = SfcRepairingRegex().Match(line);
            if (repairMatch.Success && int.TryParse(repairMatch.Groups[1].Value, CultureInfo.InvariantCulture, out var rCount))
            {
                return new CbsLiveEvent(
                    CbsLiveEventKind.SfcRepair,
                    rCount == 0
                        ? "SFC verification complete: 0 corruptions found"
                        : $"SFC repairing {rCount.ToString("N0", CultureInfo.InvariantCulture)} corrupt component(s)...",
                    EmitToConsole: rCount > 0);
            }

            if (line.Contains("[SR] Cannot repair", StringComparison.Ordinal))
            {
                var cannotMatch = SfcCannotRepairRegex().Match(line);
                var file = cannotMatch.Success
                    ? cannotMatch.Groups[1].Value
                    : ExtractTailAfterMarker(line, "[SR] Cannot repair");
                return new CbsLiveEvent(
                    CbsLiveEventKind.Warning,
                    string.IsNullOrWhiteSpace(file) ? "SFC reported an unrepairable file" : $"Cannot repair protected file: {file}");
            }

            if (line.Contains("[SR] Repaired file", StringComparison.Ordinal))
            {
                var repairedFileMatch = SfcRepairedFileRegex().Match(line);
                var file = repairedFileMatch.Success
                    ? repairedFileMatch.Groups[1].Value
                    : ExtractTailAfterMarker(line, "[SR] Repaired file");
                return new CbsLiveEvent(
                    CbsLiveEventKind.RepairedFile,
                    string.IsNullOrWhiteSpace(file) ? "Repaired protected system file" : $"Repaired protected file: {file}");
            }
        }

        if (line.Contains("Scavenge:", StringComparison.Ordinal))
        {
            if (line.Contains("Starts", StringComparison.OrdinalIgnoreCase))
            {
                return new CbsLiveEvent(CbsLiveEventKind.Cleanup, "Starting WinSxS component cleanup (Scavenge)...");
            }
            if (line.Contains("Completed", StringComparison.OrdinalIgnoreCase))
            {
                return new CbsLiveEvent(CbsLiveEventKind.Cleanup, "Completed WinSxS component cleanup (Scavenge).");
            }
        }

        if (line.Contains("Exec: Processing complete", StringComparison.Ordinal))
        {
            return new CbsLiveEvent(CbsLiveEventKind.Phase, "Servicing session processing complete.");
        }

        return null;
    }

    public static string FormatBytes(long bytes)
    {
        var clamped = Math.Max(0L, bytes);
        if (clamped < 1024L)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{clamped} B");
        }
        if (clamped < 1024L * 1024L)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{clamped / 1024.0:0.0} KB");
        }
        if (clamped < 1024L * 1024L * 1024L)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{clamped / (1024.0 * 1024.0):0.0} MB");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{clamped / (1024.0 * 1024.0 * 1024.0):0.00} GB");
    }

    public static string FormatSpeed(double bytesPerSec)
    {
        if (!double.IsFinite(bytesPerSec) || bytesPerSec <= 0)
        {
            return "0.0 MB/s";
        }
        if (bytesPerSec < 1024.0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytesPerSec:0} B/s");
        }
        if (bytesPerSec < 1024.0 * 1024.0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytesPerSec / 1024.0:0.0} KB/s");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{bytesPerSec / (1024.0 * 1024.0):0.00} MB/s");
    }

    public static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }
        if (span.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h {span.Minutes:D2}m");
        }
        if (span.TotalMinutes >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes}m {span.Seconds:D2}s");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)Math.Round(span.TotalSeconds))}s");
    }

    public static string FormatEta(TimeSpan? eta)
    {
        if (!eta.HasValue || eta.Value < TimeSpan.Zero)
        {
            return "--";
        }
        if (eta.Value.TotalSeconds < 1.0)
        {
            return "<1s";
        }
        int totalSec = (int)Math.Ceiling(eta.Value.TotalSeconds);
        if (totalSec < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{totalSec}s");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{totalSec / 60}m {totalSec % 60:D2}s");
    }

    public void Start(
        RepairStepId stepId,
        Action<string> emitConsoleLine,
        Action<RepairLiveTelemetry> emitTelemetry,
        string? cbsLogPath = null)
    {
        _cts = new CancellationTokenSource();
        var now = Environment.TickCount64;
        _startedTickMs = now;
        _lastConsoleActivityTickMs = now;
        _prevNetTickMs = now;

        bool tailCbs = ShouldTailCbs(stepId);
        string resolvedCbsPath = string.IsNullOrWhiteSpace(cbsLogPath)
            ? CbsLogParserService.DefaultCbsLogPath
            : cbsLogPath;

        if (tailCbs)
        {
            _cbsOffset = CaptureInitialFileLength(resolvedCbsPath);
        }

        var token = _cts.Token;
        _loopTask = Task.Run(async () =>
        {
            int tick = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                    tick++;
                    long tickNow = Environment.TickCount64;

                    if (tailCbs)
                    {
                        ReadNewCbsLines(resolvedCbsPath, tickNow, emitConsoleLine);
                    }

                    if (tick % 2 == 0)
                    {
                        SampleNetworkDown(tickNow);
                        SampleWorkerCpu(stepId, tickNow);
                        MaybeEmitHeartbeat(stepId, tickNow, emitConsoleLine);
                        emitTelemetry(BuildSnapshot(tickNow));
                    }
                    else
                    {
                        bool hasActiveDownload;
                        lock (_stateLock)
                        {
                            hasActiveDownload = _downloadTotalBytes is > 0;
                        }
                        if (hasActiveDownload)
                        {
                            // Refresh UI more frequently while an active Windows Update payload download is streaming.
                            emitTelemetry(BuildSnapshot(tickNow));
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when step completes or is cancelled.
            }
        }, CancellationToken.None);
    }

    public void ReportStdoutProgress(double? percent, string? phaseHint = null)
    {
        lock (_stateLock)
        {
            if (percent.HasValue)
            {
                _stepPercent = Math.Clamp(percent.Value, 0, 100);
            }
            if (!string.IsNullOrWhiteSpace(phaseHint))
            {
                _stdoutPhaseHint = phaseHint;
                if (_downloadTotalBytes is null or <= 0)
                {
                    _phaseText = phaseHint;
                }
            }
            _lastConsoleActivityTickMs = Environment.TickCount64;
        }
    }

    public void MarkStdoutActivity()
    {
        lock (_stateLock)
        {
            _lastConsoleActivityTickMs = Environment.TickCount64;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
        {
            return;
        }

        try
        {
            _cts.Cancel();
            if (_loopTask is not null)
            {
                await _loopTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during teardown.
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _loopTask = null;
        }
    }

    private static bool ShouldTailCbs(RepairStepId stepId) => stepId is
        RepairStepId.DismCheckHealth or
        RepairStepId.DismScanHealth or
        RepairStepId.DismRestoreHealth or
        RepairStepId.SfcScannow or
        RepairStepId.DismComponentCleanup;

    private static long CaptureInitialFileLength(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }
        }
        catch
        {
            // Ignore transient lock on initial file size read.
        }
        return 0;
    }

    private void ReadNewCbsLines(string cbsPath, long nowTickMs, Action<string> emitConsoleLine)
    {
        var newLines = new List<string>();
        try
        {
            if (!File.Exists(cbsPath))
            {
                return;
            }

            using var fs = new FileStream(cbsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _cbsOffset)
            {
                _cbsOffset = 0;
            }
            if (fs.Length == _cbsOffset)
            {
                return;
            }

            fs.Seek(_cbsOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: _cbsOffset == 0);
            while (reader.ReadLine() is { } line)
            {
                newLines.Add(line);
            }
            _cbsOffset = fs.Position;
        }
        catch
        {
            return;
        }

        foreach (var raw in newLines)
        {
            var ev = ParseCbsLine(raw);
            if (ev is null)
            {
                continue;
            }

            ProcessParsedCbsEvent(ev, nowTickMs, emitConsoleLine);
        }
    }

    private void ProcessParsedCbsEvent(CbsLiveEvent ev, long nowTickMs, Action<string> emitConsoleLine)
    {
        if (ev.Kind == CbsLiveEventKind.DownloadProgress && ev.TotalBytes is > 0 && ev.DownloadedBytes is >= 0)
        {
            double? speedBps = null;
            TimeSpan? eta = null;
            double pct = ev.DownloadPercent ?? 0;

            lock (_stateLock)
            {
                _downloadReceivedBytes = ev.DownloadedBytes;
                _downloadTotalBytes = ev.TotalBytes;
                _downloadPercent = pct;
                _phaseText = ev.Message;

                _downloadSamples.Add((nowTickMs, ev.DownloadedBytes.Value));
                while (_downloadSamples.Count > 2 && nowTickMs - _downloadSamples[0].TickMs > 3000)
                {
                    _downloadSamples.RemoveAt(0);
                }

                if (_downloadSamples.Count >= 2)
                {
                    long dtMs = nowTickMs - _downloadSamples[0].TickMs;
                    long dBytes = ev.DownloadedBytes.Value - _downloadSamples[0].Bytes;
                    if (dtMs >= 250 && dBytes >= 0)
                    {
                        speedBps = (dBytes * 1000.0) / dtMs;
                        _downloadBytesPerSec = speedBps;
                        if (speedBps > 0)
                        {
                            long remaining = Math.Max(0, ev.TotalBytes.Value - ev.DownloadedBytes.Value);
                            eta = TimeSpan.FromSeconds(remaining / speedBps.Value);
                            _downloadEta = eta;
                        }
                    }
                }
            }

            bool crossedMilestone = pct >= 100.0 && _lastDownloadLogPercent < 100.0;
            bool jumped5Pct = pct - _lastDownloadLogPercent >= 5.0;
            bool intervalElapsed = pct > _lastDownloadLogPercent && nowTickMs - _lastDownloadLogTickMs >= 8000;

            if (crossedMilestone || jumped5Pct || intervalElapsed)
            {
                _lastDownloadLogPercent = pct;
                _lastDownloadLogTickMs = nowTickMs;

                var extra = speedBps is > 0
                    ? string.Create(CultureInfo.InvariantCulture, $" · {FormatSpeed(speedBps.Value)} · ETA {FormatEta(eta)}")
                    : string.Empty;
                EmitCbsConsoleLine($"  [CBS] {ev.Message}{extra}", nowTickMs, emitConsoleLine);
            }
            return;
        }

        if (ev.Kind == CbsLiveEventKind.SfcVerifyBatch)
        {
            int count;
            lock (_stateLock)
            {
                _sfcBatches++;
                count = _sfcBatches;
                _phaseText = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Verifying Windows system files ({count * 100:N0} components checked)...");
            }

            if (count - _lastSfcLogBatch >= 5 || nowTickMs - _lastSfcLogTickMs >= 10_000)
            {
                _lastSfcLogBatch = count;
                _lastSfcLogTickMs = nowTickMs;
                EmitCbsConsoleLine(
                    string.Create(CultureInfo.InvariantCulture, $"  [SFC] Verified ~{count * 100:N0} protected system components..."),
                    nowTickMs,
                    emitConsoleLine);
            }
            return;
        }

        // Do not overwrite active DWLD MB/GB phase with secondary WULib progress percentages.
        if (!ev.EmitToConsole && ev.Message.StartsWith("Preparing Windows Update download", StringComparison.Ordinal))
        {
            lock (_stateLock)
            {
                if (_downloadTotalBytes is null or <= 0)
                {
                    _phaseText = ev.Message;
                }
            }
            return;
        }

        if (ev.Kind is CbsLiveEventKind.Phase or CbsLiveEventKind.Cleanup)
        {
            lock (_stateLock)
            {
                _phaseText = ev.Message;
                if (ev.Message.StartsWith("Download finished", StringComparison.Ordinal) ||
                    ev.Message.StartsWith("Servicing session", StringComparison.Ordinal))
                {
                    _downloadReceivedBytes = null;
                    _downloadTotalBytes = null;
                    _downloadPercent = null;
                    _downloadBytesPerSec = null;
                    _downloadEta = null;
                    _downloadSamples.Clear();
                }
            }
        }

        if (ev.EmitToConsole)
        {
            var tag = ev.Kind == CbsLiveEventKind.SfcRepair ? "[SFC]" : "[CBS]";
            EmitCbsConsoleLine($"  {tag} {ev.Message}", nowTickMs, emitConsoleLine);
        }
    }

    private void EmitCbsConsoleLine(string formattedLine, long nowTickMs, Action<string> emitConsoleLine)
    {
        if (string.Equals(_lastEmittedCbsMessage, formattedLine, StringComparison.Ordinal))
        {
            return;
        }

        _lastEmittedCbsMessage = formattedLine;
        lock (_stateLock)
        {
            _lastConsoleActivityTickMs = nowTickMs;
        }
        emitConsoleLine(formattedLine);
    }

    private void SampleNetworkDown(long nowTickMs)
    {
        try
        {
            long totalRx = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }

                    totalRx += ni.GetIPStatistics().BytesReceived;
                }
                catch
                {
                    // Skip adapters that change state mid-query.
                }
            }

            if (_prevNetBytes >= 0)
            {
                long dtMs = nowTickMs - _prevNetTickMs;
                long dBytes = totalRx - _prevNetBytes;
                if (dtMs > 0 && dBytes >= 0)
                {
                    lock (_stateLock)
                    {
                        _systemNetDownBps = (dBytes * 1000.0) / dtMs;
                    }
                }
            }

            _prevNetBytes = totalRx;
            _prevNetTickMs = nowTickMs;
        }
        catch
        {
            // Best-effort network throughput sampling.
        }
    }

    private void SampleWorkerCpu(RepairStepId stepId, long nowTickMs)
    {
        ReadOnlySpan<string> targetNames = stepId switch
        {
            RepairStepId.ChkdskScan => ["chkdsk"],
            RepairStepId.SfcScannow => ["sfc", "TiWorker"],
            _ => ["TiWorker", "DismHost", "DISM"]
        };

        var seenPids = new HashSet<int>();
        string? bestWorker = null;
        double bestCpuPct = -1;
        int cpuCount = Math.Max(1, Environment.ProcessorCount);

        foreach (var procName in targetNames)
        {
            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName(procName);
            }
            catch
            {
                continue;
            }

            foreach (var proc in procs)
            {
                try
                {
                    int pid = proc.Id;
                    seenPids.Add(pid);
                    var cpuTime = proc.TotalProcessorTime;

                    if (_prevProcCpu.TryGetValue(pid, out var prev))
                    {
                        long dtMs = nowTickMs - prev.TickMs;
                        if (dtMs >= 200)
                        {
                            double cpuDeltaMs = (cpuTime - prev.CpuTime).TotalMilliseconds;
                            double pct = Math.Clamp((cpuDeltaMs / (dtMs * cpuCount)) * 100.0, 0, 100);
                            if (pct > bestCpuPct)
                            {
                                bestCpuPct = pct;
                                bestWorker = pct >= 1.0
                                    ? string.Create(CultureInfo.InvariantCulture, $"{procName} {pct:0}% CPU")
                                    : $"{procName} active";
                            }
                        }
                    }
                    else if (bestWorker is null)
                    {
                        bestWorker = $"{procName} active";
                    }

                    _prevProcCpu[pid] = (nowTickMs, cpuTime, procName);
                }
                catch
                {
                    // Protected or exited process; still record presence if name matched.
                    bestWorker ??= $"{procName} active";
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        foreach (var stalePid in _prevProcCpu.Keys.Where(pid => !seenPids.Contains(pid)).ToList())
        {
            _prevProcCpu.Remove(stalePid);
        }

        lock (_stateLock)
        {
            _activeWorkerText = bestWorker;
        }
    }

    private void MaybeEmitHeartbeat(RepairStepId stepId, long nowTickMs, Action<string> emitConsoleLine)
    {
        long lastActivity;
        string? phase;
        double? netDown;
        string? worker;

        lock (_stateLock)
        {
            lastActivity = _lastConsoleActivityTickMs;
            phase = _phaseText ?? _stdoutPhaseHint;
            netDown = _downloadBytesPerSec ?? _systemNetDownBps;
            worker = _activeWorkerText;
        }

        if (nowTickMs - lastActivity < 20_000)
        {
            return;
        }

        var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, nowTickMs - _startedTickMs));
        var parts = new List<string>
        {
            $"Still active ({FormatDuration(elapsed)})"
        };

        if (!string.IsNullOrWhiteSpace(worker))
        {
            parts.Add(worker);
        }
        else if (!string.IsNullOrWhiteSpace(phase))
        {
            parts.Add(phase);
        }
        else if (stepId == RepairStepId.DismRestoreHealth)
        {
            parts.Add("waiting for Windows servicing response");
        }

        if (netDown is >= 1024)
        {
            parts.Add($"Net ↓ {FormatSpeed(netDown.Value)}");
        }

        lock (_stateLock)
        {
            _lastConsoleActivityTickMs = nowTickMs;
        }
        emitConsoleLine($"  [Live] {string.Join(" · ", parts)}");
    }

    private RepairLiveTelemetry BuildSnapshot(long nowTickMs)
    {
        lock (_stateLock)
        {
            return new RepairLiveTelemetry(
                PhaseText: _phaseText ?? _stdoutPhaseHint,
                StepPercent: _stepPercent,
                StepElapsed: TimeSpan.FromMilliseconds(Math.Max(0, nowTickMs - _startedTickMs)),
                DownloadReceivedBytes: _downloadReceivedBytes,
                DownloadTotalBytes: _downloadTotalBytes,
                DownloadPercent: _downloadPercent,
                DownloadBytesPerSec: _downloadBytesPerSec,
                DownloadEta: _downloadEta,
                SystemNetworkDownBytesPerSec: _systemNetDownBps,
                ActiveWorkerText: _activeWorkerText);
        }
    }

    private static string ExtractPayloadTarget(string line, string marker)
    {
        int idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return string.Empty;
        }

        var componentAndFile = line[(idx + marker.Length)..].Trim();
        int slashIdx = componentAndFile.IndexOf('\\');
        if (slashIdx >= 0 && slashIdx + 1 < componentAndFile.Length)
        {
            var filePart = componentAndFile[(slashIdx + 1)..];
            var compPart = componentAndFile[..slashIdx];
            var shortComp = compPart.Split('_').Skip(1).FirstOrDefault() ?? compPart;
            return $"{filePart} ({shortComp})";
        }
        return componentAndFile;
    }

    private static string ExtractTailAfterMarker(string line, string marker)
    {
        int idx = line.IndexOf(marker, StringComparison.Ordinal);
        return idx >= 0 ? line[(idx + marker.Length)..].Trim() : string.Empty;
    }
}
