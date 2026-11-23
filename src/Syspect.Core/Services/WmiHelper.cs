using System.Globalization;
using System.Management;

namespace Syspect.Core.Services;

/// <summary>
/// Safe property extractors for WMI / CIM ManagementBaseObject queries.
/// </summary>
internal static class WmiHelper
{
    public static string GetString(ManagementBaseObject obj, string property, string fallback = "")
    {
        try
        {
            return obj[property]?.ToString()?.Trim() ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public static int? GetNullableInt(ManagementBaseObject obj, string property)
    {
        try
        {
            var value = obj[property];
            return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    public static ulong? GetNullableUlong(ManagementBaseObject obj, string property)
    {
        try
        {
            var value = obj[property];
            return value is null ? null : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    public static string FormatDmtfDate(string dmtf)
    {
        if (string.IsNullOrWhiteSpace(dmtf)) return string.Empty;
        try
        {
            return ManagementDateTimeConverter.ToDateTime(dmtf).ToString("yyyy-MM-dd");
        }
        catch
        {
            return dmtf.Length >= 8 ? $"{dmtf[..4]}-{dmtf[4..6]}-{dmtf[6..8]}" : dmtf;
        }
    }

    public static string[] GetStringArray(ManagementBaseObject obj, string property)
    {
        try
        {
            return obj[property] as string[] ?? [];
        }
        catch
        {
            return [];
        }
    }
}
