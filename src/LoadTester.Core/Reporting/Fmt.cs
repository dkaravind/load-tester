using System.Globalization;

namespace LoadTester.Core.Reporting;

internal static class Fmt
{
    public static string Ms(double ms) => ms switch
    {
        >= 10_000 => (ms / 1000).ToString("F1", CultureInfo.InvariantCulture) + "s",
        >= 1_000 => (ms / 1000).ToString("F2", CultureInfo.InvariantCulture) + "s",
        >= 100 => ms.ToString("F0", CultureInfo.InvariantCulture) + "ms",
        _ => ms.ToString("F1", CultureInfo.InvariantCulture) + "ms",
    };

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("F2", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("F1", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("F1", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };

    public static string Num(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
