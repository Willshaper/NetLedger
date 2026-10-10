using NetLedger.Core;
using NetLedger.Core.Ipc;
using Xunit;

namespace NetLedger.Tests;

public class PeriodTests
{
    // Central European Time: DST 2026 starts 29 March 02:00 → 03:00 and ends 25 October 03:00 → 02:00.
    private static readonly TimeZoneInfo Copenhagen = TimeZoneInfo.FindSystemTimeZoneById("Romance Standard Time");

    private static long Utc(int y, int mo, int d, int h, int mi = 0) =>
        new DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    [Theory]
    [InlineData(DayOfWeek.Monday, 5)]
    [InlineData(DayOfWeek.Sunday, 4)]
    [InlineData(DayOfWeek.Saturday, 10)]
    public void WeekStartsOnConfiguredDay(DayOfWeek weekStart, int expectedDay)
    {
        DateTime saturday = new(2026, 10, 10, 15, 30, 0);
        Assert.Equal(new DateTime(2026, 10, expectedDay), PeriodMath.BucketStart(saturday, BucketKind.Week, weekStart));
    }

    [Fact]
    public void SkippedLocalTimeMapsToEndOfGap()
    {
        // 02:30 doesn't exist on 29 March; the first valid instant is 03:00 CEST = 01:00 UTC.
        Assert.Equal(Utc(2026, 3, 29, 1), PeriodMath.ToUtcSeconds(new DateTime(2026, 3, 29, 2, 30, 0), Copenhagen));
    }

    [Fact]
    public void RepeatedLocalTimeMapsToFirstOccurrence()
    {
        // 02:30 happens twice on 25 October; the first is still CEST (+2).
        Assert.Equal(Utc(2026, 10, 25, 0, 30), PeriodMath.ToUtcSeconds(new DateTime(2026, 10, 25, 2, 30, 0), Copenhagen));
    }

    [Fact]
    public void FallBackDayHasTwentyFiveHours()
    {
        DateTime day = new(2026, 10, 25);
        List<UsageBucket> buckets = UsageAggregator.Aggregate([], day, day.AddDays(1), BucketKind.Hour, DayOfWeek.Monday, Copenhagen);
        Assert.Equal(25, buckets.Count);
        Assert.Equal(2, buckets.Count(b => b.LocalStart.Hour == 2));
    }

    [Fact]
    public void SpringForwardDayHasTwentyThreeHours()
    {
        DateTime day = new(2026, 3, 29);
        List<UsageBucket> buckets = UsageAggregator.Aggregate([], day, day.AddDays(1), BucketKind.Hour, DayOfWeek.Monday, Copenhagen);
        Assert.Equal(23, buckets.Count);
        Assert.DoesNotContain(buckets, b => b.LocalStart.Hour == 2);
    }

    [Fact]
    public void SamplesLandInTheirLocalDay()
    {
        UsageSample[] samples =
        [
            new(Utc(2026, 10, 24, 21), 100, 1), // 23:00 local on the 24th
            new(Utc(2026, 10, 24, 22), 200, 2), // 00:00 local on the 25th
            new(Utc(2026, 10, 25, 22), 400, 4), // 23:00 local on the 25th (CET by then)
            new(Utc(2026, 10, 25, 23), 800, 8), // 00:00 local on the 26th
        ];
        DateTime from = new(2026, 10, 24);
        List<UsageBucket> days = UsageAggregator.Aggregate(samples, from, from.AddDays(3), BucketKind.Day, DayOfWeek.Monday, Copenhagen);

        Assert.Equal(3, days.Count);
        Assert.Equal([100L, 600L, 800L], days.Select(d => d.Received));
        Assert.Equal([1L, 6L, 8L], days.Select(d => d.Sent));
    }

    [Fact]
    public void EmptyBucketsAreIncludedAndOutsideSamplesIgnored()
    {
        DateTime from = new(2026, 1, 1);
        UsageSample[] samples = [new(Utc(2025, 12, 31, 12), 5, 5), new(Utc(2026, 2, 10, 12), 7, 7)];
        List<UsageBucket> months = UsageAggregator.Aggregate(samples, from, from.AddYears(1), BucketKind.Month, DayOfWeek.Monday, Copenhagen);

        Assert.Equal(12, months.Count);
        Assert.Equal(7, months[1].Received);
        Assert.Equal(7, months.Sum(m => m.Received));
    }

    [Theory]
    [InlineData("2026-02-15", 31, "2026-01-31", "2026-02-28")]
    [InlineData("2026-02-28 10:00", 31, "2026-02-28", "2026-03-31")]
    [InlineData("2026-10-10", 15, "2026-09-15", "2026-10-15")]
    [InlineData("2026-10-15", 15, "2026-10-15", "2026-11-15")]
    [InlineData("2026-12-20", 1, "2026-12-01", "2027-01-01")]
    public void BillingCycleClampsToShortMonths(string now, int startDay, string start, string end)
    {
        (DateTime s, DateTime e) = PeriodMath.BillingCycle(DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture), startDay);
        Assert.Equal(DateTime.Parse(start, System.Globalization.CultureInfo.InvariantCulture), s);
        Assert.Equal(DateTime.Parse(end, System.Globalization.CultureInfo.InvariantCulture), e);
    }
}
