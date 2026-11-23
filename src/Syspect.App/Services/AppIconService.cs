using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Windows.Storage.Streams;

namespace Syspect.App.Services;

public sealed record IconLoadTarget(
    string CacheKey,
    string? FilePath,
    string? PackageFamilyName,
    Action<ImageSource> Apply);

/// <summary>
/// Loads and caches Win32 shell icons (.exe/.lnk) and packaged UWP app logos without blocking the UI thread.
/// </summary>
public sealed class AppIconService
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;
    private const uint DibRgbColors = 0;

    private readonly Dictionary<string, ImageSource?> _uiCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, Package>? _uwpPackages;

    private sealed record RawBgraIcon(int Width, int Height, byte[] Pixels);

    public async Task PopulateIconsAsync(IReadOnlyList<IconLoadTarget> targets, CancellationToken cancellationToken = default)
    {
        if (targets.Count == 0)
        {
            return;
        }

        var uncachedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var uncachedUwp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pendingTargets = new List<IconLoadTarget>();

        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.CacheKey))
            {
                continue;
            }

            if (_uiCache.TryGetValue(target.CacheKey, out var cached))
            {
                if (cached is not null)
                {
                    target.Apply(cached);
                }
                continue;
            }

            pendingTargets.Add(target);

            if (!string.IsNullOrWhiteSpace(target.FilePath))
            {
                uncachedFiles.TryAdd(target.CacheKey, target.FilePath);
            }
            else if (!string.IsNullOrWhiteSpace(target.PackageFamilyName))
            {
                uncachedUwp.TryAdd(target.CacheKey, target.PackageFamilyName);
            }
        }

        if (pendingTargets.Count == 0)
        {
            return;
        }

        if (uncachedFiles.Count > 0)
        {
            var rawMap = await RunOnStaThreadAsync(() =>
            {
                var extracted = new Dictionary<string, RawBgraIcon?>(StringComparer.OrdinalIgnoreCase);
                foreach (var (cacheKey, filePath) in uncachedFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    extracted[cacheKey] = ExtractShellIcon(filePath);
                }
                return extracted;
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();

            foreach (var (cacheKey, raw) in rawMap)
            {
                ImageSource? bitmap = raw is not null ? CreateBitmapSource(raw) : null;
                _uiCache[cacheKey] = bitmap;
            }
        }

        if (uncachedUwp.Count > 0)
        {
            var pkgMap = await EnsureUwpPackageMapAsync(cancellationToken);
            foreach (var (cacheKey, familyName) in uncachedUwp)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_uiCache.ContainsKey(cacheKey))
                {
                    continue;
                }

                ImageSource? uwpBitmap = null;
                if (pkgMap.TryGetValue(familyName, out var pkg))
                {
                    uwpBitmap = await CreateUwpIconAsync(pkg);
                }
                _uiCache[cacheKey] = uwpBitmap;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        foreach (var target in pendingTargets)
        {
            if (_uiCache.TryGetValue(target.CacheKey, out var loaded) && loaded is not null)
            {
                target.Apply(loaded);
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, Package>> EnsureUwpPackageMapAsync(CancellationToken cancellationToken)
    {
        if (_uwpPackages is not null)
        {
            return _uwpPackages;
        }

        var map = await Task.Run(() =>
        {
            var dict = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var pm = new PackageManager();
                foreach (var pkg in pm.FindPackagesForUser(string.Empty))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    dict.TryAdd(pkg.Id.FamilyName, pkg);
                }
            }
            catch
            {
                // Ignore PackageManager access errors.
            }
            return dict;
        }, cancellationToken);

        _uwpPackages = map;
        return map;
    }

    private static async Task<ImageSource?> CreateUwpIconAsync(Package pkg)
    {
        try
        {
            var entries = await pkg.GetAppListEntriesAsync();
            if (entries is { Count: > 0 })
            {
                var logoRef = entries[0].DisplayInfo.GetLogo(new Windows.Foundation.Size(48, 48));
                if (logoRef is not null)
                {
                    using var stream = await logoRef.OpenReadAsync();
                    if (stream.Size > 0 && stream.Size <= 4 * 1024 * 1024)
                    {
                        using var reader = new DataReader(stream.GetInputStreamAt(0));
                        await reader.LoadAsync((uint)stream.Size);
                        var bytes = new byte[(int)stream.Size];
                        reader.ReadBytes(bytes);

                        using var ms = new MemoryStream(bytes);
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = 32;
                        bitmap.StreamSource = ms;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
            }
        }
        catch
        {
            // Fall back to Package.Logo URI.
        }

        try
        {
            if (pkg.Logo is { IsFile: true } logoUri && File.Exists(logoUri.LocalPath))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = logoUri;
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 32;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }
        catch
        {
            // Ignore malformed package logo URIs.
        }

        return null;
    }

    private static BitmapSource CreateBitmapSource(RawBgraIcon raw)
    {
        var bmp = BitmapSource.Create(
            raw.Width,
            raw.Height,
            96,
            96,
            PixelFormats.Pbgra32,
            null,
            raw.Pixels,
            raw.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    private static RawBgraIcon? ExtractShellIcon(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return null;
            }

            var shfi = new SHFILEINFOW();
            var result = SHGetFileInfoW(
                filePath,
                0,
                ref shfi,
                (uint)Marshal.SizeOf<SHFILEINFOW>(),
                ShgfiIcon | ShgfiLargeIcon);

            if (result == 0 || shfi.hIcon == 0)
            {
                return null;
            }

            nint hIcon = shfi.hIcon;
            nint hdc = 0;
            var iconInfo = new ICONINFO();

            try
            {
                if (!GetIconInfo(hIcon, out iconInfo) || iconInfo.hbmColor == 0)
                {
                    return null;
                }

                if (GetObjectW(iconInfo.hbmColor, Marshal.SizeOf<BITMAP>(), out var bmp) == 0)
                {
                    return null;
                }

                var width = bmp.bmWidth;
                var height = Math.Abs(bmp.bmHeight);
                if (width <= 0 || height <= 0 || width > 512 || height > 512)
                {
                    return null;
                }

                hdc = GetDC(0);
                if (hdc == 0)
                {
                    return null;
                }

                var bmi = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                };

                var pixels = new byte[width * height * 4];
                if (GetDIBits(hdc, iconInfo.hbmColor, 0, (uint)height, pixels, ref bmi, DibRgbColors) == 0)
                {
                    return null;
                }

                var hasAlpha = false;
                for (var i = 3; i < pixels.Length; i += 4)
                {
                    if (pixels[i] != 0)
                    {
                        hasAlpha = true;
                        break;
                    }
                }

                if (!hasAlpha && iconInfo.hbmMask != 0)
                {
                    var maskPixels = new byte[pixels.Length];
                    if (GetDIBits(hdc, iconInfo.hbmMask, 0, (uint)height, maskPixels, ref bmi, DibRgbColors) != 0)
                    {
                        for (var i = 0; i < pixels.Length; i += 4)
                        {
                            pixels[i + 3] = maskPixels[i] == 0 ? (byte)255 : (byte)0;
                        }
                        hasAlpha = true;
                    }
                }

                if (!hasAlpha)
                {
                    for (var i = 3; i < pixels.Length; i += 4)
                    {
                        pixels[i] = 255;
                    }
                }
                else
                {
                    for (var i = 0; i < pixels.Length; i += 4)
                    {
                        var a = pixels[i + 3];
                        if (a == 0)
                        {
                            pixels[i] = 0;
                            pixels[i + 1] = 0;
                            pixels[i + 2] = 0;
                        }
                        else if (a < 255)
                        {
                            pixels[i] = (byte)((pixels[i] * a) / 255);
                            pixels[i + 1] = (byte)((pixels[i + 1] * a) / 255);
                            pixels[i + 2] = (byte)((pixels[i + 2] * a) / 255);
                        }
                    }
                }

                return new RawBgraIcon(width, height, pixels);
            }
            finally
            {
                if (hdc != 0) ReleaseDC(0, hdc);
                if (iconInfo.hbmColor != 0) DeleteObject(iconInfo.hbmColor);
                if (iconInfo.hbmMask != 0) DeleteObject(iconInfo.hbmMask);
                DestroyIcon(hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    private static Task<T> RunOnStaThreadAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }
                tcs.TrySetResult(work());
            }
            catch (OperationCanceledException oce)
            {
                tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "SyspectIconLoader"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFOW psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(nint hWnd, nint hDc);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW", ExactSpelling = true)]
    private static extern int GetObjectW(nint h, int c, out BITMAP pv);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int GetDIBits(
        nint hdc,
        nint hbm,
        uint start,
        uint cLines,
        [Out] byte[] lpvBits,
        ref BITMAPINFOHEADER lpbmi,
        uint usage);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);
}
