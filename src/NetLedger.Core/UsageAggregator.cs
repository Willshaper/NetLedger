using NetLedger.Core.Ipc;

namespace NetLedger.Core;

/// <summary>A stored row: bucket start (UTC Unix seconds) and its byte counts.</summary>
public readonly record struct UsageSample(long TimeUtc, long Received, long Sent);

public static class UsageAggregator
{
    /// <summary>
    /// Builds every <paramref name="kind"/> bucket in [<paramref name="fromLocal"/>, <paramref name="toLocal"/>),
    /// including empty ones, and adds the samples into them.
    /// </summary>
    /// <remarks>
    /// Minute and hour buckets step in real time, so a day with a DST fall-back has 25 hourly buckets (two
    /// labelled 02:00). Longer buckets follow the local calendar. A sample counts toward the bucket containing its start.
    /// </remarks>
    public static List<UsageBucket> Aggregate(
        IEnumerable<UsageSample> samples,
        DateTime fromLocal,
        DateTime toLocal,
        BucketKind kind,
        DayOfWeek weekStart,
        TimeZoneInfo zone)
    {
        var buckets = new List<UsageBucket>();
        if (kind is BucketKind.Minute or BucketKind.Hour)
        {
            long step = kind == BucketKind.Minute ? 60 : 3600;
            long fromUtc = PeriodMath.ToUtcSeconds(fromLocal, zone);
            long toUtc = PeriodMath.ToUtcSeconds(toLocal, zone);
            for (long t = fromUtc; t < toUtc; t += step)
            {
                buckets.Add(new UsageBucket { StartUtc = t, LocalStart = PeriodMath.ToLocal(t, zone) });
            }

            if (buckets.Count == 0)
            {
                return buckets;
            }

            foreach (UsageSample sample in samples)
            {
                long index = (sample.TimeUtc - fromUtc) / step;
                if (sample.TimeUtc < fromUtc)
                {
                    // Only reachable in half-hour time zones, where UTC-aligned rows straddle local hours.
                    if (sample.TimeUtc + step <= fromUtc)
                    {
                        continue;
                    }

                    index = 0;
                }

                if (index >= buckets.Count)
                {
                    continue;
                }

                UsageBucket bucket = buckets[(int)index];
                bucket.Received += sample.Received;
                bucket.Sent += sample.Sent;
            }

            return buckets;
        }

        var byStart = new Dictionary<DateTime, UsageBucket>();
        for (DateTime start = PeriodMath.BucketStart(fromLocal, kind, weekStart);
             start < toLocal;
             start = PeriodMath.NextBucket(start, kind))
        {
            var bucket = new UsageBucket { LocalStart = start, StartUtc = PeriodMath.ToUtcSeconds(start, zone) };
            buckets.Add(bucket);
            byStart[start] = bucket;
        }

        foreach (UsageSample sample in samples)
        {
            DateTime local = PeriodMath.ToLocal(sample.TimeUtc, zone);
            if (byStart.TryGetValue(PeriodMath.BucketStart(local, kind, weekStart), out UsageBucket? bucket))
            {
                bucket.Received += sample.Received;
                bucket.Sent += sample.Sent;
            }
        }

        return buckets;
    }
}
