using System.Net.NetworkInformation;

namespace NetLedger.Service.Collection;

/// <summary>
/// Detects internet outages with a small HTTP probe (by default the endpoint Windows itself uses for its
/// "No internet" check). Two failed probes in a row open an outage that starts at the first failure.
/// </summary>
internal sealed class ConnectivityMonitor : IDisposable
{
    private const int FailingIntervalSeconds = 10;
    private const int FailuresBeforeOutage = 2;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ResumeGrace = TimeSpan.FromSeconds(60);

    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new();
    private readonly List<Action> _queuedEvents = new();
    private Task? _loop;
    private string _url;
    private string _expect;
    private int _intervalSeconds;
    private bool _paused;
    private DateTime _graceUntilUtc;
    private int _failures;
    private long _firstFailureUtc;

    public ConnectivityMonitor(string url, string expect, int intervalSeconds)
    {
        _url = url;
        _expect = expect;
        _intervalSeconds = intervalSeconds;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // a captive portal redirect means no internet
            UseCookies = false,
            ConnectTimeout = ProbeTimeout,
        })
        {
            Timeout = ProbeTimeout,
            MaxResponseContentBufferSize = 1024 * 1024,
        };
    }

    /// <summary>Raised with the outage start (UTC seconds) once it is confirmed.</summary>
    public event Action<long>? OutageStarted;

    /// <summary>Raised with the outage start and end (UTC seconds).</summary>
    public event Action<long, long>? OutageEnded;

    public bool? Online { get; private set; }

    public long? OfflineSinceUtc { get; private set; }

    public void Start()
    {
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _loop = Task.Run(LoopAsync);
    }

    public void Configure(string url, string expect, int intervalSeconds)
    {
        lock (_gate)
        {
            _url = url;
            _expect = expect;
            _intervalSeconds = intervalSeconds;
        }

        Wake();
    }

    /// <summary>The machine is going to sleep: close any open outage now, it isn't the network's fault.</summary>
    public void Suspend()
    {
        lock (_gate)
        {
            _paused = true;
            EndOutage(Now());
            _failures = 0;
            Online = null;
        }

        RaiseQueuedEvents();
    }

    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            _graceUntilUtc = DateTime.UtcNow + ResumeGrace; // adapters take a moment to reconnect after wake
            _failures = 0;
            Online = null;
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
        }

        _http.Dispose();
        _cts.Dispose();
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Wake();

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task LoopAsync()
    {
        CancellationToken token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            int delaySeconds;
            string url;
            string expect;
            lock (_gate)
            {
                url = _url;
                expect = _expect;
                delaySeconds = _intervalSeconds;
                if (_paused || DateTime.UtcNow < _graceUntilUtc)
                {
                    delaySeconds = 5;
                    url = "";
                }
            }

            if (url.Length > 0)
            {
                bool ok = await ProbeAsync(url, expect, token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                lock (_gate)
                {
                    if (!_paused)
                    {
                        Record(ok);
                    }

                    if (!ok)
                    {
                        delaySeconds = Math.Min(delaySeconds, FailingIntervalSeconds);
                    }
                }

                RaiseQueuedEvents();
            }

            try
            {
                await _wake.WaitAsync(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Record(bool ok)
    {
        long now = Now();
        if (ok)
        {
            _failures = 0;
            Online = true;
            EndOutage(now);
            return;
        }

        if (_failures++ == 0)
        {
            _firstFailureUtc = now;
        }

        if (_failures >= FailuresBeforeOutage && OfflineSinceUtc is null)
        {
            OfflineSinceUtc = _firstFailureUtc;
            Online = false;
            long start = _firstFailureUtc;
            _queuedEvents.Add(() => OutageStarted?.Invoke(start));
        }
    }

    private void EndOutage(long now)
    {
        if (OfflineSinceUtc is long start)
        {
            OfflineSinceUtc = null;
            _queuedEvents.Add(() => OutageEnded?.Invoke(start, Math.Max(start, now)));
        }
    }

    private async Task<bool> ProbeAsync(string url, string expect, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            request.Headers.ConnectionClose = true;
            using HttpResponseMessage response = await _http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            if (expect.Length == 0)
            {
                return true;
            }

            string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return body.Contains(expect, StringComparison.Ordinal);
        }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            // Any failure to fetch the page (DNS, TLS, timeout, oversized captive portal...) means offline.
            return false;
        }
    }

    // Handlers take the engine lock, so they must never run while _gate is held.
    private void RaiseQueuedEvents()
    {
        Action[] events;
        lock (_gate)
        {
            events = _queuedEvents.ToArray();
            _queuedEvents.Clear();
        }

        foreach (Action raise in events)
        {
            try
            {
                raise();
            }
            catch (Exception ex)
            {
                Log.Error("Outage handler failed", ex);
            }
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
