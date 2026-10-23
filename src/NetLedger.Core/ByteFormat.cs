using System.Globalization;

namespace NetLedger.Core;

public enum RateUnit
{
    AutoBits,
    Kbit,
    Mbit,
    AutoBytes,
    KBps,
    MBps,
}

public enum SizeBase
{
    /// <summary>1 KB = 1024 bytes, like Windows Explorer.</summary>
    Binary,

    /// <summary>1 KB = 1000 bytes, like most ISPs bill.</summary>
    Decimal,
}

public static class ByteFormat
{
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static SizeBase SizeBase { get; set; } = SizeBase.Binary;

    private static double Step => SizeBase == SizeBase.Binary ? 1024.0 : 1000.0;

    /// <summary>Formats a transfer rate, e.g. "423 Mbit" or "52.9 MB/s".</summary>
    public static string Rate(double bytesPerSecond, RateUnit unit)
    {
        bytesPerSecond = Math.Max(0, bytesPerSecond);
        double bits = bytesPerSecond * 8;
        return unit switch
        {
            RateUnit.Kbit => Number(bits / 1e3) + " Kbit",
            RateUnit.Mbit => Number(bits / 1e6) + " Mbit",
            RateUnit.KBps => Number(bytesPerSecond / Step) + " KB/s",
            RateUnit.MBps => Number(bytesPerSecond / (Step * Step)) + " MB/s",
            RateUnit.AutoBytes => AutoBytes(bytesPerSecond),
            _ => AutoBits(bits),
        };
    }

    /// <summary>The widest string <see cref="Rate"/> can produce for a unit, for stable layout.</summary>
    public static string WidestRate(RateUnit unit) => unit switch
    {
        RateUnit.Kbit or RateUnit.Mbit or RateUnit.AutoBits => Number(88.8) + " Mbit",
        _ => Number(88.8) + " MB/s",
    };

    // Thresholds sit just below 1000 of a unit so values never round up to "1000 Kbit".
    private const double RoundsToThousand = 999.95;

    private static string AutoBits(double bits)
    {
        if (bits < RoundsToThousand * 1e3)
        {
            return Number(bits / 1e3) + " Kbit";
        }

        return bits < RoundsToThousand * 1e6 ? Number(bits / 1e6) + " Mbit" : Number(bits / 1e9) + " Gbit";
    }

    private static string AutoBytes(double bytes)
    {
        double step = Step;
        if (bytes < RoundsToThousand * step)
        {
            return Number(bytes / step) + " KB/s";
        }

        return bytes < RoundsToThousand * step * step
            ? Number(bytes / (step * step)) + " MB/s"
            : Number(bytes / (step * step * step)) + " GB/s";
    }

    private static string Number(double value) =>
        value.ToString(value < 99.95 ? "0.0" : "0", CultureInfo.CurrentCulture);

    /// <summary>Formats a byte count with three significant digits, e.g. "1.23 GB", "123 MB".</summary>
    public static string Size(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + Size(-bytes);
        }

        double value = bytes;
        int unit = 0;
        double step = Step;
        while (value >= step && unit < SizeUnits.Length - 1)
        {
            value /= step;
            unit++;
        }

        if (unit == 0)
        {
            return bytes.ToString(CultureInfo.CurrentCulture) + " B";
        }

        string format = value < 9.995 ? "0.00" : value < 99.95 ? "0.0" : "0";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + SizeUnits[unit];
    }

    /// <summary>Bytes in one gigabyte or terabyte under the current <see cref="SizeBase"/>.</summary>
    public static long UnitBytes(int power) => (long)Math.Pow(Step, power);
}
