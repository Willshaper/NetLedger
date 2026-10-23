using System.Globalization;
using NetLedger.Core;
using NetLedger.Core.Ipc;
using NetLedger.Core.Native;

namespace NetLedger.Tray;

internal sealed class SettingsForm : ScaledForm
{
    private readonly LedgerClient _client;
    private readonly BackupFlow _backup;
    private readonly UiSettings _edit;
    private readonly FlowLayoutPanel _pageStrip = new();
    private readonly Panel _pageHost = new();
    private readonly List<(NavButton Button, Panel Page)> _pages = new();

    // General
    private readonly CheckBox _startup = new() { Text = "Start NetLedger when I sign in", AutoSize = true };
    private readonly ComboBox _units = Combo("Automatic (bits: Kbit, Mbit, Gbit)", "Kbit", "Mbit", "Automatic (bytes: KB/s, MB/s, GB/s)", "KB/s", "MB/s");
    private readonly NumericUpDown _smoothing = Number(1, 10);
    private readonly ComboBox _sizeBase = Combo("Binary: 1 GB = 1024 MB (like Windows)", "Decimal: 1 GB = 1000 MB (like most ISPs)");
    private readonly ComboBox _weekStart = Combo(Enum.GetNames<DayOfWeek>());

    // Taskbar
    private readonly ComboBox _placement = Combo("Inside the taskbar (recommended)", "Floating over the taskbar");
    private readonly CheckBox _showGraph = new() { Text = "Show a speed graph", AutoSize = true };
    private readonly NumericUpDown _graphWidth = Number(20, 200);
    private readonly NumericUpDown _graphSeconds = Number(20, 600);
    private readonly CheckBox _quotaBar = new() { Text = "Show quota usage as a thin bar under the speeds", AutoSize = true };
    private readonly Button _uploadColor = new() { AutoSize = true };
    private readonly Button _downloadColor = new() { AutoSize = true };
    private readonly Button _font = new() { AutoSize = true };
    private readonly NumericUpDown _gap = Number(0, 2000);

    // Adapters
    private readonly ListView _adapterList = new();

    // Quota
    private readonly CheckBox _quotaEnabled = new() { Text = "Track a monthly data quota", AutoSize = true };
    private readonly NumericUpDown _quotaAmount = new() { Minimum = 0.01m, Maximum = 1_000_000, DecimalPlaces = 2, Increment = 10, Width = 120 };
    private readonly ComboBox _quotaUnit = Combo("GB", "TB");
    private readonly NumericUpDown _cycleDay = Number(1, 31);
    private readonly CheckBox _countUpload = new() { Text = "Count uploads toward the quota", AutoSize = true };
    private readonly TextBox _alerts = new() { Width = 160 };

    // Connectivity
    private readonly CheckBox _outageLog = new() { Text = "Log internet outages", AutoSize = true };
    private readonly TextBox _probeUrl = new();
    private readonly TextBox _probeExpect = new();
    private readonly NumericUpDown _probeInterval = Number(10, 3600);

    // Data
    private readonly CheckBox _appTracking = new() { Text = "Record usage per app", AutoSize = true };
    private readonly NumericUpDown _retention = Number(1, 366);
    private readonly Label _serviceState = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly Button _undo = new() { Text = "Undo last restore", AutoSize = true };

    private readonly List<Control> _serviceControls = new();
    private ServiceSettings? _service;
    private List<AdapterInfo> _adapters = [];
    private Font _widgetFont;
    private bool _applying;

    public SettingsForm(LedgerClient client, BackupFlow backup, UiSettings current)
    {
        _client = client;
        _backup = backup;
        _edit = current.Clone();
        _widgetFont = new Font(_edit.FontFamily, _edit.FontSize);
        Text = "NetLedger settings";
        Icon = TrayApp.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9f);
        Size = new Size(Px(640), Px(560));
        MinimumSize = new Size(Px(560), Px(480));
        MaximizeBox = false;
        BuildLayout();
        ScaleFixedWidths();
        LoadUi();
        Theme.Apply(this);
    }

    /// <summary>Raised when the user applies changes to display settings.</summary>
    public event Action<UiSettings>? UiSettingsApplied;

    /// <summary>Raised after service settings or adapter choices were changed.</summary>
    public event Action? ServiceSettingsApplied;

    private static ComboBox Combo(params string[] items)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        box.Items.AddRange(items);
        return box;
    }

    private static NumericUpDown Number(int min, int max) => new() { Minimum = min, Maximum = max, Width = 90 };

    // The field initialisers above use 96-DPI widths.
    private void ScaleFixedWidths()
    {
        Control[] fixedWidth =
        [
            _units, _smoothing, _sizeBase, _weekStart, _placement, _graphWidth, _graphSeconds, _gap,
            _quotaAmount, _quotaUnit, _cycleDay, _alerts, _probeInterval, _retention,
        ];
        foreach (Control control in fixedWidth)
        {
            control.Width = Px(control.Width);
        }

        _serviceState.MaximumSize = new Size(Px(_serviceState.MaximumSize.Width), 0);
    }

    // ---- layout ----

    private void BuildLayout()
    {
        _pageStrip.Dock = DockStyle.Top;
        _pageStrip.AutoSize = true;
        _pageStrip.Padding = new Padding(Px(10), Px(10), Px(10), Px(2));
        _pageHost.Dock = DockStyle.Fill;

        TableLayoutPanel general = Page("General");
        AddRow(general, null, _startup);
        AddRow(general, "Speed units", _units);
        AddRow(general, "Average speed over", WithSuffix(_smoothing, "seconds"));
        AddRow(general, "Size units", _sizeBase);
        AddRow(general, "Weeks start on", _weekStart);

        TableLayoutPanel taskbar = Page("Taskbar");
        AddRow(taskbar, "Placement", _placement);
        AddRow(taskbar, null, _showGraph);
        AddRow(taskbar, "Graph width", WithSuffix(_graphWidth, "px"));
        AddRow(taskbar, "Graph covers", WithSuffix(_graphSeconds, "seconds"));
        AddRow(taskbar, "Upload colour", _uploadColor);
        AddRow(taskbar, "Download colour", _downloadColor);
        AddRow(taskbar, "Font", _font);
        AddRow(taskbar, "Gap to the tray icons", WithSuffix(_gap, "px"));
        AddRow(taskbar, null, _quotaBar);
        _uploadColor.Click += (_, _) => PickColor(_uploadColor);
        _downloadColor.Click += (_, _) => PickColor(_downloadColor);
        _font.Click += (_, _) => PickFont();

        Panel adapters = AddPage("Adapters");
        var adapterHelp = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = Px(52),
            Text = "Ticked adapters make up the internet total, the quota and the taskbar speeds. "
                   + "By default only physical adapters count: VPN and virtual-switch traffic also passes through a physical adapter, so counting both would double it.",
        };
        _adapterList.Dock = DockStyle.Fill;
        _adapterList.View = View.Details;
        _adapterList.CheckBoxes = true;
        _adapterList.FullRowSelect = true;
        _adapterList.Columns.Add("Adapter", Px(170));
        _adapterList.Columns.Add("Description", Px(210));
        _adapterList.Columns.Add("Type", Px(80));
        _adapterList.Columns.Add("Choice", Px(80));
        // The list also raises check events by itself (for instance when its handle is created the first time the
        // tab is shown), so the events only refresh the label; the choice is read from the ticks on Apply.
        _adapterList.ItemChecked += (_, e) =>
        {
            if (e.Item.SubItems.Count > 3 && AdapterFor(e.Item) is { } adapter)
            {
                e.Item.SubItems[3].Text = e.Item.Checked == AutomaticChoice(adapter) ? "Automatic" : "Manual";
            }
        };
        var automatic = new Button { Text = "Use the automatic choice for all", AutoSize = true, Dock = DockStyle.Bottom };
        automatic.Click += (_, _) =>
        {
            foreach (ListViewItem item in _adapterList.Items)
            {
                if (AdapterFor(item) is { } adapter)
                {
                    item.Checked = AutomaticChoice(adapter);
                }
            }
        };
        adapters.Controls.Add(_adapterList);
        adapters.Controls.Add(automatic);
        adapters.Controls.Add(adapterHelp);
        _serviceControls.AddRange([_adapterList, automatic]);

        TableLayoutPanel quota = Page("Quota");
        AddRow(quota, null, _quotaEnabled);
        var amount = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _quotaUnit.Width = 70;
        amount.Controls.AddRange([_quotaAmount, _quotaUnit]);
        AddRow(quota, "Quota per cycle", amount);
        AddRow(quota, "Cycle starts on day", WithSuffix(_cycleDay, "of the month (a shorter month uses its last day)"));
        AddRow(quota, null, _countUpload);
        AddRow(quota, "Notify at", WithSuffix(_alerts, "% (comma-separated)"));
        _serviceControls.AddRange([_quotaEnabled, _quotaAmount, _quotaUnit, _cycleDay, _countUpload, _alerts]);

        TableLayoutPanel connectivity = Page("Connectivity");
        AddRow(connectivity, null, _outageLog);
        AddRow(connectivity, "Check address", _probeUrl, stretch: true);
        AddRow(connectivity, "Expected text", _probeExpect, stretch: true);
        AddRow(connectivity, "Check every", WithSuffix(_probeInterval, "seconds (every 10 s while offline)"));
        AddRow(connectivity, null, Note("The check is one tiny web request, by default to the address Windows itself uses to decide "
                                        + "whether you're online. Two failures in a row count as an outage."));
        _serviceControls.AddRange([_outageLog, _probeUrl, _probeExpect, _probeInterval]);

        TableLayoutPanel data = Page("Data");
        AddRow(data, null, _appTracking);
        AddRow(data, "Keep minute detail for", WithSuffix(_retention, "days (hourly history is kept forever)"));
        var backupButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        var export = new Button { Text = "Back up data…", AutoSize = true };
        var import = new Button { Text = "Restore from backup…", AutoSize = true };
        export.Click += async (_, _) => await _backup.ExportAsync(this);
        import.Click += async (_, _) =>
        {
            await _backup.ImportAsync(this);
            await RefreshUndoAsync();
        };
        _undo.Click += async (_, _) =>
        {
            await _backup.UndoAsync(this);
            await RefreshUndoAsync();
        };
        backupButtons.Controls.AddRange([export, import, _undo]);
        AddRow(data, "Backups", backupButtons);
        AddRow(data, null, Note("Restoring replaces any hours that are in both the backup and this PC's history. "
                                + "A snapshot is taken first, so a restore can be undone."));
        AddRow(data, "Service", _serviceState);
        _serviceControls.AddRange([_appTracking, _retention, export, import, _undo]);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(Px(8)),
        };
        var apply = new Button { Text = "Apply", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var ok = new Button { Text = "OK", AutoSize = true };
        apply.Click += async (_, _) => await ApplyAsync();
        ok.Click += async (_, _) =>
        {
            if (await ApplyAsync())
            {
                Close();
            }
        };
        cancel.Click += (_, _) => Close();
        buttons.Controls.AddRange([apply, cancel, ok]);
        AcceptButton = ok;
        CancelButton = cancel;

        // Fill first, then the edges: the last control added docks outermost.
        Controls.Add(_pageHost);
        Controls.Add(buttons);
        Controls.Add(_pageStrip);
    }

    private Panel AddPage(string title)
    {
        bool first = _pages.Count == 0;
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(Px(12)), Visible = first };
        var button = new NavButton(title)
        {
            Checked = first,
            Margin = new Padding(0, 0, Px(4), 0),
            Padding = new Padding(Px(8), Px(3), Px(8), Px(3)),
        };
        button.CheckedChanged += (_, _) =>
        {
            if (button.Checked)
            {
                foreach ((_, Panel other) in _pages)
                {
                    other.Visible = other == page;
                }
            }
        };
        _pageStrip.Controls.Add(button);
        _pageHost.Controls.Add(page);
        _pages.Add((button, page));
        return page;
    }

    private TableLayoutPanel Page(string title)
    {
        Panel page = AddPage(title);
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(table);
        return table;
    }

    /// <param name="stretch">Fill the rest of the row (long text such as addresses) instead of keeping a fixed width.</param>
    private void AddRow(TableLayoutPanel table, string? label, Control control, bool stretch = false)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Margin = new Padding(Px(4), Px(5), Px(4), Px(5));
        if (stretch)
        {
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        }

        if (label is null)
        {
            table.Controls.Add(control, 0, row);
            table.SetColumnSpan(control, 2);
            return;
        }

        table.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(Px(4), Px(9), Px(16), Px(4)) }, 0, row);
        table.Controls.Add(control, 1, row);
    }

    private Control WithSuffix(Control control, string suffix)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        control.Margin = new Padding(0, 0, Px(6), 0);
        panel.Controls.Add(control);
        panel.Controls.Add(new Label { Text = suffix, AutoSize = true, Margin = new Padding(0, Px(4), 0, 0) });
        return panel;
    }

    private Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(Px(540), 0),
        ForeColor = Theme.Current.SubtleText,
    };

    // ---- loading ----

    private void LoadUi()
    {
        _startup.Checked = UiSettings.StartsWithWindows;
        _units.SelectedIndex = (int)_edit.RateUnit;
        _smoothing.Value = _edit.SmoothingSeconds;
        _sizeBase.SelectedIndex = (int)_edit.SizeBase;
        _weekStart.SelectedIndex = (int)_edit.WeekStart;
        _placement.SelectedIndex = (int)_edit.Placement;
        _showGraph.Checked = _edit.ShowGraph;
        _graphWidth.Value = _edit.GraphWidth;
        _graphSeconds.Value = _edit.GraphSeconds;
        _quotaBar.Checked = _edit.ShowQuotaBar;
        _gap.Value = Math.Min(_gap.Maximum, _edit.GapFromTray);
        ShowColor(_uploadColor, _edit.Upload);
        ShowColor(_downloadColor, _edit.Download);
        _font.Text = $"{_edit.FontFamily}, {_edit.FontSize.ToString("0.#", CultureInfo.CurrentCulture)} pt";
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await LoadServiceAsync();
    }

    private async Task LoadServiceAsync()
    {
        bool canUndo = false;
        try
        {
            IpcResponse settings = await _client.CallAsync(Ops.GetSettings);
            IpcResponse adapters = await _client.CallAsync(Ops.Adapters);
            IpcResponse status = await _client.CallAsync(Ops.Status);
            _service = settings.Settings;
            _adapters = adapters.Adapters ?? [];
            ServiceStatus s = status.Status!;
            canUndo = s.CanUndoImport;
            _serviceState.Text = $"Running, version {s.Version}.{Environment.NewLine}"
                                 + (s.AppTracking ? "Per-app tracking is on." : s.AppTrackingError ?? "Per-app tracking is off.")
                                 + (s.EventsLost > 0 ? $" {s.EventsLost:N0} network events were dropped under load." : "");
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or ServiceRequestException)
        {
            _service = null;
            _serviceState.Text = "Not running. Settings on the Adapters, Quota, Connectivity and Data tabs need the NetLedger service.";
            _serviceState.ForeColor = Theme.Current.Warning;
        }

        foreach (Control control in _serviceControls)
        {
            control.Enabled = _service is not null;
        }

        _undo.Enabled = _service is not null && canUndo;
        if (_service is null)
        {
            return;
        }

        QuotaSettings quota = _service.Quota;
        _quotaEnabled.Checked = quota.Enabled;
        if (!quota.Enabled && quota.LimitBytes == new QuotaSettings().LimitBytes)
        {
            // Never set up: offer a round number in whichever units are in use.
            _quotaUnit.SelectedIndex = 1;
            _quotaAmount.Value = 1;
        }
        else
        {
            decimal terabytes = (decimal)quota.LimitBytes / ByteFormat.UnitBytes(4);
            bool useTb = terabytes >= 1 && Math.Round(terabytes, 2) == Math.Round(terabytes, 6);
            _quotaUnit.SelectedIndex = useTb ? 1 : 0;
            _quotaAmount.Value = Math.Clamp(Math.Round((decimal)quota.LimitBytes / ByteFormat.UnitBytes(useTb ? 4 : 3), 2), _quotaAmount.Minimum, _quotaAmount.Maximum);
        }

        _cycleDay.Value = quota.CycleStartDay;
        _countUpload.Checked = quota.CountUpload;
        _alerts.Text = string.Join(", ", quota.AlertPercents);
        _outageLog.Checked = _service.OutageLog;
        _probeUrl.Text = _service.ProbeUrl;
        _probeExpect.Text = _service.ProbeExpect;
        _probeInterval.Value = Math.Clamp(_service.ProbeIntervalSeconds, 10, 3600);
        _appTracking.Checked = _service.AppTracking;
        _retention.Value = Math.Clamp(_service.MinuteRetentionDays, 1, 366);
        FillAdapters();
    }

    private async Task RefreshUndoAsync()
    {
        try
        {
            _undo.Enabled = (await _client.CallAsync(Ops.Status)).Status?.CanUndoImport == true;
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or ServiceRequestException)
        {
            _undo.Enabled = false;
        }
    }

    private AdapterInfo? AdapterFor(ListViewItem item) =>
        item.Tag is long id ? _adapters.FirstOrDefault(a => a.Id == id) : null;

    private static bool AutomaticChoice(AdapterInfo adapter) => IfTable.CountsByDefault(adapter.IsHardware, adapter.IfType);

    private void FillAdapters()
    {
        _adapterList.BeginUpdate();
        _adapterList.Items.Clear();
        foreach (AdapterInfo adapter in _adapters)
        {
            bool counts = adapter.CountsOverride ?? AutomaticChoice(adapter);
            string name = adapter.Present ? adapter.Name
                : adapter.LastSeenUtc > 0 ? $"{adapter.Name} (last seen {DateTimeOffset.FromUnixTimeSeconds(adapter.LastSeenUtc).LocalDateTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture)})"
                : $"{adapter.Name} (not present)";
            var item = new ListViewItem(name) { Tag = adapter.Id, Checked = counts };
            item.SubItems.Add(adapter.Description);
            item.SubItems.Add(adapter.IsHardware ? IfTable.DescribeType(adapter.IfType) : "Virtual");
            item.SubItems.Add(counts == AutomaticChoice(adapter) ? "Automatic" : "Manual");
            if (!adapter.Present)
            {
                item.ForeColor = Theme.Current.SubtleText;
            }

            _adapterList.Items.Add(item);
        }

        _adapterList.EndUpdate();
    }

    // ---- pickers ----

    private void ShowColor(Button button, Color color)
    {
        button.Text = "     " + Theme.ToHex(color);
        button.Tag = color;
        button.Image?.Dispose();
        var swatch = new Bitmap(Px(14), Px(14));
        using (Graphics g = Graphics.FromImage(swatch))
        {
            g.Clear(color);
        }

        button.Image = swatch;
        button.ImageAlign = ContentAlignment.MiddleLeft;
    }

    private void PickColor(Button button)
    {
        using var dialog = new ColorDialog { Color = (Color)button.Tag!, FullOpen = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            ShowColor(button, dialog.Color);
        }
    }

    private void PickFont()
    {
        using var dialog = new FontDialog { Font = _widgetFont, ShowEffects = false, MinSize = 6, MaxSize = 16, FontMustExist = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _widgetFont.Dispose();
            _widgetFont = new Font(dialog.Font.FontFamily, dialog.Font.SizeInPoints);
            _font.Text = $"{_widgetFont.FontFamily.Name}, {_widgetFont.SizeInPoints.ToString("0.#", CultureInfo.CurrentCulture)} pt";
        }
    }

    // ---- saving ----

    private async Task<bool> ApplyAsync()
    {
        // A quick double click on OK must not send everything twice.
        if (_applying)
        {
            return false;
        }

        _applying = true;
        try
        {
            return await ApplyCoreAsync();
        }
        finally
        {
            _applying = false;
        }
    }

    private async Task<bool> ApplyCoreAsync()
    {
        _edit.RateUnit = (RateUnit)_units.SelectedIndex;
        _edit.SmoothingSeconds = (int)_smoothing.Value;
        _edit.SizeBase = (SizeBase)_sizeBase.SelectedIndex;
        _edit.WeekStart = (DayOfWeek)_weekStart.SelectedIndex;
        _edit.Placement = (WidgetPlacement)_placement.SelectedIndex;
        _edit.ShowGraph = _showGraph.Checked;
        _edit.GraphWidth = (int)_graphWidth.Value;
        _edit.GraphSeconds = (int)_graphSeconds.Value;
        _edit.ShowQuotaBar = _quotaBar.Checked;
        _edit.GapFromTray = (int)_gap.Value;
        _edit.UploadColor = Theme.ToHex((Color)_uploadColor.Tag!);
        _edit.DownloadColor = Theme.ToHex((Color)_downloadColor.Tag!);
        _edit.FontFamily = _widgetFont.FontFamily.Name;
        _edit.FontSize = _widgetFont.SizeInPoints;

        try
        {
            if (_startup.Checked != UiSettings.StartsWithWindows)
            {
                UiSettings.StartsWithWindows = _startup.Checked;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Dialogs.Warn(this, $"Couldn't change the startup setting: {ex.Message}");
        }

        UiSettingsApplied?.Invoke(_edit.Clone());

        if (_service is null)
        {
            return true;
        }

        if (!TryReadServiceSettings(out ServiceSettings? updated, out string? problem))
        {
            Dialogs.Warn(this, problem!);
            return false;
        }

        try
        {
            await _client.CallAsync(new IpcRequest { Op = Ops.SetSettings, Settings = updated });
            _service = updated;
            foreach (ListViewItem item in _adapterList.Items)
            {
                if (AdapterFor(item) is not { } adapter)
                {
                    continue;
                }

                // A tick that matches the automatic rule stays automatic; only a deliberate difference is stored.
                bool? choice = item.Checked == AutomaticChoice(adapter) ? null : item.Checked;
                if (choice != adapter.CountsOverride)
                {
                    await _client.CallAsync(new IpcRequest { Op = Ops.SetAdapter, Adapter = new AdapterUpdate { Id = adapter.Id, CountsOverride = choice } });
                    adapter.CountsOverride = choice;
                }
            }

            ServiceSettingsApplied?.Invoke();
            return true;
        }
        catch (Exception ex) when (ex is ServiceUnavailableException or ServiceRequestException)
        {
            Dialogs.Warn(this, $"The service didn't accept the settings: {ex.Message}");
            return false;
        }
    }

    private bool TryReadServiceSettings(out ServiceSettings? settings, out string? problem)
    {
        settings = null;
        problem = null;
        var alerts = new List<int>();
        foreach (string part in _alerts.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part.TrimEnd('%'), NumberStyles.Integer, CultureInfo.CurrentCulture, out int value) || value is < 1 or > 1000)
            {
                problem = $"\"{part}\" isn't a valid alert percentage (1–1000).";
                return false;
            }

            alerts.Add(value);
        }

        if (!Uri.TryCreate(_probeUrl.Text.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            problem = "The check address must start with http:// or https://.";
            return false;
        }

        settings = new ServiceSettings
        {
            AppTracking = _appTracking.Checked,
            MinuteRetentionDays = (int)_retention.Value,
            OutageLog = _outageLog.Checked,
            ProbeUrl = uri.ToString(),
            ProbeExpect = _probeExpect.Text.Trim(),
            ProbeIntervalSeconds = (int)_probeInterval.Value,
            Quota = new QuotaSettings
            {
                Enabled = _quotaEnabled.Checked,
                LimitBytes = (long)(_quotaAmount.Value * ByteFormat.UnitBytes(_quotaUnit.SelectedIndex == 1 ? 4 : 3)),
                CycleStartDay = (int)_cycleDay.Value,
                CountUpload = _countUpload.Checked,
                AlertPercents = alerts.Distinct().Order().ToList(),
            },
        };
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _widgetFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
