using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Syspect.Core.Models;

namespace Syspect.Core.Services;

/// <summary>
/// Parses C:\Windows\Logs\CBS\CBS.log without locking it to extract DISM corruption/repair summaries and SFC runs.
/// </summary>
public sealed partial class CbsLogParserService
{
    public const string DefaultCbsLogPath = @"C:\Windows\Logs\CBS\CBS.log";

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}),")]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"\[SR\] Repairing (\d+) components")]
    private static partial Regex SfcRepairingRegex();

    public Task<CbsRepairReport> ParseAsync(string? logPath = null, CancellationToken cancellationToken = default)
    {
        var targetPath = string.IsNullOrWhiteSpace(logPath) ? DefaultCbsLogPath : logPath;

        return Task.Run(() =>
        {
            if (!File.Exists(targetPath))
            {
                return new CbsRepairReport(
                    LogExists: false,
                    LastRepairTimestamp: null,
                    TotalDetectedCorruption: 0,
                    TotalRepairedCorruption: 0,
                    PayloadCorruptionsFixed: 0,
                    FileFlagsCorruptFixed: 0,
                    SecondsBetweenDetectionAndRepair: null,
                    RepairedFiles: [],
                    LastSfcTimestamp: null,
                    LastSfcSummary: "CBS.log not found",
                    RawSummaryText: "C:\\Windows\\Logs\\CBS\\CBS.log does not exist.");
            }

            var currentFiles = new List<string>();
            var latestFiles = new List<string>();
            DateTime? currentReadinessTime = null;
            DateTime? latestReadinessTime = null;

            bool inReadinessSummary = false;
            int totalDetected = 0;
            int totalRepaired = 0;
            int payloadFixed = 0;
            int fileFlagsFixed = 0;
            double? secondsGap = null;

            int tempDetected = 0;
            int tempRepaired = 0;
            int tempPayload = 0;
            int tempFileFlags = 0;
            double? tempSeconds = null;

            DateTime? lastSfcTime = null;
            string lastSfcSummary = "No SFC scan in current CBS.log";

            using var fs = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (line.Contains("Checking System Update Readiness.", StringComparison.Ordinal))
                {
                    currentFiles.Clear();
                    currentReadinessTime = TryParseTimestamp(line);
                    inReadinessSummary = false;
                    tempDetected = 0;
                    tempRepaired = 0;
                    tempPayload = 0;
                    tempFileFlags = 0;
                    tempSeconds = null;
                    continue;
                }

                if (line.Contains("CSI Payload Corrupt", StringComparison.Ordinal) &&
                    line.Contains("(Fixed)", StringComparison.Ordinal))
                {
                    var item = ExtractFixedFileName(line);
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        currentFiles.Add(item);
                    }
                    continue;
                }

                if (line.EndsWith("Summary:", StringComparison.Ordinal))
                {
                    inReadinessSummary = true;
                    continue;
                }

                if (inReadinessSummary)
                {
                    if (TryExtractAfterColonInt(line, "Total Detected Corruption:", out var v1)) tempDetected = v1;
                    else if (TryExtractAfterColonInt(line, "CSI Payload Corruption:", out var v2)) tempPayload = v2;
                    else if (TryExtractAfterColonInt(line, "CSI FileFlags Corrupt:", out var v3)) tempFileFlags = v3;
                    else if (TryExtractAfterColonInt(line, "Total Repaired Corruption:", out var v4)) tempRepaired = v4;
                    else if (line.Contains("Seconds between corruption and repair:", StringComparison.Ordinal))
                    {
                        var part = line.Split(':').LastOrDefault()?.Trim();
                        if (double.TryParse(part, CultureInfo.InvariantCulture, out var sec))
                        {
                            tempSeconds = sec;
                        }

                        latestReadinessTime = currentReadinessTime ?? TryParseTimestamp(line);
                        latestFiles = [.. currentFiles];
                        totalDetected = tempDetected;
                        totalRepaired = tempRepaired;
                        payloadFixed = tempPayload;
                        fileFlagsFixed = tempFileFlags;
                        secondsGap = tempSeconds;
                        inReadinessSummary = false;
                    }
                    else if (line.Contains("Exec: Processing complete", StringComparison.Ordinal))
                    {
                        latestReadinessTime = currentReadinessTime ?? TryParseTimestamp(line);
                        latestFiles = [.. currentFiles];
                        totalDetected = tempDetected;
                        totalRepaired = tempRepaired;
                        payloadFixed = tempPayload;
                        fileFlagsFixed = tempFileFlags;
                        secondsGap = tempSeconds;
                        inReadinessSummary = false;
                    }
                }

                if (line.Contains("[SR]", StringComparison.Ordinal))
                {
                    var m = SfcRepairingRegex().Match(line);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var count))
                    {
                        lastSfcTime = TryParseTimestamp(line);
                        lastSfcSummary = count == 0
                            ? "0 components repaired (no integrity violations)"
                            : $"{count} component(s) repaired";
                    }
                    else if (line.Contains("[SR] Cannot repair", StringComparison.Ordinal))
                    {
                        lastSfcTime = TryParseTimestamp(line);
                        lastSfcSummary = "One or more files could not be repaired";
                    }
                }
            }

            var rawBuilder = new StringBuilder();
            if (latestReadinessTime.HasValue)
            {
                rawBuilder.AppendLine($"Latest DISM Component Store Repair: {latestReadinessTime:yyyy-MM-dd HH:mm:ss}");
                rawBuilder.AppendLine($"Detected Issues: {totalDetected:N0} | Repaired: {totalRepaired:N0}");
                rawBuilder.AppendLine($"  • Corrupt Payload Files (CSI Payload): {payloadFixed}");
                rawBuilder.AppendLine($"  • Corrupt File Flags (CSI FileFlags): {fileFlagsFixed:N0}");
                if (secondsGap.HasValue && secondsGap.Value > 0)
                {
                    var span = TimeSpan.FromSeconds(secondsGap.Value);
                    rawBuilder.AppendLine($"  • Corruption Age Before Repair: {(int)span.TotalDays}d {span.Hours}h");
                }
                if (latestFiles.Count > 0)
                {
                    rawBuilder.AppendLine();
                    rawBuilder.AppendLine("Repaired Payload Files:");
                    foreach (var f in latestFiles)
                    {
                        rawBuilder.AppendLine($"  [Fixed] {f}");
                    }
                }
            }
            else
            {
                rawBuilder.AppendLine("No completed DISM repair summary found in current CBS.log.");
            }

            rawBuilder.AppendLine();
            rawBuilder.AppendLine($"Latest SFC Status: {(lastSfcTime.HasValue ? $"{lastSfcTime:yyyy-MM-dd HH:mm:ss} — " : "")}{lastSfcSummary}");

            return new CbsRepairReport(
                LogExists: true,
                LastRepairTimestamp: latestReadinessTime,
                TotalDetectedCorruption: totalDetected,
                TotalRepairedCorruption: totalRepaired,
                PayloadCorruptionsFixed: payloadFixed,
                FileFlagsCorruptFixed: fileFlagsFixed,
                SecondsBetweenDetectionAndRepair: secondsGap,
                RepairedFiles: latestFiles,
                LastSfcTimestamp: lastSfcTime,
                LastSfcSummary: lastSfcSummary,
                RawSummaryText: rawBuilder.ToString().TrimEnd());
        }, cancellationToken);
    }

    private static DateTime? TryParseTimestamp(string line)
    {
        var m = TimestampRegex().Match(line);
        if (m.Success && DateTime.TryParseExact(
            m.Groups[1].Value,
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var dt))
        {
            return dt;
        }
        return null;
    }

    private static bool TryExtractAfterColonInt(string line, string marker, out int value)
    {
        value = 0;
        var idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return false;

        var tail = line[(idx + marker.Length)..].Trim();
        return int.TryParse(tail, CultureInfo.InvariantCulture, out value);
    }

    private static string ExtractFixedFileName(string line)
    {
        var idx = line.IndexOf("(Fixed)", StringComparison.Ordinal);
        if (idx < 0) return string.Empty;

        var componentAndFile = line[(idx + "(Fixed)".Length)..].Trim();
        var slashIdx = componentAndFile.IndexOf('\\');
        if (slashIdx >= 0 && slashIdx + 1 < componentAndFile.Length)
        {
            var filePart = componentAndFile[(slashIdx + 1)..];
            var compPart = componentAndFile[..slashIdx];
            var shortComp = compPart.Split('_').Skip(1).FirstOrDefault() ?? compPart;
            return $"{filePart} ({shortComp})";
        }
        return componentAndFile;
    }
}
