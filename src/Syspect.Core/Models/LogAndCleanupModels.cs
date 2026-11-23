namespace Syspect.Core.Models;

public sealed record LogFileEntry(
    string FileName,
    string FullPath,
    string SourceLabel,
    DateTime LastModified,
    long SizeBytes,
    bool IsCbsVirtualEntry = false)
{
    public string SizeDisplay => IsCbsVirtualEntry
        ? "Live Analysis"
        : SizeBytes >= 1024 * 1024
            ? $"{SizeBytes / (1024.0 * 1024.0):F1} MB"
            : $"{Math.Max(1, SizeBytes / 1024.0):F0} KB";

    public string ModifiedDisplay => LastModified.ToString("yyyy-MM-dd HH:mm");
}

public sealed record QuickCleanupSummary(
    int ActionsExecuted,
    IReadOnlyList<string> Details);
