using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Syspect.Core.Services;

/// <summary>
/// Executes native Windows CLI tools with accurate ANSI/OEM/UTF-16 decoding and live line streaming.
/// </summary>
public static class ProcessRunner
{
    static ProcessRunner()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static Encoding GetOemEncoding()
    {
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            return Encoding.GetEncoding(850);
        }
    }

    public static Encoding GetAnsiEncoding()
    {
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch
        {
            return Encoding.GetEncoding(1252);
        }
    }

    public static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        string arguments,
        Encoding? encoding = null,
        Action<string>? onLine = null,
        string? stdinText = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveEncoding = encoding ?? GetOemEncoding();
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinText is not null,
            StandardOutputEncoding = effectiveEncoding,
            StandardErrorEncoding = effectiveEncoding
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        if (stdinText is not null)
        {
            await process.StandardInput.WriteAsync(stdinText);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();
        }

        var buffer = new StringBuilder();
        var bufferLock = new object();

        using var reg = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort process tree termination on cancel.
            }
        });

        async Task ReadStreamAsync(StreamReader reader)
        {
            var charBuffer = new char[128];
            var lineBuilder = new StringBuilder();
            bool lastWasCr = false;
            string? lastBufferedLine = null;

            void FlushLine(bool allowDuplicateInBuffer)
            {
                if (lineBuilder.Length == 0)
                {
                    return;
                }

                var cleaned = lineBuilder.ToString();
                lineBuilder.Clear();

                lock (bufferLock)
                {
                    if (allowDuplicateInBuffer || !string.Equals(lastBufferedLine, cleaned, StringComparison.Ordinal))
                    {
                        buffer.AppendLine(cleaned);
                        lastBufferedLine = cleaned;
                    }
                }

                onLine?.Invoke(cleaned);
            }

            while (true)
            {
                int read = await reader.ReadAsync(charBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                for (int i = 0; i < read; i++)
                {
                    char c = charBuffer[i];
                    if (c == '\0')
                    {
                        continue;
                    }

                    if (c == '\n')
                    {
                        if (lastWasCr)
                        {
                            lastWasCr = false;
                            continue;
                        }

                        FlushLine(allowDuplicateInBuffer: true);
                        continue;
                    }

                    if (c == '\r')
                    {
                        lastWasCr = true;
                        if (lineBuilder.Length > 0)
                        {
                            FlushLine(allowDuplicateInBuffer: false);
                        }
                        continue;
                    }

                    lastWasCr = false;
                    lineBuilder.Append(c);

                    if (c == ']' && lineBuilder.Length >= 8)
                    {
                        var current = lineBuilder.ToString();
                        if (current.TrimStart().StartsWith('[') && current.Contains('%', StringComparison.Ordinal))
                        {
                            FlushLine(allowDuplicateInBuffer: false);
                        }
                    }
                    else if (c == '.' && lineBuilder.Length >= 14)
                    {
                        var current = lineBuilder.ToString();
                        if (current.Contains('%', StringComparison.Ordinal) &&
                            (current.Contains("abgeschlossen", StringComparison.OrdinalIgnoreCase) ||
                             current.Contains("complete", StringComparison.OrdinalIgnoreCase)))
                        {
                            FlushLine(allowDuplicateInBuffer: false);
                        }
                    }
                }
            }

            FlushLine(allowDuplicateInBuffer: true);
        }

        try
        {
            await Task.WhenAll(
                ReadStreamAsync(process.StandardOutput),
                ReadStreamAsync(process.StandardError)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Terminated via cancellation callback.
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        string combined;
        lock (bufferLock)
        {
            combined = buffer.ToString();
        }

        return (process.ExitCode, combined);
    }

    /// <summary>
    /// Runs a command (such as netsh) whose redirected stdout uses UTF-8 or OEM depending on console codepage.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunAutoUtf8OrOemAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        using var msOut = new MemoryStream();
        using var msErr = new MemoryStream();
        await Task.WhenAll(
            process.StandardOutput.BaseStream.CopyToAsync(msOut, cancellationToken),
            process.StandardError.BaseStream.CopyToAsync(msErr, cancellationToken)).ConfigureAwait(false);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var bytes = msOut.Length > 0 ? msOut.ToArray() : msErr.ToArray();
        string decoded;
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            decoded = strictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            decoded = GetOemEncoding().GetString(bytes);
        }

        return (process.ExitCode, decoded);
    }
}
