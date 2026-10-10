namespace NetLedger.Core;

public enum BucketKind
{
    Minute,
    Hour,
    Day,
    Week,
    Month,
    Year,
}

/// <summary>Calendar arithmetic in local time. Storage is always UTC; periods are always local.</summary>
public static class PeriodMath
{
    public static DateTime BucketStart(DateTime local, BucketKind kind, DayOfWeek weekStart)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return kind switch
        {
            BucketKind.Minute => new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0),
            BucketKind.Hour => new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0),
            BucketKind.Day => local.Date,
            BucketKind.Week => local.Date.AddDays(-(((int)local.DayOfWeek - (int)weekStart + 7) % 7)),
            BucketKind.Month => new DateTime(local.Year, local.Month, 1),
            _ => new DateTime(local.Year, 1, 1),
        };
    }

    public static DateTime NextBucket(DateTime start, BucketKind kind) => kind switch
    {
        BucketKind.Minute => start.AddMinutes(1),
        BucketKind.Hour => start.AddHours(1),
        BucketKind.Day => start.AddDays(1),
        BucketKind.Week => start.AddDays(7),
        BucketKind.Month => start.AddMonths(1),
        _ => start.AddYears(1),
    };

    /// <summary>
    /// Converts a local wall-clock time to UTC Unix seconds. A time skipped by a DST jump maps to the end of the
    /// gap; a time that occurs twice maps to its first occurrence.
    /// </summary>
    public static long ToUtcSeconds(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        for (int i = 0; i < 8 && zone.IsInvalidTime(local); i++)
        {
            local = local.AddMinutes(15);
        }

        TimeSpan offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUnixTimeSeconds();
    }

    public static DateTime ToLocal(long utcSeconds, TimeZoneInfo zone) =>
        DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.UnixEpoch.AddSeconds(utcSeconds), zone),
            DateTimeKind.Unspecified);

    public static long FloorTo(long seconds, long step) => seconds - (((seconds % step) + step) % step);

    /// <summary>
    /// The billing cycle containing <paramref name="localNow"/>. Cycles start on <paramref name="startDay"/>
    /// of each month, or on the month's last day when it is shorter (day 31 → 28 Feb).
    /// </summary>
    public static (DateTime Start, DateTime End) BillingCycle(DateTime localNow, int startDay)
    {
        startDay = Math.Clamp(startDay, 1, 31);
        localNow = DateTime.SpecifyKind(localNow, DateTimeKind.Unspecified);
        DateTime monthStart = new(localNow.Year, localNow.Month, 1);
        DateTime start = CycleStartIn(monthStart, startDay);
        if (localNow < start)
        {
            monthStart = monthStart.AddMonths(-1);
            start = CycleStartIn(monthStart, startDay);
        }

        return (start, CycleStartIn(monthStart.AddMonths(1), startDay));
    }

    private static DateTime CycleStartIn(DateTime monthStart, int startDay) =>
        new(monthStart.Year, monthStart.Month, Math.Min(startDay, DateTime.DaysInMonth(monthStart.Year, monthStart.Month)));
}
