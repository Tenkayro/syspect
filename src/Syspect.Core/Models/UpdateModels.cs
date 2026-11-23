namespace Syspect.Core.Models;

/// <summary>
/// Outcome of checking GitHub Releases for a newer version of syspect.
/// </summary>
public enum UpdateStatus
{
    UpToDate,
    UpdateAvailable,
    NoReleasesPublished,
    Error
}

/// <summary>
/// Metadata extracted from the latest GitHub Release payload without reflection.
/// </summary>
public sealed class UpdateReleaseInfo
{
    public string TagName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string ReleaseName { get; init; } = string.Empty;
    public string ReleaseNotes { get; init; } = string.Empty;
    public string HtmlUrl { get; init; } = string.Empty;
    public string? MsiDownloadUrl { get; init; }
    public string? MsiFileName { get; init; }
    public long MsiSizeBytes { get; init; }
    public string? Sha256SumsDownloadUrl { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}

/// <summary>
/// Result of comparing the running syspect version against the latest GitHub Release.
/// </summary>
public sealed class UpdateCheckResult
{
    public UpdateStatus Status { get; init; }
    public string CurrentVersion { get; init; } = "0.0.0";
    public UpdateReleaseInfo? LatestRelease { get; init; }
    public string Message { get; init; } = string.Empty;

    public bool IsUpdateAvailable =>
        Status == UpdateStatus.UpdateAvailable && LatestRelease is not null;
}
