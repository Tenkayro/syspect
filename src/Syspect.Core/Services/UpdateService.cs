using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Syspect.Core.Models;

namespace Syspect.Core.Services;

/// <summary>
/// Trim-safe GitHub Releases updater for syspect (checks Tenkayro/syspect, verifies SHA256SUMS.txt, and launches msiexec).
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryOwner = "Tenkayro";
    public const string RepositoryName = "syspect";
    public const string ReleasesPageUrl = $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases";
    private const string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";

    private readonly HttpClient _httpClient;
    private readonly string _currentVersionOverride;

    public UpdateService(HttpClient? httpClient = null, string? currentVersion = null)
    {
        _httpClient = httpClient ?? CreateDefaultHttpClient();
        _currentVersionOverride = string.IsNullOrWhiteSpace(currentVersion)
            ? GetCurrentVersion()
            : NormalizeVersionString(currentVersion);
    }

    public static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"syspect/{GetCurrentVersion()} (+https://tenkayro.de)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    public static string GetCurrentVersion(Assembly? targetAssembly = null)
    {
        var assembly = targetAssembly;
        if (assembly is null)
        {
            var entry = Assembly.GetEntryAssembly();
            var entryName = entry?.GetName().Name;
            assembly = entry is not null &&
                       (string.Equals(entryName, "syspect", StringComparison.OrdinalIgnoreCase) ||
                        (entryName?.StartsWith("Syspect", StringComparison.OrdinalIgnoreCase) ?? false))
                ? entry
                : typeof(UpdateService).Assembly;
        }

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var normalized = NormalizeVersionString(informational);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        var version = assembly.GetName().Version;
        return version is not null
            ? $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}"
            : "0.1.0";
    }

    public static string NormalizeVersionString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "0.0.0";
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[1..].Trim();
        }

        var plusIndex = trimmed.IndexOf('+');
        if (plusIndex >= 0)
        {
            trimmed = trimmed[..plusIndex];
        }

        return string.IsNullOrWhiteSpace(trimmed) ? "0.0.0" : trimmed;
    }

    /// <summary>
    /// Compares two SemVer strings (&gt; 0 means left is newer than right).
    /// </summary>
    public static int CompareVersions(string? left, string? right)
    {
        var (leftCore, leftPre) = SplitSemVer(NormalizeVersionString(left));
        var (rightCore, rightPre) = SplitSemVer(NormalizeVersionString(right));

        var maxLen = Math.Max(leftCore.Length, rightCore.Length);
        for (var i = 0; i < maxLen; i++)
        {
            var lVal = i < leftCore.Length ? leftCore[i] : 0;
            var rVal = i < rightCore.Length ? rightCore[i] : 0;
            if (lVal != rVal)
            {
                return lVal.CompareTo(rVal);
            }
        }

        var leftHasPre = !string.IsNullOrEmpty(leftPre);
        var rightHasPre = !string.IsNullOrEmpty(rightPre);
        if (!leftHasPre && !rightHasPre) return 0;
        if (!leftHasPre) return 1;
        if (!rightHasPre) return -1;

        var leftParts = leftPre!.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = rightPre!.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var preMax = Math.Max(leftParts.Length, rightParts.Length);
        for (var i = 0; i < preMax; i++)
        {
            if (i >= leftParts.Length) return -1;
            if (i >= rightParts.Length) return 1;

            var lIsNum = int.TryParse(leftParts[i], out var lNum);
            var rIsNum = int.TryParse(rightParts[i], out var rNum);

            if (lIsNum && rIsNum)
            {
                if (lNum != rNum) return lNum.CompareTo(rNum);
            }
            else if (lIsNum != rIsNum)
            {
                return lIsNum ? -1 : 1;
            }
            else
            {
                var cmp = string.Compare(leftParts[i], rightParts[i], StringComparison.OrdinalIgnoreCase);
                if (cmp != 0) return cmp;
            }
        }

        return 0;
    }

    public static UpdateReleaseInfo? ParseReleaseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var tagName = GetOptionalString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        var version = NormalizeVersionString(tagName);
        var releaseName = GetOptionalString(root, "name");
        if (string.IsNullOrWhiteSpace(releaseName))
        {
            releaseName = tagName;
        }

        var releaseNotes = GetOptionalString(root, "body") ?? string.Empty;
        var htmlUrl = GetOptionalString(root, "html_url") ?? ReleasesPageUrl;

        DateTimeOffset? publishedAt = null;
        if (root.TryGetProperty("published_at", out var pubProp) &&
            pubProp.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(pubProp.GetString(), out var parsedDate))
        {
            publishedAt = parsedDate;
        }

        string? msiUrl = null;
        string? msiName = null;
        long msiSize = 0;
        string? sha256Url = null;

        if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsProp.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object) continue;

                var assetName = GetOptionalString(asset, "name");
                var downloadUrl = GetOptionalString(asset, "browser_download_url");
                if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }

                if (assetName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) && msiUrl is null)
                {
                    msiName = assetName;
                    msiUrl = downloadUrl;
                    if (asset.TryGetProperty("size", out var sizeProp) && sizeProp.ValueKind == JsonValueKind.Number)
                    {
                        msiSize = sizeProp.GetInt64();
                    }
                }
                else if (string.Equals(assetName, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                {
                    sha256Url = downloadUrl;
                }
            }
        }

        return new UpdateReleaseInfo
        {
            TagName = tagName,
            Version = version,
            ReleaseName = releaseName,
            ReleaseNotes = releaseNotes,
            HtmlUrl = htmlUrl,
            MsiDownloadUrl = msiUrl,
            MsiFileName = msiName,
            MsiSizeBytes = msiSize,
            Sha256SumsDownloadUrl = sha256Url,
            PublishedAt = publishedAt
        };
    }

    public static string? ParseExpectedSha256(string sha256SumsContent, string targetFileName)
    {
        if (string.IsNullOrWhiteSpace(sha256SumsContent) || string.IsNullOrWhiteSpace(targetFileName))
        {
            return null;
        }

        var wantedName = Path.GetFileName(targetFileName.Trim());
        var lines = sha256SumsContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIdx = line.IndexOfAny([' ', '\t']);
            if (separatorIdx <= 0)
            {
                continue;
            }

            var hashToken = line[..separatorIdx].Trim();
            if (!IsHexSha256(hashToken))
            {
                continue;
            }

            var remainder = line[separatorIdx..].Trim().TrimStart('*');
            var candidateName = Path.GetFileName(remainder);
            if (string.Equals(candidateName, wantedName, StringComparison.OrdinalIgnoreCase))
            {
                return hashToken.ToLowerInvariant();
            }
        }

        return null;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = _currentVersionOverride;

        try
        {
            using var response = await _httpClient.GetAsync(LatestReleaseApiUrl, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateStatus.NoReleasesPublished,
                    CurrentVersion = currentVersion,
                    Message = $"No GitHub releases have been published for {RepositoryOwner}/{RepositoryName} yet (current: v{currentVersion})."
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateStatus.Error,
                    CurrentVersion = currentVersion,
                    Message = $"GitHub API returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
                };
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var release = ParseReleaseJson(json);
            if (release is null)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateStatus.Error,
                    CurrentVersion = currentVersion,
                    Message = "Could not parse release metadata from GitHub."
                };
            }

            if (CompareVersions(release.Version, currentVersion) > 0)
            {
                return new UpdateCheckResult
                {
                    Status = UpdateStatus.UpdateAvailable,
                    CurrentVersion = currentVersion,
                    LatestRelease = release,
                    Message = $"Update v{release.Version} is available (installed: v{currentVersion})."
                };
            }

            return new UpdateCheckResult
            {
                Status = UpdateStatus.UpToDate,
                CurrentVersion = currentVersion,
                LatestRelease = release,
                Message = $"syspect v{currentVersion} is up to date."
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                Status = UpdateStatus.Error,
                CurrentVersion = currentVersion,
                Message = $"Update check failed: {ex.Message}"
            };
        }
    }

    public async Task<string> DownloadAndVerifyMsiAsync(
        UpdateReleaseInfo release,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (string.IsNullOrWhiteSpace(release.MsiDownloadUrl) ||
            !Uri.TryCreate(release.MsiDownloadUrl, UriKind.Absolute, out var msiUri) ||
            !string.Equals(msiUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The GitHub release does not contain a valid HTTPS .msi installer URL.");
        }

        var stagingDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "syspect",
            "updates");
        Directory.CreateDirectory(stagingDir);

        var fileName = Path.GetFileName(string.IsNullOrWhiteSpace(release.MsiFileName)
            ? $"syspect-{release.Version}-win-x64.msi"
            : release.MsiFileName);

        var finalPath = Path.Combine(stagingDir, fileName);
        var partPath = finalPath + ".part";

        try
        {
            using (var response = await _httpClient.GetAsync(msiUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? release.MsiSizeBytes;
                await using var networkStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var fileStream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await networkStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                    totalRead += bytesRead;

                    if (totalBytes > 0)
                    {
                        progress?.Report(Math.Min(99.5, totalRead * 100.0 / totalBytes));
                    }
                    else
                    {
                        progress?.Report(-1);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(release.Sha256SumsDownloadUrl) &&
                Uri.TryCreate(release.Sha256SumsDownloadUrl, UriKind.Absolute, out var shaUri) &&
                string.Equals(shaUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(99.9);
                var sumsText = await _httpClient.GetStringAsync(shaUri, cancellationToken).ConfigureAwait(false);
                var expectedHash = ParseExpectedSha256(sumsText, fileName);

                if (!string.IsNullOrWhiteSpace(expectedHash))
                {
                    await using var verifyStream = File.OpenRead(partPath);
                    var hashBytes = await SHA256.HashDataAsync(verifyStream, cancellationToken).ConfigureAwait(false);
                    var actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

                    if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"SHA-256 verification failed for {fileName}. Expected {expectedHash}, got {actualHash}.");
                    }
                }
            }

            File.Move(partPath, finalPath, overwrite: true);
            progress?.Report(100.0);
            return finalPath;
        }
        catch
        {
            TryDeleteFile(partPath);
            throw;
        }
    }

    public static bool TryLaunchMsiUpgrade(string msiPath, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(msiPath) || !File.Exists(msiPath))
        {
            errorMessage = "Downloaded installer file was not found.";
            return false;
        }

        try
        {
            var programFilesExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "syspect",
                "syspect.exe");

            var relaunchPath = File.Exists(programFilesExe)
                ? programFilesExe
                : (Environment.ProcessPath ?? programFilesExe);

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 2 /nobreak >nul & start \"\" /wait msiexec.exe /i \"{msiPath}\" /qb & if exist \"{relaunchPath}\" start \"\" \"{relaunchPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public static void OpenUrlInBrowser(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true
        });
    }

    private static (int[] Core, string? PreRelease) SplitSemVer(string version)
    {
        var dashIdx = version.IndexOf('-');
        var corePart = dashIdx >= 0 ? version[..dashIdx] : version;
        var prePart = dashIdx >= 0 ? version[(dashIdx + 1)..] : null;

        var segments = corePart.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var parsed = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            parsed[i] = int.TryParse(segments[i], out var n) ? n : 0;
        }

        return (parsed, prePart);
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private static bool IsHexSha256(string value)
    {
        if (value.Length != 64) return false;
        foreach (var ch in value)
        {
            var isHex = (ch >= '0' && ch <= '9') ||
                        (ch >= 'a' && ch <= 'f') ||
                        (ch >= 'A' && ch <= 'F');
            if (!isHex) return false;
        }
        return true;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup errors on temp files.
        }
    }
}
