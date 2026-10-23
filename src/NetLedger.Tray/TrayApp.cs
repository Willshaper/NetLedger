using System.Globalization;
using NetLedger.Core;
using NetLedger.Core.Ipc;

namespace NetLedger.Tray;

/// <summary>Owns the taskbar widget, its menu and windows, and the periodic summary fetched from the service.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private static readonly TimeSpan SummaryMaxAge = TimeSpan.FromSeconds(20);

    private readonly LedgerClient _client = new();
    private readonly SpeedMonitor _speed;
    private readonly TaskbarWidget _widget;
    private readonly MessageWindow _messages;
    private readonly ContextMenuStrip _menu;
    private readonly HoverCard _hover = new();
    private readonly BackupFlow _backup;
    private readonly System.Windows.Forms.Timer _summaryTimer = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer _environmentTimer = new() { Interval = 750 };

    private UiSettings _settings;
    private StatsForm? _stats;
    private SettingsForm? _settingsForm;
    private Summary _summary = Summary.Empty;
    private DateTime _summaryAt = DateTime.MinValue;
    private bool _refreshing;
    private bool _hovering;

    public TrayApp()
    {
        _settings = UiSettings.Load();
        ByteFormat.SizeBase = _settings.SizeBase;

        _speed = new SpeedMonitor { SmoothingSeconds = _settings.SmoothingSeconds };
        _widget = new TaskbarWidget(_speed, _settings);
        _widget.Clicked += ShowStatistics;
        _widget.MenuRequested += ShowMenu;
        _widget.HoverStarted += OnHoverStarted;
        _widget.HoverEnded += OnHoverEnded;
        _speed.Updated += OnSpeedUpdated;

        _backup = new BackupFlow(_client);
        _backup.HistoryChanged += () =>
        {
            _stats?.RefreshAll();
            _ = RefreshSummaryAsync();
        };

        _messages = new MessageWindow();
        _messages.TaskbarCreated += () => _widget.Attach();
        _messages.ShowStatisticsRequested += ShowStatistics;
        _messages.EnvironmentChanged += () =>
        {
            // These arrive in bursts; react once things settle.
            _environmentTimer.Stop();
            _environmentTimer.Start();
        };
        _environmentTimer.Tick += (_, _) =>
        {
            _environmentTimer.Stop();
            Theme.Refresh();
            _widget.Attach();
            _stats?.RefreshAll();
        };

        _menu = BuildMenu();
        _widget.Attach();

        _summaryTimer.Tick += async (_, _) => await RefreshSummaryAsync();
        _summaryTimer.Start();
        _ = RefreshSummaryAsync();
    }

    public static Icon AppIcon { get; } = LoadIcon();

    private static Icon LoadIcon()
    {
        using Stream? stream = typeof(TrayApp).Assembly.GetManifestResourceStream("NetLedger.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    // ---- widget events ----

    private void OnSpeedUpdated()
    {
        _widget.Tick();
        if (_hovering)
        {
            ShowHoverCard();
        }
    }

    private void OnHoverStarted()
    {
        _hovering = true;
        ShowHoverCard();
        if (DateTime.UtcNow - _summaryAt > SummaryMaxAge)
        {
            _ = RefreshSummaryAsync();
        }
    }

    private void OnHoverEnded()
    {
        _hovering = false;
        _hover.Hide();
    }

    private void ShowHoverCard()
    {
        (double rx, double tx) = _speed.CurrentRate();
        var rows = new List<(string, string)>
        {
            ("Now", $"↓ {ByteFormat.Rate(rx, _settings.RateUnit)}   ↑ {ByteFormat.Rate(tx, _settings.RateUnit)}"),
        };
        string? note = null;
        bool warning = false;
        if (_summary.Available)
        {
            rows.Add(("Today", $"↓ {ByteFormat.Size(_summary.Today.Received)}   ↑ {ByteFormat.Size(_summary.Today.Sent)}"));
            rows.Add(("This month", $"↓ {ByteFormat.Size(_summary.Month.Received)}   ↑ {ByteFormat.Size(_summary.Month.Sent)}"));
            if (_summary.Quota is { Enabled: true } quota)
            {
                rows.Add(("Quota", $"{ByteFormat.Size(quota.UsedBytes)} of {ByteFormat.Size(quota.LimitBytes)} ({quota.Percent.ToString("0", CultureInfo.CurrentCulture)} %)"));
            }

            if (_summary.Status is { Online: false, OfflineSinceUtc: long since })
            {
                note = $"No internet since {DateTimeOffset.FromUnixTimeSeconds(since).LocalDateTime:HH:mm}.";
                warning = true;
            }
        }
        else if (_summaryAt != DateTime.MinValue)
        {
            note = "History isn't being recorded: the NetLedger service isn't running.";
            warning = true;
        }

        _hover.ShowAbove(_widget.ScreenBounds, rows, note, warning);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false };
        var statistics = new ToolStripMenuItem("Statistics…", null, (_, _) => ShowStatistics());
        statistics.Font = new Font(statistics.Font, FontStyle.Bold);
        menu.Items.Add(statistics);
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Back up data…", null, async (_, _) => await _backup.ExportAsync(null));
        menu.Items.Add("Restore from backup…", null, async (_, _) => await _backup.ImportAsync(null));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private void ShowMenu(Point screen)
    {
        // Without this the menu wouldn't close when clicking elsewhere.
        Native.SetForegroundWindow(_messages.Handle);
        _menu.Show(screen, ToolStripDropDownDirection.AboveLeft);
    }

    // ---- windows ----

    private void ShowStatistics()
    {
        if (_stats is null || _stats.IsDisposed)
        {
            _stats = new StatsForm(_client, _settings);
            _stats.SettingsRequested += ShowSettings;
            _stats.FormClosed += (_, _) =>
            {
                SaveSettings();
                _stats = null;
            };
            _stats.Show();
        }

        if (_stats.WindowState == FormWindowState.Minimized)
        {
            _stats.WindowState = FormWindowState.Normal;
        }

        _stats.Activate();
    }

    private void ShowSettings()
    {
        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_client, _backup, _settings);
            _settingsForm.UiSettingsApplied += ApplyUiSettings;
            _settingsForm.ServiceSettingsApplied += () =>
            {
                _ = RefreshSummaryAsync();
                _stats?.RefreshAll();
            };
            _settingsForm.FormClosed += (_, _) => _settingsForm = null;
            _settingsForm.Show();
        }

        _settingsForm.Activate();
    }

    private void ApplyUiSettings(UiSettings settings)
    {
        // Keep window sizes the stats form recorded into the old instance.
        settings.StatsWidth = _settings.StatsWidth;
        settings.StatsHeight = _settings.StatsHeight;
        _settings = settings;
        ByteFormat.SizeBase = settings.SizeBase;
        _speed.SmoothingSeconds = settings.SmoothingSeconds;
        _widget.ApplySettings(settings);
        SaveSettings();
        _stats?.UseSettings(settings);
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth interrupting the user over; the next save tries again.
        }
    }

    // ---- summary ----

    private async Task RefreshSummaryAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            IpcResponse adapters = await _client.CallAsync(Ops.Adapters);
            _speed.SetCountedAdapters(adapters.Adapters ?? []);
            IpcResponse status = await _client.CallAsync(Ops.Status);
            DateTime today = DateTime.Today;
            UsageBucket day = await SingleBucketAsync(BucketKind.Day, today, today.AddDays(1));
            DateTime month = new(today.Year, today.Month, 1);
            UsageBucket monthBucket = await SingleBucketAsync(BucketKind.Month, month, month.AddMonths(1));
            IpcResponse quota = await _client.CallAsync(Ops.Quota);
            _summary = new Summary(true, status.Status, day, monthBucket, quota.Quota);
            _widget.SetQuota(quota.Quota is { Enabled: true } ? quota.Quota : null);
            CheckQuotaAlert(quota.Quota);
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or ServiceRequestException)
        {
            _summary = Summary.Empty;
            _widget.SetQuota(null);
        }
        finally
        {
            _summaryAt = DateTime.UtcNow;
            _refreshing = false;
        }
    }

    private async Task<UsageBucket> SingleBucketAsync(BucketKind kind, DateTime from, DateTime to)
    {
        IpcResponse response = await _client.CallAsync(new IpcRequest
        {
            Op = Ops.Usage,
            Usage = new UsageQuery { Kind = kind, FromLocal = from, ToLocal = to, WeekStart = _settings.WeekStart },
        });
        return response.Buckets?.FirstOrDefault() ?? new UsageBucket();
    }

    private void CheckQuotaAlert(QuotaStatus? quota)
    {
        if (quota is not { Enabled: true })
        {
            return;
        }

        int reached = quota.AlertPercents.Where(p => quota.Percent >= p).DefaultIfEmpty(0).Max();
        if (reached <= quota.NotifiedPercent)
        {
            return;
        }

        string ends = quota.CycleEndLocal.AddDays(-1).ToString("d MMM", CultureInfo.CurrentCulture);
        var toast = new Toast(
            reached >= 100 ? "Data quota used up" : $"Data quota {reached} % used",
            $"{ByteFormat.Size(quota.UsedBytes)} of {ByteFormat.Size(quota.LimitBytes)} used this cycle, which ends {ends}. "
            + $"At this rate you'll reach {ByteFormat.Size(quota.ProjectedBytes)}.");
        toast.Click += (_, _) => ShowStatistics();
        toast.FormClosed += (_, _) => toast.Dispose();
        toast.ShowNow();

        _ = AcknowledgeAsync(quota.CycleStartLocal, reached);
    }

    private async Task AcknowledgeAsync(DateTime cycleStart, int percent)
    {
        try
        {
            await _client.CallAsync(new IpcRequest { Op = Ops.AckQuota, QuotaAck = new QuotaAck { CycleStartLocal = cycleStart, Percent = percent } });
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or ServiceRequestException)
        {
            // It will simply be shown again next time.
        }
    }

    // ---- shutdown ----

    protected override void ExitThreadCore()
    {
        SaveSettings();
        _summaryTimer.Dispose();
        _environmentTimer.Dispose();
        _speed.Dispose();
        _widget.Dispose();
        _messages.Dispose();
        _hover.Dispose();
        _menu.Dispose();
        _stats?.Close();
        _settingsForm?.Close();
        base.ExitThreadCore();
    }

    private sealed record Summary(bool Available, ServiceStatus? Status, UsageBucket Today, UsageBucket Month, QuotaStatus? Quota)
    {
        public static readonly Summary Empty = new(false, null, new UsageBucket(), new UsageBucket(), null);
    }
}
