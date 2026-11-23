using System.Text;
using Syspect.Core.Models;

namespace Syspect.Core.Services;

/// <summary>
/// Discovers and reads saved syspect logs and legacy HealthCheck script logs.
/// </summary>
public sealed class LogRepositoryService
{
    private static IReadOnlyList<(string Path, string Label)> GetLogSources() =>
    [
        (HealthRepairService.DefaultAppLogDirectory, "syspect"),
        (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Scripts", "HealthCheck", "logs"), "Legacy Script")
    ];

    public Task<IReadOnlyList<LogFileEntry>> GetLogFilesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<LogFileEntry>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = new List<LogFileEntry>();

            foreach (var (dir, label) in GetLogSources())
            {
                if (!Directory.Exists(dir)) continue;

                foreach (var path in Directory.EnumerateFiles(dir, "*.log"))
                {
                    var fi = new FileInfo(path);
                    entries.Add(new LogFileEntry(
                        FileName: fi.Name,
                        FullPath: fi.FullName,
                        SourceLabel: label,
                        LastModified: fi.LastWriteTime,
                        SizeBytes: fi.Length));
                }
            }

            return entries
                .OrderByDescending(e => e.LastModified)
                .ToList();
        }, cancellationToken);
    }

    public async Task<string> ReadLogContentAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(fullPath))
        {
            return "Log file not found.";
        }

        using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}
