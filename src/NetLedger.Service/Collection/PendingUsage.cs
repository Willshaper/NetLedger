using System.Runtime.InteropServices;
using NetLedger.Core;

namespace NetLedger.Service.Collection;

internal struct ByteCounts
{
    public long Received;
    public long Sent;
}

/// <summary>Traffic counted since the last database flush, already split into minute and hour buckets.</summary>
internal sealed class PendingUsage
{
    public Dictionary<(long Time, long Id), ByteCounts> Minutes { get; } = new();
    public Dictionary<(long Time, long Id), ByteCounts> Hours { get; } = new();
    public Dictionary<(long Time, long Id), ByteCounts> AppHours { get; } = new();

    public bool IsEmpty => Minutes.Count == 0 && Hours.Count == 0 && AppHours.Count == 0;

    public void AddAdapter(long nowUtc, long adapterId, long received, long sent)
    {
        Add(Minutes, (PeriodMath.FloorTo(nowUtc, 60), adapterId), received, sent);
        Add(Hours, (PeriodMath.FloorTo(nowUtc, 3600), adapterId), received, sent);
    }

    public void AddApp(long nowUtc, long appId, long received, long sent) =>
        Add(AppHours, (PeriodMath.FloorTo(nowUtc, 3600), appId), received, sent);

    public void Clear()
    {
        Minutes.Clear();
        Hours.Clear();
        AppHours.Clear();
    }

    private static void Add(Dictionary<(long, long), ByteCounts> map, (long, long) key, long received, long sent)
    {
        ref ByteCounts counts = ref CollectionsMarshal.GetValueRefOrAddDefault(map, key, out _);
        counts.Received += received;
        counts.Sent += sent;
    }
}
