using System.Diagnostics;
using NetLedger.Core;
using NetLedger.Core.Ipc;
using NetLedger.Core.Native;

namespace NetLedger.Tray;

/// <summary>
/// Samples adapter counters once a second for the live display. This runs in the user's process and needs no
/// service, so the widget keeps working even when history isn't being recorded.
/// </summary>
internal sealed class SpeedMonitor : IDisposable
{
    private const int HistoryCapacity = 600;

    private readonly SynchronizationContext _ui;
    private readonly System.Threading.Timer _timer;
    private readonly CounterTracker _tracker = new();
    private readonly List<InterfaceSnapshot> _snapshots = new();
    private readonly List<InterfaceDelta> _deltas = new();
    private readonly object _gate = new();
    private readonly double[] _received = new double[HistoryCapacity];
    private readonly double[] _sent = new double[HistoryCapacity];
    private int _head;
    private int _count;
    private long _last = Stopwatch.GetTimestamp();
    private Dictionary<Guid, bool> _countsByGuid = new();
    private Dictionary<(string, string), bool> _countsByName = new();
    private bool _disposed;
    private int _sampling;

    public SpeedMonitor()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _timer = new System.Threading.Timer(_ => Sample(), null, 0, 1000);
    }

    /// <summary>Raised on the UI thread after every sample.</summary>
    public event Action? Updated;

    public int SmoothingSeconds { get; set; } = 2;

    /// <summary>
    /// The service's per-adapter choice. An adapter is matched by GUID, then by name and description (VPN tunnels come
    /// back with a new GUID on every connect); anything else follows the automatic rule.
    /// </summary>
    public void SetCountedAdapters(IReadOnlyList<AdapterInfo> adapters)
    {
        var byGuid = adapters.ToDictionary(a => a.Guid, a => a.CountsInTotal);
        var byName = new Dictionary<(string, string), bool>();
        foreach (AdapterInfo adapter in adapters)
        {
            byName.TryAdd((adapter.Name, adapter.Description), adapter.CountsInTotal);
        }

        lock (_gate)
        {
            _countsByGuid = byGuid;
            _countsByName = byName;
        }
    }

    public (double Received, double Sent) CurrentRate()
    {
        lock (_gate)
        {
            int n = Math.Min(Math.Max(1, SmoothingSeconds), _count);
            if (n == 0)
            {
                return (0, 0);
            }

            double rx = 0, tx = 0;
            for (int i = 0; i < n; i++)
            {
                int index = (_head - 1 - i + HistoryCapacity) % HistoryCapacity;
                rx += _received[index];
                tx += _sent[index];
            }

            return (rx / n, tx / n);
        }
    }

    /// <summary>Copies the last <paramref name="seconds"/> per-second rates, oldest first, padding with zeros.</summary>
    public void CopyHistory(int seconds, double[] received, double[] sent)
    {
        lock (_gate)
        {
            for (int i = 0; i < seconds; i++)
            {
                int age = seconds - 1 - i;
                if (age < _count)
                {
                    int index = (_head - 1 - age + HistoryCapacity) % HistoryCapacity;
                    received[i] = _received[index];
                    sent[i] = _sent[index];
                }
                else
                {
                    received[i] = 0;
                    sent[i] = 0;
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _timer.Dispose();
    }

    private void Sample()
    {
        // Timer callbacks can overlap if one stalls; the tracker isn't thread-safe.
        if (Interlocked.Exchange(ref _sampling, 1) == 1)
        {
            return;
        }

        try
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = Stopwatch.GetElapsedTime(_last, now).TotalSeconds;
            _last = now;
            IfTable.Read(_snapshots);
            _tracker.Update(_snapshots, elapsed, _deltas);

            // After a stall the delta covers the whole gap, so dividing by the real time gives its average rate.
            double rx = 0, tx = 0;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (InterfaceDelta delta in _deltas)
                {
                    InterfaceSnapshot nic = delta.Interface;
                    bool counts = _countsByGuid.TryGetValue(nic.Guid, out bool chosen)
                                  || _countsByName.TryGetValue((nic.Alias, nic.Description), out chosen)
                        ? chosen
                        : IfTable.CountsByDefault(nic.IsHardware, nic.IfType);
                    if (counts)
                    {
                        rx += delta.Received;
                        tx += delta.Sent;
                    }
                }

                _received[_head] = rx / Math.Max(elapsed, 0.25);
                _sent[_head] = tx / Math.Max(elapsed, 0.25);
                _head = (_head + 1) % HistoryCapacity;
                _count = Math.Min(_count + 1, HistoryCapacity);
            }

            _ui.Post(_ => Updated?.Invoke(), null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Speed sample failed: {ex}");
        }
        finally
        {
            Volatile.Write(ref _sampling, 0);
        }
    }
}
