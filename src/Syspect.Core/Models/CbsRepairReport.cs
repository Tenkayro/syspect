namespace Syspect.Core.Models;

/// <summary>
/// Structured summary extracted from C:\Windows\Logs\CBS\CBS.log.
/// </summary>
public sealed record CbsRepairReport(
    bool LogExists,
    DateTime? LastRepairTimestamp,
    int TotalDetectedCorruption,
    int TotalRepairedCorruption,
    int PayloadCorruptionsFixed,
    int FileFlagsCorruptFixed,
    double? SecondsBetweenDetectionAndRepair,
    IReadOnlyList<string> RepairedFiles,
    DateTime? LastSfcTimestamp,
    string LastSfcSummary,
    string RawSummaryText)
{
    public string LastRepairDisplay => LastRepairTimestamp.HasValue
        ? LastRepairTimestamp.Value.ToString("yyyy-MM-dd HH:mm:ss")
        : "No DISM repair run found";

    public string LastSfcDisplay => LastSfcTimestamp.HasValue
        ? $"{LastSfcTimestamp.Value:yyyy-MM-dd HH:mm:ss} — {LastSfcSummary}"
        : LastSfcSummary;
}
