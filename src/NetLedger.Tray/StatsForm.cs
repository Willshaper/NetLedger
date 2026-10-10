using System.Globalization;
using NetLedger.Core;
using NetLedger.Core.Ipc;

namespace NetLedger.Tray;

/// <summary>Usage history by minute, hour, day, week, month and year, plus per-app usage and the outage log.</summary>
internal sealed class StatsForm : Form
{
    private enum View
    {
        Minutes,
        Hours,
        Days,
        Weeks,
        Months,
        Years,
        Apps,
        Outages,
    }

    private enum AppRange
    {
        Day,
        Week,
        Month,
        Year,
        AllTime,
    }

    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    private readonly LedgerClient _client;
    private readonly UiSettings _settings;
    private readonly FlowLayoutPanel _tabs = new();
    private readonly Button _previous = new();
    private readonly Button _next = new();
    private readonly Button _today = new();
    private readonly Label _period = new();
    private readonly ComboBox _adapterBox = new();
    private readonly ComboBox _rangeBox = new();
    private readonly Label _banner = new();
    private readonly QuotaStrip _quota = new();
    private readonly BarChart _chart = new();
    private readonly ListView _list = new();
    private readonly Label _footer = new();
    private readonly ImageList _icons = new();
    private readonly Dictionary<string, int> _iconIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(View View, RadioButton Button)> _tabButtons = new();

    private View _view = View.Days;
    private AppRange _appRange = AppRange.Month;
    private DateTime _anchor = DateTime.Now;
    private List<AdapterInfo> _adapters = [];
    private List<UsageBucket> _buckets = [];
    private ServiceStatus? _status;
    private Font? _boldFont;
    private bool _populating;
    private int _requestId;

    public StatsForm(LedgerClient client, UiSettings settings)
    {
        _client = client;
        _settings = settings;
        Text = "NetLedger";
        Icon = TrayApp.AppIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(Px(560), Px(420));
        Size = new Size(
            settings.StatsWidth > 0 ? settings.StatsWidth : Px(820),
            settings.StatsHeight > 0 ? settings.StatsHeight : Px(640));
        KeyPreview = true;
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f);
        BuildLayout();
        Theme.Apply(this);
        ApplyPalette();
    }

    private int Px(int logical) => (int)Math.Round(logical * DeviceDpi / 96.0);

    // ---- layout ----

    private void BuildLayout()
    {
        _tabs.Dock = DockStyle.Top;
        _tabs.AutoSize = true;
        _tabs.Padding = new Padding(Px(10), Px(10), Px(10), 0);
        foreach (View view in Enum.GetValues<View>())
        {
            var button = new RadioButton
            {
                Text = view.ToString(),
                Appearance = Appearance.Button,
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 0, Px(4), 0),
                Padding = new Padding(Px(6), Px(2), Px(6), Px(2)),
                Checked = view == _view,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            View captured = view;
            button.CheckedChanged += (_, _) =>
            {
                if (button.Checked)
                {
                    SwitchTo(captured);
                }
            };
            _tabButtons.Add((view, button));
            _tabs.Controls.Add(button);
        }

        var nav = new Panel { Dock = DockStyle.Top, Height = Px(44), Padding = new Padding(Px(10), Px(8), Px(10), Px(4)) };
        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        ConfigureNavButton(_previous, "◀", "Previous period (Left arrow)");
        ConfigureNavButton(_next, "▶", "Next period (Right arrow)");
        ConfigureNavButton(_today, "Now", "Jump to the current period");
        _today.Width = Px(52);
        _previous.Click += (_, _) => Step(-1);
        _next.Click += (_, _) => Step(1);
        _today.Click += (_, _) =>
        {
            _anchor = DateTime.Now;
            Reload();
        };
        _period.AutoSize = true;
        _period.Margin = new Padding(Px(8), Px(6), Px(8), 0);
        _period.Font = new Font(Font.FontFamily, 11f, FontStyle.Bold);
        left.Controls.AddRange([_previous, _next, _today, _period]);

        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
        _adapterBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _adapterBox.Width = Px(260);
        _adapterBox.SelectedIndexChanged += (_, _) =>
        {
            if (!_populating)
            {
                Reload();
            }
        };
        _rangeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _rangeBox.Width = Px(120);
        _rangeBox.Items.AddRange(["Day", "Week", "Month", "Year", "All time"]);
        _rangeBox.SelectedIndex = (int)_appRange;
        _rangeBox.SelectedIndexChanged += (_, _) =>
        {
            _appRange = (AppRange)_rangeBox.SelectedIndex;
            Reload();
        };
        right.Controls.AddRange([_adapterBox, _rangeBox]);
        nav.Controls.Add(left);
        nav.Controls.Add(right);

        _banner.Dock = DockStyle.Top;
        _banner.AutoSize = false;
        _banner.Height = Px(40);
        _banner.Padding = new Padding(Px(12), Px(4), Px(12), Px(4));
        _banner.TextAlign = ContentAlignment.MiddleLeft;
        _banner.Visible = false;

        _quota.Dock = DockStyle.Top;
        _quota.Height = Px(40);
        _quota.Visible = false;

        _chart.Dock = DockStyle.Top;
        _chart.Height = Px(230);
        _chart.Margin = new Padding(Px(10));
        _chart.BarClicked += DrillDown;

        _list.Dock = DockStyle.Fill;
        _list.View = System.Windows.Forms.View.Details;
        _list.FullRowSelect = true;
        _list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _list.BorderStyle = BorderStyle.None;
        _list.SmallImageList = _icons;
        _list.DoubleClick += (_, _) =>
        {
            if (_list.SelectedIndices.Count == 1 && _view <= View.Years)
            {
                DrillDown(_list.SelectedIndices[0]);
            }
        };
        _icons.ColorDepth = ColorDepth.Depth32Bit;
        _icons.ImageSize = new Size(Px(16), Px(16));

        _footer.Dock = DockStyle.Bottom;
        _footer.Height = Px(32);
        _footer.Padding = new Padding(Px(12), 0, Px(12), 0);
        _footer.TextAlign = ContentAlignment.MiddleLeft;

        // Fill first, then the top-docked controls from the bottom up: the last one added docks highest.
        Controls.Add(_list);
        Controls.Add(_footer);
        Controls.Add(_chart);
        Controls.Add(_quota);
        Controls.Add(_banner);
        Controls.Add(nav);
        Controls.Add(_tabs);
    }

    private void ConfigureNavButton(Button button, string text, string tip)
    {
        button.Text = text;
        button.Width = Px(34);
        button.Height = Px(28);
        button.FlatStyle = FlatStyle.Flat;
        button.Margin = new Padding(0, 0, Px(4), 0);
        new ToolTip().SetToolTip(button, tip);
    }

    private void ApplyPalette()
    {
        Palette palette = Theme.Current;
        foreach ((_, RadioButton button) in _tabButtons)
        {
            button.FlatAppearance.BorderColor = palette.Border;
            button.FlatAppearance.CheckedBackColor = palette.Dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(220, 230, 245);
            button.BackColor = palette.Window;
            button.ForeColor = palette.Text;
        }

        foreach (Button button in new[] { _previous, _next, _today })
        {
            button.FlatAppearance.BorderColor = palette.Border;
            button.BackColor = palette.Surface;
            button.ForeColor = palette.Text;
        }

        _list.BackColor = palette.Window;
        _list.ForeColor = palette.Text;
        _footer.ForeColor = palette.SubtleText;
        _chart.DownloadColor = _settings.Download;
        _chart.UploadColor = _settings.Upload;
        _chart.BackColor = palette.Window;
        _chart.ForeColor = palette.Text;
        _chart.Font = Font;
        _quota.Invalidate();
    }

    // ---- navigation ----

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await RefreshMetaAsync();
        Reload();
    }

    /// <summary>Re-reads adapters, status and quota, then the current view.</summary>
    public async void RefreshAll()
    {
        ApplyPalette();
        await RefreshMetaAsync();
        Reload();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Left or Keys.Right && !_adapterBox.Focused && !_rangeBox.Focused)
        {
            Step(e.KeyCode == Keys.Left ? -1 : 1);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.F5)
        {
            RefreshAll();
        }
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        if (WindowState == FormWindowState.Normal)
        {
            _settings.StatsWidth = Width;
            _settings.StatsHeight = Height;
        }
    }

    private void SwitchTo(View view)
    {
        _view = view;
        Reload();
    }

    private void Step(int direction)
    {
        if (!_next.Enabled && direction > 0 || !_previous.Enabled && direction < 0)
        {
            return;
        }

        _anchor = _view switch
        {
            View.Minutes => _anchor.AddHours(direction),
            View.Hours => _anchor.AddDays(direction),
            View.Days => _anchor.AddMonths(direction),
            View.Weeks or View.Months => _anchor.AddYears(direction),
            View.Apps => _appRange switch
            {
                AppRange.Day => _anchor.AddDays(direction),
                AppRange.Week => _anchor.AddDays(7 * direction),
                AppRange.Month => _anchor.AddMonths(direction),
                AppRange.Year => _anchor.AddYears(direction),
                _ => _anchor,
            },
            _ => _anchor,
        };
        Reload();
    }

    private void DrillDown(int index)
    {
        if (index < 0 || index >= _buckets.Count)
        {
            return;
        }

        DateTime start = _buckets[index].LocalStart;
        View? target = _view switch
        {
            View.Hours => View.Minutes,
            View.Days => View.Hours,
            View.Weeks => View.Days,
            View.Months => View.Days,
            View.Years => View.Months,
            _ => null,
        };
        if (target is null)
        {
            return;
        }

        _anchor = start;
        _tabButtons.First(t => t.View == target).Button.Checked = true; // triggers Reload
    }

    private (DateTime From, DateTime To, BucketKind Kind, string Title) UsagePeriod()
    {
        DateTime a = _anchor;
        switch (_view)
        {
            case View.Minutes:
            {
                DateTime from = new(a.Year, a.Month, a.Day, a.Hour, 0, 0);
                return (from, from.AddHours(1), BucketKind.Minute,
                    $"{from.ToString("ddd d MMM yyyy", Culture)}, {from:HH:mm}–{from.AddHours(1):HH:mm}");
            }

            case View.Hours:
                return (a.Date, a.Date.AddDays(1), BucketKind.Hour, a.Date.ToString("dddd d MMMM yyyy", Culture));
            case View.Days:
            {
                DateTime from = new(a.Year, a.Month, 1);
                return (from, from.AddMonths(1), BucketKind.Day, from.ToString("MMMM yyyy", Culture));
            }

            case View.Weeks:
                return (new DateTime(a.Year, 1, 1), new DateTime(a.Year + 1, 1, 1), BucketKind.Week, a.Year.ToString(Culture));
            case View.Months:
                return (new DateTime(a.Year, 1, 1), new DateTime(a.Year + 1, 1, 1), BucketKind.Month, a.Year.ToString(Culture));
            default:
                return (default, new DateTime(DateTime.Now.Year + 1, 1, 1), BucketKind.Year, "All years");
        }
    }

    private (DateTime From, DateTime To, string Title) AppPeriod()
    {
        DateTime a = _anchor;
        switch (_appRange)
        {
            case AppRange.Day:
                return (a.Date, a.Date.AddDays(1), a.Date.ToString("dddd d MMMM yyyy", Culture));
            case AppRange.Week:
            {
                DateTime from = PeriodMath.BucketStart(a, BucketKind.Week, _settings.WeekStart);
                return (from, from.AddDays(7), $"{from.ToString("d MMM", Culture)} – {from.AddDays(6).ToString("d MMM yyyy", Culture)}");
            }

            case AppRange.Month:
            {
                DateTime from = new(a.Year, a.Month, 1);
                return (from, from.AddMonths(1), from.ToString("MMMM yyyy", Culture));
            }

            case AppRange.Year:
                return (new DateTime(a.Year, 1, 1), new DateTime(a.Year + 1, 1, 1), a.Year.ToString(Culture));
            default:
                return (default, default, "All time");
        }
    }

    // ---- loading ----

    private async Task RefreshMetaAsync()
    {
        try
        {
            IpcResponse adapters = await _client.CallAsync(Ops.Adapters);
            IpcResponse status = await _client.CallAsync(Ops.Status);
            IpcResponse quota = await _client.CallAsync(Ops.Quota);
            _adapters = adapters.Adapters ?? [];
            _status = status.Status;
            _quota.Status = quota.Quota is { Enabled: true } ? quota.Quota : null;
            _quota.Visible = _quota.Status is not null;
            _quota.Invalidate();
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or ServiceRequestException)
        {
            _status = null;
            _quota.Visible = false;
        }

        object? selected = _adapterBox.SelectedItem;
        _populating = true;
        _adapterBox.BeginUpdate();
        _adapterBox.Items.Clear();
        _adapterBox.Items.Add(new AdapterChoice("Internet total (counted adapters)", AdapterSelection.Counted, 0));
        _adapterBox.Items.Add(new AdapterChoice("All adapters", AdapterSelection.All, 0));
        foreach (AdapterInfo adapter in _adapters)
        {
            string state = adapter.Present ? "" : " (not present)";
            _adapterBox.Items.Add(new AdapterChoice($"{adapter.Name}{state}", AdapterSelection.Single, adapter.Id));
        }

        int index = selected is AdapterChoice previous ? _adapterBox.Items.IndexOf(previous) : -1;
        _adapterBox.SelectedIndex = Math.Max(0, index);
        _adapterBox.EndUpdate();
        _populating = false;
    }

    private sealed record AdapterChoice(string Text, AdapterSelection Selection, long Id)
    {
        public override string ToString() => Text;
    }

    private async void Reload()
    {
        int id = ++_requestId;
        bool usage = _view <= View.Years;
        _chart.Visible = usage;
        _adapterBox.Visible = usage;
        _rangeBox.Visible = _view == View.Apps;
        bool navigable = _view is not (View.Years or View.Outages) && !(_view == View.Apps && _appRange == AppRange.AllTime);
        _previous.Enabled = navigable;
        _today.Enabled = navigable;
        _next.Enabled = navigable;

        try
        {
            switch (_view)
            {
                case View.Apps:
                    await LoadAppsAsync(id);
                    break;
                case View.Outages:
                    await LoadOutagesAsync(id);
                    break;
                default:
                    await LoadUsageAsync(id);
                    break;
            }
        }
        catch (ServiceUnavailableException)
        {
            if (id == _requestId)
            {
                ShowUnavailable();
            }
        }
        catch (ServiceRequestException ex)
        {
            if (id == _requestId)
            {
                SetBanner(ex.Message, warning: true);
            }
        }
    }

    private void ShowUnavailable()
    {
        SetBanner("The NetLedger service isn't running, so history isn't being recorded or shown. "
                  + "Live speeds on the taskbar still work. Reinstalling NetLedger or starting the \"NetLedger\" service fixes this.", warning: true);
        _buckets = [];
        _chart.SetBars([]);
        _list.Items.Clear();
        _footer.Text = "";
    }

    private void SetBanner(string? text, bool warning)
    {
        _banner.Visible = text is not null;
        _banner.Text = text ?? "";
        Palette palette = Theme.Current;
        _banner.ForeColor = warning ? palette.Warning : palette.SubtleText;
        _banner.BackColor = palette.Surface;
    }

    private async Task LoadUsageAsync(int id)
    {
        (DateTime from, DateTime to, BucketKind kind, string title) = UsagePeriod();
        _period.Text = title;
        _next.Enabled = _view != View.Years && to <= DateTime.Now;
        var choice = _adapterBox.SelectedItem as AdapterChoice;
        var request = new IpcRequest
        {
            Op = Ops.Usage,
            Usage = new UsageQuery
            {
                Kind = kind,
                FromLocal = from,
                ToLocal = to,
                WeekStart = _settings.WeekStart,
                Adapters = choice?.Selection ?? AdapterSelection.Counted,
                AdapterId = choice?.Id ?? 0,
            },
        };
        IpcResponse response = await _client.CallAsync(request);
        if (id != _requestId)
        {
            return;
        }

        SetBanner(_view == View.Minutes ? "Minute detail is kept for a limited time (30 days by default)." : null, warning: false);
        _buckets = response.Buckets ?? [];
        DateTime now = DateTime.Now;
        var bars = new List<ChartBar>(_buckets.Count);
        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Columns.Clear();
        _list.Columns.Add(kind switch { BucketKind.Minute => "Minute", BucketKind.Hour => "Hour", BucketKind.Day => "Day", BucketKind.Week => "Week", BucketKind.Month => "Month", _ => "Year" }, Px(260));
        _list.Columns.Add("Download", Px(120), HorizontalAlignment.Right);
        _list.Columns.Add("Upload", Px(120), HorizontalAlignment.Right);
        _list.Columns.Add("Total", Px(120), HorizontalAlignment.Right);
        long totalRx = 0, totalTx = 0;
        int elapsedBuckets = 0;
        foreach (UsageBucket bucket in _buckets)
        {
            DateTime next = kind is BucketKind.Minute or BucketKind.Hour
                ? bucket.LocalStart.AddSeconds(kind == BucketKind.Minute ? 60 : 3600)
                : PeriodMath.NextBucket(bucket.LocalStart, kind);
            bool future = bucket.LocalStart > now;
            bool current = bucket.LocalStart <= now && now < next;
            bars.Add(new ChartBar(ShortLabel(bucket.LocalStart, kind), LongLabel(bucket.LocalStart, kind), bucket.Received, bucket.Sent, future, current));
            totalRx += bucket.Received;
            totalTx += bucket.Sent;
            if (!future)
            {
                elapsedBuckets++;
            }

            var item = new ListViewItem(LongLabel(bucket.LocalStart, kind));
            item.SubItems.Add(ByteFormat.Size(bucket.Received));
            item.SubItems.Add(ByteFormat.Size(bucket.Sent));
            item.SubItems.Add(ByteFormat.Size(bucket.Total));
            if (future)
            {
                item.ForeColor = Theme.Current.SubtleText;
            }
            else if (current)
            {
                item.Font = _boldFont ??= new Font(_list.Font, FontStyle.Bold);
            }

            _list.Items.Add(item);
        }

        _list.EndUpdate();
        _chart.EmptyText = "No traffic recorded in this period";
        _chart.SetBars(bars);
        string unit = kind switch { BucketKind.Minute => "minute", BucketKind.Hour => "hour", BucketKind.Day => "day", BucketKind.Week => "week", BucketKind.Month => "month", _ => "year" };
        string average = elapsedBuckets > 0 ? $"   ·   Average per {unit}: {ByteFormat.Size((totalRx + totalTx) / elapsedBuckets)}" : "";
        _footer.Text = $"Total  ↓ {ByteFormat.Size(totalRx)}   ↑ {ByteFormat.Size(totalTx)}   Σ {ByteFormat.Size(totalRx + totalTx)}{average}";
    }

    private static string ShortLabel(DateTime start, BucketKind kind) => kind switch
    {
        BucketKind.Minute => start.ToString("HH:mm", Culture),
        BucketKind.Hour => start.ToString("HH", Culture),
        BucketKind.Day => start.Day.ToString(Culture),
        BucketKind.Week => start.ToString("d MMM", Culture),
        BucketKind.Month => start.ToString("MMM", Culture),
        _ => start.Year.ToString(Culture),
    };

    private string LongLabel(DateTime start, BucketKind kind) => kind switch
    {
        BucketKind.Minute => start.ToString("HH:mm", Culture),
        BucketKind.Hour => $"{start:HH:mm} – {start.AddHours(1):HH:mm}",
        BucketKind.Day => start.ToString("dddd d MMMM", Culture),
        BucketKind.Week => WeekLabel(start),
        BucketKind.Month => start.ToString("MMMM yyyy", Culture),
        _ => start.Year.ToString(Culture),
    };

    private string WeekLabel(DateTime start)
    {
        string range = $"{start.ToString("d MMM", Culture)} – {start.AddDays(6).ToString("d MMM yyyy", Culture)}";
        return _settings.WeekStart == DayOfWeek.Monday ? $"Week {ISOWeek.GetWeekOfYear(start)}: {range}" : range;
    }

    private async Task LoadAppsAsync(int id)
    {
        (DateTime from, DateTime to, string title) = AppPeriod();
        _period.Text = title;
        _next.Enabled = _appRange != AppRange.AllTime && to <= DateTime.Now;
        var range = new RangeQuery { FromLocal = from, ToLocal = to };
        IpcResponse apps = await _client.CallAsync(new IpcRequest { Op = Ops.Apps, Range = range });
        long adapterTotal = 0;
        if (from != default)
        {
            IpcResponse usage = await _client.CallAsync(new IpcRequest
            {
                Op = Ops.Usage,
                Usage = new UsageQuery { Kind = BucketKind.Day, FromLocal = from, ToLocal = to, WeekStart = _settings.WeekStart },
            });
            adapterTotal = usage.Buckets?.Sum(b => b.Total) ?? 0;
        }

        if (id != _requestId)
        {
            return;
        }

        if (_status is { AppTracking: false })
        {
            SetBanner(_status.AppTrackingError is { } error
                ? error
                : "Per-app tracking is turned off in Settings.", warning: true);
        }
        else
        {
            SetBanner("Per-app figures count TCP/UDP payload only, so they add up to a little less than the adapter totals.", warning: false);
        }

        List<AppUsageRow> rows = apps.Apps ?? [];
        long appTotal = rows.Sum(r => r.Received + r.Sent);
        long grand = Math.Max(appTotal, adapterTotal);
        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Columns.Clear();
        _list.Columns.Add("App", Px(300));
        _list.Columns.Add("Download", Px(110), HorizontalAlignment.Right);
        _list.Columns.Add("Upload", Px(110), HorizontalAlignment.Right);
        _list.Columns.Add("Total", Px(110), HorizontalAlignment.Right);
        _list.Columns.Add("Share", Px(70), HorizontalAlignment.Right);
        foreach (AppUsageRow row in rows)
        {
            long total = row.Received + row.Sent;
            var item = new ListViewItem(row.Name) { ImageIndex = IconFor(row.Path), ToolTipText = row.Path ?? row.Key };
            item.SubItems.Add(ByteFormat.Size(row.Received));
            item.SubItems.Add(ByteFormat.Size(row.Sent));
            item.SubItems.Add(ByteFormat.Size(total));
            item.SubItems.Add(grand > 0 ? (total * 100.0 / grand).ToString("0.0", Culture) + " %" : "");
            _list.Items.Add(item);
        }

        if (adapterTotal > appTotal && rows.Count > 0)
        {
            long other = adapterTotal - appTotal;
            var item = new ListViewItem("Other (protocol overhead, untracked traffic)") { ForeColor = Theme.Current.SubtleText, ImageIndex = -1 };
            item.SubItems.Add("");
            item.SubItems.Add("");
            item.SubItems.Add(ByteFormat.Size(other));
            item.SubItems.Add((other * 100.0 / grand).ToString("0.0", Culture) + " %");
            _list.Items.Add(item);
        }

        _list.ShowItemToolTips = true;
        _list.EndUpdate();
        _footer.Text = rows.Count == 0
            ? "No per-app data for this period."
            : $"{rows.Count} apps   ·   ↓ {ByteFormat.Size(rows.Sum(r => r.Received))}   ↑ {ByteFormat.Size(rows.Sum(r => r.Sent))}";
    }

    private int IconFor(string? path)
    {
        if (path is null)
        {
            return -1;
        }

        if (_iconIndex.TryGetValue(path, out int index))
        {
            return index;
        }

        index = -1;
        try
        {
            using Icon? icon = Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                _icons.Images.Add(icon.ToBitmap());
                index = _icons.Images.Count - 1;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        _iconIndex[path] = index;
        return index;
    }

    private async Task LoadOutagesAsync(int id)
    {
        _period.Text = "Internet outages";
        IpcResponse response = await _client.CallAsync(new IpcRequest { Op = Ops.Outages, Range = new RangeQuery() });
        if (id != _requestId)
        {
            return;
        }

        string? note = _status switch
        {
            null => null,
            { Online: false, OfflineSinceUtc: long since } => $"Offline since {Local(since):HH:mm:ss}.",
            { Online: true } => "Online now. Outages are detected by a small web request every minute (see Settings).",
            _ => "The outage log is off, or no check has finished yet.",
        };
        SetBanner(note, warning: _status?.Online == false);

        List<OutageRow> rows = response.Outages ?? [];
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Columns.Clear();
        _list.Columns.Add("Lost connection", Px(240));
        _list.Columns.Add("Back online", Px(200));
        _list.Columns.Add("Duration", Px(140), HorizontalAlignment.Right);
        long downtime = 0;
        foreach (OutageRow row in rows)
        {
            long end = row.EndUtc ?? now;
            downtime += end - row.StartUtc;
            var item = new ListViewItem(Local(row.StartUtc).ToString("ddd d MMM yyyy  HH:mm:ss", Culture));
            item.SubItems.Add(row.EndUtc is long e ? Local(e).ToString("HH:mm:ss", Culture) : "still offline");
            item.SubItems.Add(Duration(end - row.StartUtc));
            _list.Items.Add(item);
        }

        _list.EndUpdate();
        _footer.Text = rows.Count == 0
            ? "No outages recorded."
            : $"{rows.Count} outages   ·   total downtime {Duration(downtime)}";
    }

    private static DateTime Local(long utcSeconds) => DateTimeOffset.FromUnixTimeSeconds(utcSeconds).LocalDateTime;

    internal static string Duration(long seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalMinutes < 1)
        {
            return $"{span.Seconds} s";
        }

        if (span.TotalHours < 1)
        {
            return $"{span.Minutes} min {span.Seconds} s";
        }

        return span.TotalDays < 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : $"{(int)span.TotalDays} d {span.Hours} h";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icons.Dispose();
            _boldFont?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>A strip showing how much of the data quota this billing cycle has used.</summary>
    private sealed class QuotaStrip : Control
    {
        public QuotaStrip()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public QuotaStatus? Status { get; set; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Palette palette = Theme.Current;
            e.Graphics.Clear(palette.Window);
            if (Status is null)
            {
                return;
            }

            int pad = (int)Math.Round(12 * DeviceDpi / 96.0);
            string text = $"Data quota: {ByteFormat.Size(Status.UsedBytes)} of {ByteFormat.Size(Status.LimitBytes)} ({Status.Percent.ToString("0", Culture)} %)"
                          + $"   ·   cycle {Status.CycleStartLocal.ToString("d MMM", Culture)} – {Status.CycleEndLocal.AddDays(-1).ToString("d MMM", Culture)}"
                          + $"   ·   projected {ByteFormat.Size(Status.ProjectedBytes)}";
            TextRenderer.DrawText(e.Graphics, text, Font, new Point(pad, pad / 3), palette.Text);
            int barY = Height - pad / 2 - 6;
            var track = new Rectangle(pad, barY, Width - 2 * pad, (int)Math.Max(4, 4 * DeviceDpi / 96.0));
            using (var trackBrush = new SolidBrush(palette.Grid))
            {
                e.Graphics.FillRectangle(trackBrush, track);
            }

            Color color = Status.Percent >= 100 ? palette.Danger : Status.Percent >= 80 ? palette.Warning : palette.Accent;
            using var fill = new SolidBrush(color);
            e.Graphics.FillRectangle(fill, track.Left, track.Top, (int)(track.Width * Math.Clamp(Status.Percent / 100, 0, 1)), track.Height);
        }
    }
}
