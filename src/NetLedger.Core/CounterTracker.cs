using NetLedger.Core.Native;

namespace NetLedger.Core;

public readonly record struct InterfaceDelta(InterfaceSnapshot Interface, ulong Received, ulong Sent);

/// <summary>
/// Turns cumulative interface counters into per-sample deltas, coping with counter resets, adapters that come
/// and go, and the occasional nonsense value a driver reports.
/// </summary>
public sealed class CounterTracker
{
    // Slack on top of the link speed before a delta is treated as garbage.
    private const double LinkSpeedSlack = 2.0;
    private const ulong MinimumAllowance = 1 << 20;

    private readonly Dictionary<Guid, (ulong In, ulong Out)> _last = new();
    private readonly HashSet<Guid> _seen = new();

    /// <summary>Forget all baselines; the next update only records new baselines.</summary>
    public void Reset() => _last.Clear();

    public void Update(IReadOnlyList<InterfaceSnapshot> snapshots, double elapsedSeconds, List<InterfaceDelta> output)
    {
        output.Clear();
        _seen.Clear();
        foreach (InterfaceSnapshot snap in snapshots)
        {
            _seen.Add(snap.Guid);
            if (_last.TryGetValue(snap.Guid, out var last))
            {
                ulong received = Delta(last.In, snap.InOctets, snap.LinkSpeedBitsPerSecond, elapsedSeconds);
                ulong sent = Delta(last.Out, snap.OutOctets, snap.LinkSpeedBitsPerSecond, elapsedSeconds);
                if (received != 0 || sent != 0)
                {
                    output.Add(new InterfaceDelta(snap, received, sent));
                }
            }

            _last[snap.Guid] = (snap.InOctets, snap.OutOctets);
        }

        // Adapters that vanished get a fresh baseline when they come back.
        if (_last.Count != _seen.Count)
        {
            foreach (Guid guid in _last.Keys.Where(g => !_seen.Contains(g)).ToList())
            {
                _last.Remove(guid);
            }
        }
    }

    internal static ulong Delta(ulong previous, ulong current, ulong linkSpeedBitsPerSecond, double elapsedSeconds)
    {
        // A counter that went backwards was reset (adapter restarted): everything it shows is new traffic.
        ulong delta = current >= previous ? current - previous : current;
        if (linkSpeedBitsPerSecond > 0)
        {
            double allowance = linkSpeedBitsPerSecond / 8.0 * Math.Max(elapsedSeconds, 1.0) * LinkSpeedSlack;
            if (delta > MinimumAllowance && delta > allowance)
            {
                return 0;
            }
        }

        return delta;
    }
}
