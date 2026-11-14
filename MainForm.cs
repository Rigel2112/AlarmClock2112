using System.ComponentModel;
using System.Drawing;
using System.Drawing.Text;
using System.Media;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Forms;

namespace AlarmClock2112;

public sealed class AlarmItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Alarm";
    public TimeSpan Time { get; set; } = new(7, 0, 0);
    public bool[] Days { get; set; } = [true, true, true, true, true, true, true];
    public bool Enabled { get; set; } = true;
    public string SoundFile { get; set; } = "";
    public DateTime SnoozeUntil { get; set; } = DateTime.MinValue;
    public string LastFiredKey { get; set; } = "";
}

public sealed class SettingsData
{
    public List<AlarmItem> Alarms { get; set; } = [];
    public string FontName { get; set; } = "Consolas";
    public decimal FontSize { get; set; } = 54;
    public int ClockColorArgb { get; set; } = Color.FromArgb(105, 214, 255).ToArgb();
    public bool Use24Hour { get; set; } = false;
    public bool ShowSeconds { get; set; } = true;
    public bool MinimizeToTray { get; set; } = false;
    public bool AlwaysOnTop { get; set; } = false;
    public bool HideTitleBar { get; set; } = false;
    public bool UseInternetTime { get; set; } = true;
    public int WindowWidth { get; set; } = 520;
    public int WindowHeight { get; set; } = 190;
}

public sealed class MainForm : Form
{
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlarmClock2112", "settings.json");
    private SettingsData settings = new();
    private readonly Label clock = new();
    private Label status = new();
    private readonly Label alarmIndicator = new();
    private readonly Button settingsButton = new() { Text = "⚙ Settings", AutoSize = true };
    private ListView alarmList = new();
    private ComboBox fonts = new();
    private Button colorButton = new() { Text = "Text color...", AutoSize = true, MinimumSize = new Size(100, 30) };
    private NumericUpDown fontSizePicker = new() { Minimum = 8, Maximum = 500, Increment = 2, Width = 72 };
    private CheckBox format24 = new() { Text = "24-hour time", AutoSize = true };
    private CheckBox showSeconds = new() { Text = "Show seconds", AutoSize = true };
    private CheckBox trayOption = new() { Text = "Minimize to tray", AutoSize = true };
    private CheckBox ntpOption = new() { Text = "Use internet time (NTP)", AutoSize = true };
    private CheckBox topOption = new() { Text = "Always on top", AutoSize = true };
    private CheckBox borderOption = new() { Text = "Hide title bar", AutoSize = true };
    private readonly NotifyIcon tray = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private TimeSpan ntpOffset = TimeSpan.Zero;
    private DateTime nextSyncUtc = DateTime.MinValue;
    private readonly object soundLock = new();
    private SoundPlayer? player;
    private System.Windows.Forms.Timer? beepTimer;
    private AlarmItem? ringing;
    private Form? ringingWindow;
    private bool exiting;
    private Font? ownedClockFont;
    private readonly ContextMenuStrip clockMenu = new();

    public MainForm()
    {
        Text = "AlarmClock 2112";
        MinimumSize = new Size(180, 125);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Size = new Size(480, 170);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 27, 34);
        ForeColor = Color.WhiteSmoke;
        LoadSettings();
        TopMost = settings.AlwaysOnTop;
        FormBorderStyle = settings.HideTitleBar ? FormBorderStyle.None : FormBorderStyle.FixedSingle;
        clockMenu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        clockMenu.Items.Add("Minimize", null, (_, _) => WindowState = FormWindowState.Minimized);
        clockMenu.Items.Add("Show title bar", null, (_, _) => { settings.HideTitleBar = false; ApplyWindowOptions(); SaveSettings(); });
        clockMenu.Items.Add("Exit", null, (_, _) => Close());
        ContextMenuStrip = clockMenu;
        clockMenu.Items[2].Visible = settings.HideTitleBar;
        
        BuildUi();
        Shown += (_, _) => { Tick(); UpdateClockFont(); };
        tray.Icon = SystemIcons.Application;
        tray.Text = "AlarmClock 2112";
        tray.Visible = true;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => Restore());
        menu.Items.Add("Exit", null, (_, _) => { exiting = true; Close(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Restore();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized && settings.MinimizeToTray) Hide(); };
        FormClosing += (_, _) => { SaveSettings(); StopSound(); tray.Visible = false; tray.Dispose(); };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        _ = SynchronizeClockAsync();
    }

    private static Button MakeButton(string label, EventHandler handler)
    {
        var button = new Button { Text = label, AutoSize = true, MinimumSize = new Size(95, 32), FlatStyle = FlatStyle.System };
        button.Click += handler;
        return button;
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 2, BackColor = BackColor };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        Controls.Add(root);
        clock.Dock = DockStyle.Fill;
        clock.ContextMenuStrip = clockMenu;
        clock.MouseDown += BeginWindowDrag;
        clock.TextAlign = ContentAlignment.MiddleCenter;
        clock.AutoEllipsis = false;
        clock.ForeColor = Color.FromArgb(settings.ClockColorArgb);
        root.Controls.Add(clock, 0, 0);
        root.MouseDown += BeginWindowDrag;
        
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        alarmIndicator.Dock = DockStyle.Fill;
        alarmIndicator.TextAlign = ContentAlignment.MiddleLeft;
        alarmIndicator.ForeColor = Color.FromArgb(160, 210, 230);
        alarmIndicator.Font = new Font("Segoe UI", 9);
        footer.Controls.Add(alarmIndicator, 0, 0);
        settingsButton.FlatStyle = FlatStyle.Flat;
        settingsButton.FlatAppearance.BorderSize = 0;
        settingsButton.ForeColor = Color.LightGray;
        settingsButton.Click += (_, _) => OpenSettings();
        footer.Controls.Add(settingsButton, 1, 0);
        root.Controls.Add(footer, 0, 1);
        UpdateIndicator();
        UpdateClockFont();
    }

    private void OpenSettings()
    {
        // Settings controls belong to this dialog only. Never reparent controls
        // from a closed dialog: WinForms may have disposed them already.
        status = new Label();
        alarmList = new ListView();
        fonts = new ComboBox();
        colorButton = new Button { Text = "Text color...", AutoSize = true, MinimumSize = new Size(100, 30) };
        fontSizePicker = new NumericUpDown { Minimum = 8, Maximum = 500, Increment = 2, Width = 72 };
        format24 = new CheckBox { Text = "24-hour time", AutoSize = true };
        showSeconds = new CheckBox { Text = "Show seconds", AutoSize = true };
        trayOption = new CheckBox { Text = "Minimize to tray", AutoSize = true };
        ntpOption = new CheckBox { Text = "Use internet time (NTP)", AutoSize = true };
        topOption = new CheckBox { Text = "Always on top", AutoSize = true };
        borderOption = new CheckBox { Text = "Hide title bar", AutoSize = true };
        using var dialog = new Form
        {
            Text = "AlarmClock 2112 — Settings", StartPosition = FormStartPosition.CenterParent,
            Size = new Size(840, 570), MinimumSize = new Size(680, 450),
            BackColor = BackColor, ForeColor = ForeColor
        };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        dialog.Controls.Add(layout);
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        top.Controls.Add(new Label { Text = "Clock font:", AutoSize = true, Margin = new Padding(0, 9, 8, 0) });
        fonts.DropDownStyle = ComboBoxStyle.DropDownList;
        fonts.Width = 230;
        if (fonts.Items.Count == 0)
        {
            using var collection = new InstalledFontCollection();
            foreach (var family in collection.Families.OrderBy(x => x.Name)) fonts.Items.Add(family.Name);
            fonts.SelectedIndexChanged += (_, _) => { settings.FontName = fonts.SelectedItem?.ToString() ?? "Consolas"; UpdateClockFont(); SaveSettings(); };
        }
        if (fonts.Items.Contains(settings.FontName)) fonts.SelectedItem = settings.FontName;
        else if (fonts.Items.Contains("Consolas")) fonts.SelectedItem = "Consolas";
        top.Controls.Add(fonts);
        top.Controls.Add(new Label { Text = "Size (pt):", AutoSize = true, Margin = new Padding(12, 9, 5, 0) });
        fontSizePicker.Value = Math.Clamp(settings.FontSize, fontSizePicker.Minimum, fontSizePicker.Maximum);
        colorButton.Click += (_, _) =>
        {
            using var picker = new ColorDialog { Color = Color.FromArgb(settings.ClockColorArgb), FullOpen = true, AnyColor = true };
            if (picker.ShowDialog(dialog) != DialogResult.OK) return;
            settings.ClockColorArgb = picker.Color.ToArgb();
            clock.ForeColor = picker.Color;
            colorButton.BackColor = picker.Color;
            colorButton.ForeColor = picker.Color.GetBrightness() > 0.55f ? Color.Black : Color.White;
            SaveSettings();
        };
        fontSizePicker.ValueChanged += (_, _) => { settings.FontSize = fontSizePicker.Value; UpdateClockFont(); SaveSettings(); };
        top.Controls.Add(fontSizePicker);
        colorButton.BackColor = Color.FromArgb(settings.ClockColorArgb);
        colorButton.ForeColor = colorButton.BackColor.GetBrightness() > 0.55f ? Color.Black : Color.White;
        colorButton.Margin = new Padding(12, 2, 0, 0);
        top.Controls.Add(colorButton);
        format24.Checked = settings.Use24Hour;
        format24.Margin = new Padding(14, 8, 0, 0);
        format24.CheckedChanged += (_, _) => { settings.Use24Hour = format24.Checked; UpdateClockFont(); SaveSettings(); RefreshAlarms(); };
        top.Controls.Add(format24);
        showSeconds.Checked = settings.ShowSeconds;
        showSeconds.Margin = new Padding(12, 8, 0, 0);
        showSeconds.CheckedChanged += (_, _) => { settings.ShowSeconds = showSeconds.Checked; Tick(); UpdateClockFont(); SaveSettings(); };
        top.Controls.Add(showSeconds);
        layout.Controls.Add(top, 0, 0);
        alarmList.Dock = DockStyle.Fill;
        alarmList.View = View.Details;
        alarmList.FullRowSelect = true;
        alarmList.MultiSelect = false;
        alarmList.BackColor = Color.FromArgb(36, 40, 50);
        alarmList.ForeColor = Color.WhiteSmoke;
        if (alarmList.Columns.Count == 0)
        {
            alarmList.Columns.Add("On", 48);
            alarmList.Columns.Add("Time", 100);
            alarmList.Columns.Add("Name", 185);
            alarmList.Columns.Add("Repeat", 260);
            alarmList.Columns.Add("Sound", 160);
            alarmList.DoubleClick += (_, _) => EditSelected();
        }
        layout.Controls.Add(alarmList, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        actions.Controls.Add(MakeButton("Add alarm", (_, _) => EditAlarm(null)));
        actions.Controls.Add(MakeButton("Edit", (_, _) => EditSelected()));
        actions.Controls.Add(MakeButton("Delete", (_, _) => DeleteSelected()));
        actions.Controls.Add(MakeButton("Enable / Disable", (_, _) => ToggleSelected()));
        layout.Controls.Add(actions, 0, 2);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        trayOption.Checked = settings.MinimizeToTray;
        trayOption.CheckedChanged += (_, _) => { settings.MinimizeToTray = trayOption.Checked; SaveSettings(); };
        bottom.Controls.Add(trayOption);
        ntpOption.Checked = settings.UseInternetTime;
        ntpOption.Margin = new Padding(14, 3, 0, 0);
        ntpOption.CheckedChanged += (_, _) => { settings.UseInternetTime = ntpOption.Checked; SaveSettings(); if (ntpOption.Checked) _ = SynchronizeClockAsync(); else { ntpOffset = TimeSpan.Zero; SetNtpStatus("System time"); } };
        bottom.Controls.Add(ntpOption);
        topOption.Checked = settings.AlwaysOnTop;
        borderOption.Checked = settings.HideTitleBar;
        topOption.CheckedChanged += (_, _) => { settings.AlwaysOnTop = topOption.Checked; ApplyWindowOptions(); SaveSettings(); };
        borderOption.CheckedChanged += (_, _) => { settings.HideTitleBar = borderOption.Checked; ApplyWindowOptions(); SaveSettings(); };
        var windowOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        windowOptions.Controls.Add(topOption);
        windowOptions.Controls.Add(borderOption);
        layout.Controls.Add(windowOptions, 0, 4);
        bottom.Controls.Add(MakeButton("Sync now", (_, _) => _ = SynchronizeClockAsync()));
        status.AutoSize = true;
        status.Margin = new Padding(12, 10, 0, 0);
        bottom.Controls.Add(status);
        layout.Controls.Add(bottom, 0, 3);
        RefreshAlarms();
        dialog.ShowDialog(this);

    }

    private void ApplyWindowOptions()
    {
        TopMost = settings.AlwaysOnTop;
        FormBorderStyle = settings.HideTitleBar ? FormBorderStyle.None : FormBorderStyle.FixedSingle;
        var showTitle = clockMenu.Items[2];
        showTitle.Visible = settings.HideTitleBar;
        UpdateClockFont();
    }

    private void BeginWindowDrag(object? sender, MouseEventArgs e)
    {
        if (!settings.HideTitleBar || e.Button != MouseButtons.Left) return;
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    private void UpdateIndicator()
    {
        int count = settings.Alarms.Count(a => a.Enabled && a.Days.Length == 7 && a.Days.Any(d => d));
        alarmIndicator.Text = count == 0 ? "○ No alarms" : $"● Alarm {(count == 1 ? "set" : $"set ({count})")}";
        alarmIndicator.ForeColor = count == 0 ? Color.Gray : Color.FromArgb(110, 225, 145);
    }

    private void UpdateClockFont()
    {
        // Font size is the source of truth. Measure a worst-case time string and
        // resize the *client area* to fit it, rather than relying on layout events.
        if (IsDisposed) return;
        float points = (float)Math.Clamp(settings.FontSize, 8m, 500m);
        using var measurementFont = CreateClockFont(points);
        using var graphics = CreateGraphics();
        string sample = settings.Use24Hour
            ? (settings.ShowSeconds ? "23:59:59" : "23:59")
            : (settings.ShowSeconds ? "12:59:59 PM" : "12:59 PM");
        SizeF measured = graphics.MeasureString(sample, measurementFont,
            int.MaxValue, StringFormat.GenericTypographic);
        // WinForms Label uses GDI TextRenderer, which can require more room than
        // Graphics.MeasureString (GDI+), particularly for decorative TTF fonts.
        // Measure using the same renderer as the label and reserve extra space.
        Size gdiSize = TextRenderer.MeasureText(sample, measurementFont,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        int textWidth = Math.Max((int)Math.Ceiling(measured.Width), gdiSize.Width);
        int requiredWidth = Math.Max(180, (int)Math.Ceiling(textWidth * 1.25) + 80);
        int requiredHeight = Math.Max(125, (int)Math.Ceiling(measurementFont.GetHeight(graphics)) + 66);
        // Never dispose a font inherited from WinForms/Control.DefaultFont.
        // Only dispose fonts created and owned by this form.
        var previousOwnedFont = ownedClockFont;
        ownedClockFont = CreateClockFont(points);
        clock.Font = ownedClockFont;
        previousOwnedFont?.Dispose();
        ClientSize = new Size(requiredWidth, requiredHeight);
    }

    private Font CreateClockFont(float size)
    {
        try { return new Font(settings.FontName, size, FontStyle.Regular, GraphicsUnit.Point); }
        catch { return new Font("Consolas", size, FontStyle.Regular, GraphicsUnit.Point); }
    }

    private DateTime Now => DateTime.Now + (settings.UseInternetTime ? ntpOffset : TimeSpan.Zero);

    private void Tick()
    {
        var now = Now;
        clock.Text = now.ToString(settings.Use24Hour
            ? (settings.ShowSeconds ? "HH:mm:ss" : "HH:mm")
            : (settings.ShowSeconds ? "hh:mm:ss tt" : "hh:mm tt"));
        if (settings.UseInternetTime && DateTime.UtcNow >= nextSyncUtc)
        {
            nextSyncUtc = DateTime.UtcNow.AddMinutes(30);
            _ = SynchronizeClockAsync();
        }
        foreach (var alarm in settings.Alarms)
        {
            if (!alarm.Enabled) continue;
            bool snoozing = alarm.SnoozeUntil > DateTime.MinValue;
            bool due = snoozing
                ? now >= alarm.SnoozeUntil
                : alarm.Days.Length == 7 && alarm.Days[(int)now.DayOfWeek] && now.Hour == alarm.Time.Hours && now.Minute == alarm.Time.Minutes;
            string key = snoozing ? $"s:{alarm.SnoozeUntil:O}" : $"d:{now:yyyy-MM-dd}:{alarm.Time}";
            if (due && alarm.LastFiredKey != key && ringing is null)
            {
                alarm.LastFiredKey = key;
                alarm.SnoozeUntil = DateTime.MinValue;
                SaveSettings();
                Ring(alarm);
                break;
            }
        }
    }

    private void SetNtpStatus(string message)
    {
        if (!status.IsDisposed && !Disposing && !IsDisposed) status.Text = message;
    }

    private async Task SynchronizeClockAsync()
    {
        if (!settings.UseInternetTime) return;
        SetNtpStatus("Syncing NTP...");
        try
        {
            var offset = await Task.Run(QueryNtpOffset);
            if (IsDisposed || !settings.UseInternetTime) return;
            ntpOffset = offset;
            SetNtpStatus($"NTP synced ({offset.TotalSeconds:+0.0;-0.0;0.0}s)");
            nextSyncUtc = DateTime.UtcNow.AddHours(1);
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            SetNtpStatus("NTP unavailable; using " + (ntpOffset == TimeSpan.Zero ? "system time" : "last offset"));
            nextSyncUtc = DateTime.UtcNow.AddMinutes(5);
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private static TimeSpan QueryNtpOffset()
    {
        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = 3000;
        udp.Connect("time.cloudflare.com", 123);
        byte[] request = new byte[48];
        request[0] = 0x23;
        var t1 = DateTime.UtcNow;
        udp.Send(request, request.Length);
        IPEndPoint? endpoint = null;
        byte[] response = udp.Receive(ref endpoint);
        var t4 = DateTime.UtcNow;
        if (response.Length < 48 || (response[0] & 7) != 4 || (response[0] >> 6) == 3)
            throw new InvalidDataException("Invalid NTP response");
        DateTime ReadStamp(int pos)
        {
            uint sec = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(response, pos)) is int signed ? unchecked((uint)signed) : 0;
            uint frac = unchecked((uint)IPAddress.NetworkToHostOrder(BitConverter.ToInt32(response, pos + 4)));
            return new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(sec + frac / 4294967296.0);
        }
        var t2 = ReadStamp(32);
        var t3 = ReadStamp(40);
        if (t3.Year < 2020) throw new InvalidDataException("Invalid NTP timestamp");
        return TimeSpan.FromTicks(((t2 - t1).Ticks + (t3 - t4).Ticks) / 2);
    }

    private void Ring(AlarmItem alarm)
    {
        ringing = alarm;
        StartSound(alarm.SoundFile);
        var dialog = new Form { Text = "AlarmClock 2112 - ALARM", StartPosition = FormStartPosition.CenterScreen, Size = new Size(430, 210), TopMost = true, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        ringingWindow = dialog;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(18), WrapContents = false };
        panel.Controls.Add(new Label { Text = alarm.Name, Font = new Font("Segoe UI", 20, FontStyle.Bold), AutoSize = true, MaximumSize = new Size(370, 60) });
        panel.Controls.Add(new Label { Text = Now.ToString("f"), AutoSize = true });
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        buttons.Controls.Add(MakeButton("Snooze 5 min", (_, _) => { alarm.SnoozeUntil = Now.AddMinutes(5); SaveSettings(); dialog.Close(); }));
        buttons.Controls.Add(MakeButton("Dismiss", (_, _) => dialog.Close()));
        panel.Controls.Add(buttons);
        dialog.Controls.Add(panel);
        dialog.FormClosed += (_, _) => { StopSound(); ringing = null; ringingWindow = null; };
        dialog.Show(this);
        dialog.Activate();
    }

    private void StartSound(string file)
    {
        StopSound();
        if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
        {
            if (Path.GetExtension(file).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                try { player = new SoundPlayer(file); player.PlayLooping(); return; } catch { player = null; }
            }
            else
            {
                // Windows Media Player COM is not required: use built-in MCI for MP3 playback.
                try
                {
                    string alias = "alarm2112";
                    Mci($"open \"{file.Replace("\"", "") }\" type mpegvideo alias {alias}");
                    Mci($"play {alias} repeat");
                    playingMci = true;
                    return;
                }
                catch { Mci("close alarm2112"); }
            }
        }
        beepTimer = new System.Windows.Forms.Timer { Interval = 1100 };
        beepTimer.Tick += (_, _) => SystemSounds.Exclamation.Play();
        beepTimer.Start();
        SystemSounds.Exclamation.Play();
    }

    private bool playingMci;
    [System.Runtime.InteropServices.DllImport("winmm.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int mciSendString(string command, System.Text.StringBuilder? result, int length, IntPtr callback);
    private static void Mci(string command)
    {
        int code = mciSendString(command, null, 0, IntPtr.Zero);
        if (code != 0) throw new InvalidOperationException($"MCI error {code}");
    }
    private void StopSound()
    {
        beepTimer?.Stop(); beepTimer?.Dispose(); beepTimer = null;
        player?.Stop(); player?.Dispose(); player = null;
        if (playingMci) { try { Mci("stop alarm2112"); Mci("close alarm2112"); } catch { } playingMci = false; }
    }

    private AlarmItem? Selected => alarmList.SelectedItems.Count > 0 ? alarmList.SelectedItems[0].Tag as AlarmItem : null;
    private void EditSelected() { if (Selected is { } alarm) EditAlarm(alarm); }
    private void DeleteSelected()
    {
        if (Selected is not { } alarm) return;
        if (MessageBox.Show($"Delete '{alarm.Name}'?", "Confirm", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        settings.Alarms.Remove(alarm); SaveSettings(); RefreshAlarms();
    }
    private void ToggleSelected()
    {
        if (Selected is not { } alarm) return;
        alarm.Enabled = !alarm.Enabled; SaveSettings(); RefreshAlarms();
    }
    private void EditAlarm(AlarmItem? original)
    {
        AlarmEditor dialog;
        try
        {
            var source = original ?? new AlarmItem();
            dialog = new AlarmEditor(source);
        }
        catch (Exception ex)
        {
            ShowAlarmError(ex);
            return;
        }
        using (dialog)
        {
            try { if (dialog.ShowDialog(this) != DialogResult.OK) return; }
            catch (Exception ex) { ShowAlarmError(ex); return; }
        }
        if (original is null) settings.Alarms.Add(dialog.Result);
        else
        {
            original.Name = dialog.Result.Name;
            original.Time = dialog.Result.Time;
            original.Days = dialog.Result.Days;
            original.Enabled = dialog.Result.Enabled;
            original.SoundFile = dialog.Result.SoundFile;
        }
        SaveSettings(); RefreshAlarms(); UpdateIndicator();
    }
    private void ShowAlarmError(Exception ex)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AlarmClock2112", "error.log");
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.AppendAllText(path, $"[{DateTime.Now:O}]\n{ex}\n\n"); } catch { }
        MessageBox.Show(this, $"Alarm editor error:\n\n{ex.Message}\n\nDetails saved to:\n{path}", "AlarmClock 2112", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    private void RefreshAlarms()
    {
        alarmList.Items.Clear();
        UpdateIndicator();
        foreach (var a in settings.Alarms.OrderBy(x => x.Time))
        {
            string days = a.Days.Length == 7 && a.Days.All(x => x) ? "Every day" :
                string.Join(", ", Enum.GetValues<DayOfWeek>().Where(d => a.Days.Length == 7 && a.Days[(int)d]).Select(d => d.ToString()[..3]));
            var item = new ListViewItem([a.Enabled ? "Yes" : "No", DateTime.Today.Add(a.Time).ToString(settings.Use24Hour ? "HH:mm" : "h:mm tt"), a.Name, days, string.IsNullOrEmpty(a.SoundFile) ? "Default beep" : Path.GetFileName(a.SoundFile)]) { Tag = a };
            alarmList.Items.Add(item);
        }
    }
    private void LoadSettings()
    {
        try { if (File.Exists(settingsPath)) settings = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(settingsPath)) ?? new(); }
        catch { settings = new(); }
    }
    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
    }
    private void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); }
}

public sealed class AlarmEditor : Form
{
    public AlarmItem Result { get; }
    private readonly TextBox name = new() { Width = 270 };
    private readonly DateTimePicker time = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "hh:mm tt", ShowUpDown = true, Width = 120 };
    private readonly CheckBox[] days = new CheckBox[7];
    private readonly TextBox sound = new() { Width = 245, ReadOnly = true };
    private readonly CheckBox enabled = new() { Text = "Enabled", AutoSize = true };
    private readonly SoundPreview preview = new();
    private readonly Button testSound = new() { Text = "▶ Test", Width = 76 };

    public AlarmEditor(AlarmItem source)
    {
        Result = new AlarmItem { Id = source.Id, Name = source.Name, Time = source.Time, Days = (bool[])source.Days.Clone(), Enabled = source.Enabled, SoundFile = source.SoundFile };
        Text = "Alarm settings";
        Size = new Size(590, 360);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(15), ColumnCount = 1, RowCount = 9 };
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "Alarm name", AutoSize = true });
        name.Text = source.Name;
        layout.Controls.Add(name);
        var timeRow = new FlowLayoutPanel { AutoSize = true };
        timeRow.Controls.Add(new Label { Text = "Time:", AutoSize = true, Margin = new Padding(0, 7, 8, 0) });
        time.Value = DateTime.Today.Add(source.Time);
        timeRow.Controls.Add(time);
        enabled.Checked = source.Enabled;
        enabled.Margin = new Padding(20, 7, 0, 0);
        timeRow.Controls.Add(enabled);
        layout.Controls.Add(timeRow);
        layout.Controls.Add(new Label { Text = "Repeat on weekdays (Sunday first):", AutoSize = true });
        var dayRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        string[] labels = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
        for (int i = 0; i < 7; i++)
        {
            days[i] = new CheckBox { Text = labels[i], Checked = source.Days.Length == 7 && source.Days[i], AutoSize = true };
            dayRow.Controls.Add(days[i]);
        }
        layout.Controls.Add(dayRow);
        layout.Controls.Add(new Label { Text = "Alarm sound (optional WAV or MP3):", AutoSize = true });
        var soundRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        sound.Text = source.SoundFile;
        soundRow.Controls.Add(sound);
        var browse = new Button { Text = "Browse...", AutoSize = true };
        browse.Click += (_, _) => { using var picker = new OpenFileDialog { Filter = "Audio files|*.wav;*.mp3|All files|*.*" }; if (picker.ShowDialog(this) == DialogResult.OK) { StopPreview(); sound.Text = picker.FileName; } };
        soundRow.Controls.Add(browse);
        var clear = new Button { Text = "Clear", AutoSize = true };
        clear.Click += (_, _) => { StopPreview(); sound.Text = ""; };
        soundRow.Controls.Add(clear);
        testSound.Click += (_, _) =>
        {
            if (preview.IsPlaying) { StopPreview(); return; }
            try { preview.Play(sound.Text); testSound.Text = "■ Stop"; }
            catch (Exception ex) { StopPreview(); MessageBox.Show(this, ex.Message, "Audio preview", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        soundRow.Controls.Add(testSound);
        layout.Controls.Add(soundRow);
        FormClosed += (_, _) => { preview.Dispose(); };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "Save", Width = 85, DialogResult = DialogResult.None };
        ok.Click += (_, _) =>
        {
            if (!days.Any(d => d.Checked)) { MessageBox.Show("Select at least one weekday."); return; }
            Result.Name = string.IsNullOrWhiteSpace(name.Text) ? "Alarm" : name.Text.Trim();
            Result.Time = new TimeSpan(time.Value.Hour, time.Value.Minute, 0);
            Result.Days = days.Select(d => d.Checked).ToArray();
            Result.SoundFile = sound.Text;
            Result.Enabled = enabled.Checked;
            DialogResult = DialogResult.OK;
            Close();
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(new Button { Text = "Cancel", Width = 85, DialogResult = DialogResult.Cancel });
        layout.Controls.Add(buttons);
        AcceptButton = ok;
    }

    private void StopPreview()
    {
        preview.Stop();
        testSound.Text = "▶ Test";
    }
}

// Independent playback instance so previewing never interrupts a ringing alarm.
internal sealed class SoundPreview : IDisposable
{
    private SoundPlayer? wav;
    private System.Windows.Forms.Timer? beep;
    private bool mp3Open;
    public bool IsPlaying { get; private set; }

    [System.Runtime.InteropServices.DllImport("winmm.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int mciSendString(string command, System.Text.StringBuilder? result, int length, IntPtr callback);
    private static void Mci(string command)
    {
        int result = mciSendString(command, null, 0, IntPtr.Zero);
        if (result != 0) throw new InvalidOperationException($"Cannot play this audio file (Windows MCI error {result}).");
    }

    public void Play(string path)
    {
        Stop();
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The selected audio file could not be found.", path);
            if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                wav = new SoundPlayer(path);
                try { wav.Load(); wav.PlayLooping(); }
                catch { wav.Dispose(); wav = null; throw; }
                IsPlaying = true;
                return;
            }
            if (Path.GetExtension(path).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Mci($"open \"{path.Replace("\"", "")}\" type mpegvideo alias alarm2112preview");
                    mp3Open = true;
                    Mci("play alarm2112preview repeat");
                    IsPlaying = true;
                    return;
                }
                catch { Stop(); throw; }
            }
            throw new NotSupportedException("Preview supports WAV and MP3 audio files.");
        }
        beep = new System.Windows.Forms.Timer { Interval = 1100 };
        beep.Tick += (_, _) => SystemSounds.Exclamation.Play();
        beep.Start();
        SystemSounds.Exclamation.Play();
        IsPlaying = true;
    }

    public void Stop()
    {
        beep?.Stop(); beep?.Dispose(); beep = null;
        wav?.Stop(); wav?.Dispose(); wav = null;
        if (mp3Open)
        {
            try { Mci("stop alarm2112preview"); } catch { }
            try { Mci("close alarm2112preview"); } catch { }
            mp3Open = false;
        }
        IsPlaying = false;
    }
    public void Dispose() => Stop();
}
