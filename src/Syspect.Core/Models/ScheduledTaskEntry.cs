namespace Syspect.Core.Models;

public sealed record ScheduledTaskEntry(
    string TaskPath,
    string TaskName,
    string FullPath,
    bool Enabled,
    string State,
    string Triggers,
    string Action,
    string TargetPath,
    bool IsMissingTarget,
    bool IsCleanupCandidate,
    string Note)
{
    public string StatusLabel => !Enabled
        ? "Disabled"
        : IsMissingTarget
            ? "Orphaned (Missing File)"
            : "Active";
}
