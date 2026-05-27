using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Windows.Media.Control;
using Microsoft.Win32;

internal sealed class SettingsForm : Form
{
    private readonly WidgetForm owner;
    private readonly WidgetSettings baseline;
    private NumericUpDown widthBox;
    private NumericUpDown heightBox;
    private NumericUpDown leftXBox;
    private NumericUpDown bottomYBox;
    private NumericUpDown backgroundTransparencyBox;
    private NumericUpDown applicationTransparencyBox;
    private NumericUpDown clockWidthBox;
    private NumericUpDown clockHeightBox;
    private NumericUpDown clockLeftXBox;
    private NumericUpDown clockBottomYBox;
    private NumericUpDown clockTransparencyBox;
    private NumericUpDown dockIconSizeBox;
    private NumericUpDown dockMagnificationBox;
    private NumericUpDown dockBottomMarginBox;
    private TrackBar widthSlider;
    private TrackBar heightSlider;
    private TrackBar leftXSlider;
    private TrackBar bottomYSlider;
    private TrackBar backgroundTransparencySlider;
    private TrackBar applicationTransparencySlider;
    private TrackBar clockWidthSlider;
    private TrackBar clockHeightSlider;
    private TrackBar clockLeftXSlider;
    private TrackBar clockBottomYSlider;
    private TrackBar clockTransparencySlider;
    private TrackBar dockIconSizeSlider;
    private TrackBar dockMagnificationSlider;
    private TrackBar dockBottomMarginSlider;
    private ComboBox visibilityCombo;
    private ComboBox thermalTestCombo;
    private Button alertTestButton;
    private CheckBox startupCheck;
    private CheckBox powerSavingCheck;
    private CheckBox hoverOpacityCheck;
    private CheckBox clockUse24HourCheck;
    private CheckBox clockCalendarCheck;
    private CheckBox clockPowerCheck;
    private CheckBox dockEnabledCheck;
    private TextBox dockItemsBox;
    private FlowLayoutPanel availableMetricsPanel;
    private TableLayoutPanel metricSlotsPanel;
    private Panel[] metricSlotPanels;
    private string draggedMetricId;
    private int draggedSourceSlotIndex;
    private bool initializing;
    private bool saved;

    public bool OwnerFormClosing { get; set; }

    public SettingsForm(WidgetForm owner, WidgetSettings baseline)
    {
        this.owner = owner;
        this.baseline = baseline.Clone();
        this.baseline.Normalize();

        this.Text = "性能小窗设置";
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.ShowInTaskbar = false;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.AutoScroll = true;
        this.AutoScrollMinSize = GetDesiredClientSize();
        this.ClientSize = FitClientSizeToScreen(GetDesiredClientSize());
        this.Font = new Font("Segoe UI", 10.0f);
        this.BackColor = DarkTheme.Window;
        this.ForeColor = DarkTheme.Text;

        BuildControls();
        LoadControls(this.baseline);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!this.saved && !this.OwnerFormClosing)
        {
            this.owner.RevertSettings(this.baseline);
        }

        base.OnFormClosing(e);
    }

    private void BuildControls()
    {
        TableLayoutPanel root = new TableLayoutPanel();
        root.Location = new Point(0, 0);
        root.Size = GetDesiredClientSize();
        root.Padding = new Padding(26, 20, 26, 20);
        root.BackColor = DarkTheme.Window;
        root.ColumnCount = 4;
        root.RowCount = 25;
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        this.Controls.Add(root);

        this.widthBox = BuildNumberBox(WidgetSettings.MinWidth, WidgetSettings.MaxWidth);
        this.heightBox = BuildNumberBox(WidgetSettings.MinHeight, WidgetSettings.MaxHeight);
        this.leftXBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.bottomYBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.backgroundTransparencyBox = BuildNumberBox(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.backgroundTransparencyBox.Increment = 1;
        this.applicationTransparencyBox = BuildNumberBox(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.applicationTransparencyBox.Increment = 1;
        this.clockWidthBox = BuildNumberBox(WidgetSettings.MinClockWidth, WidgetSettings.MaxClockWidth);
        this.clockHeightBox = BuildNumberBox(WidgetSettings.MinClockHeight, WidgetSettings.MaxClockHeight);
        this.clockLeftXBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.clockBottomYBox = BuildNumberBox(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.clockTransparencyBox = BuildNumberBox(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.clockTransparencyBox.Increment = 1;
        this.dockIconSizeBox = BuildNumberBox(WidgetSettings.MinDockIconSize, WidgetSettings.MaxDockIconSize);
        this.dockIconSizeBox.Increment = 2;
        this.dockMagnificationBox = BuildNumberBox(WidgetSettings.MinDockMagnificationPercent, WidgetSettings.MaxDockMagnificationPercent);
        this.dockMagnificationBox.Increment = 5;
        this.dockBottomMarginBox = BuildNumberBox(WidgetSettings.MinDockBottomMargin, WidgetSettings.MaxDockBottomMargin);
        this.dockBottomMarginBox.Increment = 2;
        this.widthSlider = BuildSlider(WidgetSettings.MinWidth, WidgetSettings.MaxWidth);
        this.heightSlider = BuildSlider(WidgetSettings.MinHeight, WidgetSettings.MaxHeight);
        this.leftXSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.bottomYSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.backgroundTransparencySlider = BuildSlider(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.applicationTransparencySlider = BuildSlider(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.clockWidthSlider = BuildSlider(WidgetSettings.MinClockWidth, WidgetSettings.MaxClockWidth);
        this.clockHeightSlider = BuildSlider(WidgetSettings.MinClockHeight, WidgetSettings.MaxClockHeight);
        this.clockLeftXSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Left, Screen.PrimaryScreen.Bounds.Right - 1);
        this.clockBottomYSlider = BuildSlider(Screen.PrimaryScreen.Bounds.Top, Screen.PrimaryScreen.Bounds.Bottom - 1);
        this.clockTransparencySlider = BuildSlider(WidgetSettings.MinBackgroundTransparency, WidgetSettings.MaxBackgroundTransparency);
        this.dockIconSizeSlider = BuildSlider(WidgetSettings.MinDockIconSize, WidgetSettings.MaxDockIconSize);
        this.dockMagnificationSlider = BuildSlider(WidgetSettings.MinDockMagnificationPercent, WidgetSettings.MaxDockMagnificationPercent);
        this.dockBottomMarginSlider = BuildSlider(WidgetSettings.MinDockBottomMargin, WidgetSettings.MaxDockBottomMargin);
        this.visibilityCombo = BuildCombo();
        this.thermalTestCombo = BuildCombo();
        this.alertTestButton = new Button();
        this.alertTestButton.Width = 96;
        this.alertTestButton.Height = 44;
        this.alertTestButton.MinimumSize = new Size(96, 44);
        this.alertTestButton.AutoSize = true;
        this.alertTestButton.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        this.alertTestButton.Padding = new Padding(14, 6, 14, 6);
        this.alertTestButton.Click += delegate
        {
            if (this.initializing)
            {
                return;
            }

            SetAlertTestButtonState(!GetAlertTestButtonState());
            this.owner.PreviewSettings(ReadControls());
        };
        this.startupCheck = new CheckBox();
        this.startupCheck.Text = "开机自动启动";
        this.startupCheck.AutoSize = true;
        this.startupCheck.ForeColor = DarkTheme.Text;
        this.startupCheck.BackColor = DarkTheme.Window;
        this.startupCheck.CheckedChanged += OnSettingChanged;
        this.powerSavingCheck = new CheckBox();
        this.powerSavingCheck.Text = "节能模式";
        this.powerSavingCheck.AutoSize = true;
        this.powerSavingCheck.ForeColor = DarkTheme.Text;
        this.powerSavingCheck.BackColor = DarkTheme.Window;
        this.powerSavingCheck.CheckedChanged += OnSettingChanged;
        this.hoverOpacityCheck = new CheckBox();
        this.hoverOpacityCheck.Text = "悬停透明 95%";
        this.hoverOpacityCheck.AutoSize = true;
        this.hoverOpacityCheck.ForeColor = DarkTheme.Text;
        this.hoverOpacityCheck.BackColor = DarkTheme.Window;
        this.hoverOpacityCheck.CheckedChanged += OnSettingChanged;
        this.clockUse24HourCheck = new CheckBox();
        this.clockUse24HourCheck.Text = "24 小时制";
        this.clockUse24HourCheck.AutoSize = true;
        this.clockUse24HourCheck.ForeColor = DarkTheme.Text;
        this.clockUse24HourCheck.BackColor = DarkTheme.Window;
        this.clockUse24HourCheck.CheckedChanged += OnSettingChanged;
        this.clockCalendarCheck = new CheckBox();
        this.clockCalendarCheck.Text = "启用日历";
        this.clockCalendarCheck.AutoSize = true;
        this.clockCalendarCheck.ForeColor = DarkTheme.Text;
        this.clockCalendarCheck.BackColor = DarkTheme.Window;
        this.clockCalendarCheck.CheckedChanged += OnSettingChanged;
        this.clockPowerCheck = new CheckBox();
        this.clockPowerCheck.Text = "显示功耗";
        this.clockPowerCheck.AutoSize = true;
        this.clockPowerCheck.ForeColor = DarkTheme.Text;
        this.clockPowerCheck.BackColor = DarkTheme.Window;
        this.clockPowerCheck.CheckedChanged += OnSettingChanged;
        this.dockEnabledCheck = new CheckBox();
        this.dockEnabledCheck.Text = "启用 Dock";
        this.dockEnabledCheck.AutoSize = true;
        this.dockEnabledCheck.ForeColor = DarkTheme.Text;
        this.dockEnabledCheck.BackColor = DarkTheme.Window;
        this.dockEnabledCheck.CheckedChanged += OnSettingChanged;
        this.dockItemsBox = new TextBox();
        this.dockItemsBox.Multiline = true;
        this.dockItemsBox.ScrollBars = ScrollBars.Vertical;
        this.dockItemsBox.AcceptsReturn = true;
        this.dockItemsBox.AcceptsTab = false;
        this.dockItemsBox.Dock = DockStyle.Fill;
        this.dockItemsBox.Font = new Font("Consolas", 9.5f);
        this.dockItemsBox.BackColor = DarkTheme.Control;
        this.dockItemsBox.ForeColor = DarkTheme.Text;
        this.dockItemsBox.BorderStyle = BorderStyle.FixedSingle;
        this.dockItemsBox.TextChanged += OnSettingChanged;
        this.metricSlotPanels = new Panel[WidgetSettings.DefaultMetricOrder.Length];

        Label title = new Label();
        title.Text = "性能小窗设置";
        title.Font = new Font("Segoe UI", 16.0f, FontStyle.Bold);
        title.ForeColor = DarkTheme.Text;
        title.BackColor = DarkTheme.Window;
        title.Dock = DockStyle.Fill;
        title.TextAlign = ContentAlignment.MiddleLeft;
        root.SetColumnSpan(title, 4);
        root.Controls.Add(title, 0, 0);

        AddSliderRow(root, 1, "窗口宽度", this.widthBox, this.widthSlider);
        AddSliderRow(root, 2, "窗口高度", this.heightBox, this.heightSlider);
        AddSliderRow(root, 3, "位置 X", this.leftXBox, this.leftXSlider);
        AddSliderRow(root, 4, "位置 Y", this.bottomYBox, this.bottomYSlider);
        AddSliderRow(root, 5, "黑色背景透明度", this.backgroundTransparencyBox, this.backgroundTransparencySlider);
        AddSliderRow(root, 6, "内容透明度", this.applicationTransparencyBox, this.applicationTransparencySlider);
        AddEditorRow(root, 7, "可见性", this.visibilityCombo);
        AddLabel(root, 8, "告警测试");
        Control alertEditor = BuildButtonEditor(this.alertTestButton);
        root.SetColumnSpan(alertEditor, 2);
        root.Controls.Add(alertEditor, 1, 8);
        AddEditorRow(root, 21, "温度测试", this.thermalTestCombo);

        FlowLayoutPanel runtimeOptions = new FlowLayoutPanel();
        runtimeOptions.Dock = DockStyle.Fill;
        runtimeOptions.FlowDirection = FlowDirection.LeftToRight;
        runtimeOptions.WrapContents = false;
        runtimeOptions.BackColor = DarkTheme.Window;
        runtimeOptions.Padding = new Padding(0, 10, 0, 0);
        this.startupCheck.Margin = new Padding(0, 0, 34, 0);
        this.powerSavingCheck.Margin = new Padding(0, 0, 34, 0);
        this.hoverOpacityCheck.Margin = new Padding(0, 0, 0, 0);
        runtimeOptions.Controls.Add(this.startupCheck);
        runtimeOptions.Controls.Add(this.powerSavingCheck);
        runtimeOptions.Controls.Add(this.hoverOpacityCheck);
        AddLabel(root, 9, "运行选项");
        root.SetColumnSpan(runtimeOptions, 2);
        root.Controls.Add(runtimeOptions, 1, 9);

        Control metricLayoutEditor = BuildMetricLayoutSidePanel();
        root.SetRowSpan(metricLayoutEditor, 23);
        root.Controls.Add(metricLayoutEditor, 3, 1);

        FlowLayoutPanel clockOptions = new FlowLayoutPanel();
        clockOptions.Dock = DockStyle.Fill;
        clockOptions.FlowDirection = FlowDirection.LeftToRight;
        clockOptions.WrapContents = false;
        clockOptions.BackColor = DarkTheme.Window;
        clockOptions.Padding = new Padding(0, 10, 0, 0);
        clockOptions.Controls.Add(this.clockUse24HourCheck);
        AddLabel(root, 10, "时间窗口");
        root.SetColumnSpan(clockOptions, 2);
        root.Controls.Add(clockOptions, 1, 10);
        AddSliderRow(root, 11, "时间宽度", this.clockWidthBox, this.clockWidthSlider);
        AddSliderRow(root, 12, "时间高度", this.clockHeightBox, this.clockHeightSlider);
        AddSliderRow(root, 13, "位置 X", this.clockLeftXBox, this.clockLeftXSlider);
        AddSliderRow(root, 14, "位置 Y", this.clockBottomYBox, this.clockBottomYSlider);
        FlowLayoutPanel calendarOptions = new FlowLayoutPanel();
        calendarOptions.Dock = DockStyle.Fill;
        calendarOptions.FlowDirection = FlowDirection.LeftToRight;
        calendarOptions.WrapContents = false;
        calendarOptions.BackColor = DarkTheme.Window;
        calendarOptions.Padding = new Padding(0, 10, 0, 0);
        this.clockCalendarCheck.Margin = new Padding(0, 0, 34, 0);
        this.clockPowerCheck.Margin = new Padding(0, 0, 0, 0);
        calendarOptions.Controls.Add(this.clockCalendarCheck);
        calendarOptions.Controls.Add(this.clockPowerCheck);
        AddLabel(root, 15, "时间信息");
        root.SetColumnSpan(calendarOptions, 2);
        root.Controls.Add(calendarOptions, 1, 15);

        FlowLayoutPanel dockOptions = new FlowLayoutPanel();
        dockOptions.Dock = DockStyle.Fill;
        dockOptions.FlowDirection = FlowDirection.LeftToRight;
        dockOptions.WrapContents = false;
        dockOptions.BackColor = DarkTheme.Window;
        dockOptions.Padding = new Padding(0, 10, 0, 0);
        dockOptions.Controls.Add(this.dockEnabledCheck);
        AddLabel(root, 16, "Dock");
        root.SetColumnSpan(dockOptions, 2);
        root.Controls.Add(dockOptions, 1, 16);
        AddSliderRow(root, 17, "Dock 图标", this.dockIconSizeBox, this.dockIconSizeSlider);
        AddSliderRow(root, 18, "Dock 放大", this.dockMagnificationBox, this.dockMagnificationSlider);
        AddSliderRow(root, 19, "Dock 底距", this.dockBottomMarginBox, this.dockBottomMarginSlider);
        AddEditorRow(root, 20, "Dock 项目", this.dockItemsBox);

        Button saveButton = new Button();
        saveButton.Text = "保存";
        saveButton.Width = 104;
        saveButton.Height = 36;
        saveButton.Click += delegate
        {
            this.owner.SaveSettings(ReadControls());
            this.saved = true;
            this.Close();
        };

        Button cancelButton = new Button();
        cancelButton.Text = "取消";
        cancelButton.Width = 96;
        cancelButton.Height = 36;
        cancelButton.Click += delegate { this.Close(); };

        Button resetButton = new Button();
        resetButton.Text = "重置";
        resetButton.Width = 96;
        resetButton.Height = 36;
        resetButton.Click += delegate
        {
            WidgetSettings defaults = WidgetSettings.CreateDefaults();
            defaults.StartupEnabled = Program.IsStartupEnabled();
            LoadControls(defaults);
            this.owner.PreviewSettings(ReadControls());
        };

        StyleButton(saveButton, true);
        StyleButton(cancelButton, false);
        StyleButton(resetButton, false);
        StyleButton(this.alertTestButton, false);

        FlowLayoutPanel buttons = new FlowLayoutPanel();
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Dock = DockStyle.Fill;
        buttons.BackColor = DarkTheme.Window;
        buttons.Padding = new Padding(0, 10, 0, 0);
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(resetButton);
        root.SetColumnSpan(buttons, 4);
        root.Controls.Add(buttons, 0, 24);

        this.visibilityCombo.Items.Add(new ComboOption("仅桌面可见", WidgetVisibilityMode.DesktopOnly));
        this.visibilityCombo.Items.Add(new ComboOption("一直可见", WidgetVisibilityMode.AlwaysVisible));
        this.visibilityCombo.Items.Add(new ComboOption("仅全屏不可见", WidgetVisibilityMode.HideWhenFullscreen));
        this.thermalTestCombo.Items.Add(new ComboOption("关闭", ThermalTestMode.Off));
        this.thermalTestCombo.Items.Add(new ComboOption("模拟 75 度", ThermalTestMode.Simulate75));
        this.thermalTestCombo.Items.Add(new ComboOption("模拟 100 度", ThermalTestMode.Simulate100));

        WirePair(this.widthBox, this.widthSlider);
        WirePair(this.heightBox, this.heightSlider);
        WirePair(this.leftXBox, this.leftXSlider);
        WirePair(this.bottomYBox, this.bottomYSlider);
        WirePair(this.backgroundTransparencyBox, this.backgroundTransparencySlider);
        WirePair(this.applicationTransparencyBox, this.applicationTransparencySlider);
        WirePair(this.clockWidthBox, this.clockWidthSlider);
        WirePair(this.clockHeightBox, this.clockHeightSlider);
        WirePair(this.clockLeftXBox, this.clockLeftXSlider);
        WirePair(this.clockBottomYBox, this.clockBottomYSlider);
        WirePair(this.clockTransparencyBox, this.clockTransparencySlider);
        WirePair(this.dockIconSizeBox, this.dockIconSizeSlider);
        WirePair(this.dockMagnificationBox, this.dockMagnificationSlider);
        WirePair(this.dockBottomMarginBox, this.dockBottomMarginSlider);
    }

    private static Size GetDesiredClientSize()
    {
        return new Size(1280, 1440);
    }

    private static Size FitClientSizeToScreen(Size desiredSize)
    {
        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        int margin = 64;
        int maxWidth = Math.Max(640, workArea.Width - margin);
        int maxHeight = Math.Max(520, workArea.Height - margin);
        int width = Math.Min(desiredSize.Width + SystemInformation.VerticalScrollBarWidth, maxWidth);
        int height = Math.Min(desiredSize.Height, maxHeight);
        return new Size(width, height);
    }

    private NumericUpDown BuildNumberBox(int min, int max)
    {
        NumericUpDown box = new NumericUpDown();
        box.Minimum = min;
        box.Maximum = max;
        box.Increment = 10;
        box.Dock = DockStyle.Fill;
        box.Font = new Font("Segoe UI", 10.5f);
        box.BackColor = DarkTheme.Control;
        box.ForeColor = DarkTheme.Text;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.ValueChanged += OnSettingChanged;
        return box;
    }

    private TrackBar BuildSlider(int min, int max)
    {
        TrackBar slider = new TrackBar();
        slider.Minimum = min;
        slider.Maximum = max;
        slider.TickStyle = TickStyle.None;
        slider.AutoSize = false;
        slider.Height = 34;
        slider.Dock = DockStyle.Fill;
        slider.BackColor = DarkTheme.Window;
        slider.SmallChange = 1;
        slider.LargeChange = Math.Max(10, (max - min) / 20);
        slider.ValueChanged += OnSettingChanged;
        return slider;
    }

    private ComboBox BuildCombo()
    {
        ComboBox combo = new ComboBox();
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Dock = DockStyle.Fill;
        combo.Font = new Font("Segoe UI", 10.5f);
        combo.BackColor = DarkTheme.Control;
        combo.ForeColor = DarkTheme.Text;
        combo.SelectedIndexChanged += OnSettingChanged;
        return combo;
    }

    private Control BuildMetricLayoutSidePanel()
    {
        TableLayoutPanel panel = new TableLayoutPanel();
        panel.Dock = DockStyle.Fill;
        panel.BackColor = DarkTheme.Window;
        panel.ColumnCount = 1;
        panel.RowCount = 2;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.Padding = new Padding(12, 0, 0, 0);

        Label label = new Label();
        label.Text = "栏目排序";
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        label.UseCompatibleTextRendering = true;
        label.ForeColor = DarkTheme.SubtleText;
        label.BackColor = DarkTheme.Window;

        Control editor = BuildMetricLayoutEditor();
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(editor, 0, 1);
        return panel;
    }

    private Control BuildMetricLayoutEditor()
    {
        TableLayoutPanel editor = new TableLayoutPanel();
        editor.Dock = DockStyle.Fill;
        editor.BackColor = DarkTheme.Window;
        editor.ColumnCount = 1;
        editor.RowCount = 2;
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        this.availableMetricsPanel = new FlowLayoutPanel();
        this.availableMetricsPanel.Dock = DockStyle.Fill;
        this.availableMetricsPanel.BackColor = DarkTheme.Control;
        this.availableMetricsPanel.Padding = new Padding(10, 10, 10, 8);
        this.availableMetricsPanel.AllowDrop = true;
        this.availableMetricsPanel.DragEnter += OnMetricDragEnter;
        this.availableMetricsPanel.DragDrop += delegate
        {
            if (this.draggedSourceSlotIndex >= 0)
            {
                SetSlotMetric(this.draggedSourceSlotIndex, string.Empty);
                RefreshMetricLayoutEditor();
                this.owner.PreviewSettings(ReadControls());
            }
        };

        this.metricSlotsPanel = new TableLayoutPanel();
        this.metricSlotsPanel.Dock = DockStyle.Fill;
        this.metricSlotsPanel.BackColor = DarkTheme.Window;
        int slotColumns = 2;
        int slotRows = (WidgetSettings.DefaultMetricOrder.Length + slotColumns - 1) / slotColumns;
        this.metricSlotsPanel.ColumnCount = slotColumns;
        this.metricSlotsPanel.RowCount = slotRows;
        for (int row = 0; row < slotRows; row++)
        {
            this.metricSlotsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0f / slotRows));
        }

        for (int column = 0; column < slotColumns; column++)
        {
            this.metricSlotsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f / slotColumns));
        }

        for (int i = 0; i < WidgetSettings.DefaultMetricOrder.Length; i++)
        {
            Panel slot = BuildMetricSlot(i);
            this.metricSlotPanels[i] = slot;
            this.metricSlotsPanel.Controls.Add(slot, i % slotColumns, i / slotColumns);
        }

        editor.Controls.Add(this.availableMetricsPanel, 0, 0);
        editor.Controls.Add(this.metricSlotsPanel, 0, 1);
        return editor;
    }

    private Label BuildMetricChip(string metricId)
    {
        Label chip = new Label();
        chip.Text = WidgetSettings.MetricDisplayName(metricId);
        chip.Tag = metricId;
        chip.Width = 108;
        chip.Height = 34;
        chip.Margin = new Padding(0, 0, 8, 6);
        chip.TextAlign = ContentAlignment.MiddleCenter;
        chip.BackColor = Color.FromArgb(58, 58, 62);
        chip.ForeColor = DarkTheme.Text;
        chip.BorderStyle = BorderStyle.FixedSingle;
        chip.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        chip.Cursor = Cursors.Hand;
        chip.MouseDown += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                BeginMetricDrag(metricId, -1, chip);
            }
        };
        return chip;
    }

    private Panel BuildMetricSlot(int index)
    {
        Panel slot = new Panel();
        slot.Dock = DockStyle.Fill;
        slot.Margin = new Padding(0, 6, index % 2 == 0 ? 10 : 0, 0);
        slot.BackColor = DarkTheme.Control;
        slot.BorderStyle = BorderStyle.FixedSingle;
        slot.AllowDrop = true;
        slot.Tag = string.Empty;
        slot.DragEnter += OnMetricDragEnter;
        slot.DragDrop += delegate { DropMetricToSlot(index); };
        slot.MouseDown += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                string metricId = GetSlotMetric(index);
                if (metricId.Length > 0)
                {
                    BeginMetricDrag(metricId, index, slot);
                }
            }
        };

        Label number = new Label();
        number.Name = "slotNumber";
        number.Text = (index + 1).ToString();
        number.AutoSize = true;
        number.Location = new Point(14, 12);
        number.TextAlign = ContentAlignment.TopLeft;
        number.ForeColor = DarkTheme.SubtleText;
        number.BackColor = Color.Transparent;
        number.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        number.UseCompatibleTextRendering = true;

        Label content = new Label();
        content.Name = "slotContent";
        content.AutoSize = false;
        content.TextAlign = ContentAlignment.MiddleCenter;
        content.ForeColor = DarkTheme.Text;
        content.BackColor = Color.Transparent;
        content.Font = new Font("Segoe UI", 10.0f, FontStyle.Bold);
        content.MouseDown += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                string metricId = GetSlotMetric(index);
                if (metricId.Length > 0)
                {
                    BeginMetricDrag(metricId, index, content);
                }
            }
        };

        Button remove = new Button();
        remove.Name = "slotRemove";
        remove.Text = "x";
        remove.Width = 24;
        remove.Height = 22;
        remove.Visible = false;
        remove.FlatStyle = FlatStyle.Flat;
        remove.FlatAppearance.BorderColor = DarkTheme.Border;
        remove.FlatAppearance.BorderSize = 1;
        remove.BackColor = Color.FromArgb(54, 54, 58);
        remove.ForeColor = DarkTheme.Text;
        remove.Font = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        remove.Click += delegate
        {
            SetSlotMetric(index, string.Empty);
            RefreshMetricLayoutEditor();
            this.owner.PreviewSettings(ReadControls());
        };

        slot.Controls.Add(number);
        slot.Controls.Add(content);
        slot.Controls.Add(remove);
        slot.Resize += delegate
        {
            content.Location = new Point(30, 8);
            content.Size = new Size(Math.Max(10, slot.ClientSize.Width - 60), Math.Max(20, slot.ClientSize.Height - 22));
            remove.Location = new Point(Math.Max(0, slot.ClientSize.Width - remove.Width - 7), Math.Max(0, slot.ClientSize.Height - remove.Height - 7));
        };

        return slot;
    }

    private void AddSliderRow(TableLayoutPanel root, int row, string labelText, NumericUpDown editor, TrackBar slider)
    {
        AddLabel(root, row, labelText);
        root.Controls.Add(editor, 1, row);
        root.Controls.Add(slider, 2, row);
    }

    private void AddEditorRow(TableLayoutPanel root, int row, string labelText, Control editor)
    {
        AddLabel(root, row, labelText);
        root.Controls.Add(editor, 1, row);
        root.SetColumnSpan(editor, 2);
    }

    private Control BuildButtonEditor(Button button)
    {
        Panel panel = new Panel();
        panel.Dock = DockStyle.Fill;
        panel.BackColor = DarkTheme.Window;
        button.Anchor = AnchorStyles.Left;
        button.Location = new Point(0, 8);
        panel.Controls.Add(button);
        panel.Resize += delegate
        {
            button.Location = new Point(0, Math.Max(0, (panel.ClientSize.Height - button.Height) / 2));
        };
        return panel;
    }

    private void AddLabel(TableLayoutPanel root, int row, string labelText)
    {
        Label label = new Label();
        label.Text = labelText;
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        label.UseCompatibleTextRendering = true;
        label.ForeColor = DarkTheme.SubtleText;
        label.BackColor = DarkTheme.Window;
        root.Controls.Add(label, 0, row);
    }

    private void WirePair(NumericUpDown box, TrackBar slider)
    {
        box.ValueChanged += delegate
        {
            if (this.initializing)
            {
                return;
            }

            int value = (int)box.Value;
            if (slider.Value != value)
            {
                slider.Value = value;
            }
        };

        slider.ValueChanged += delegate
        {
            if (this.initializing)
            {
                return;
            }

            if ((int)box.Value != slider.Value)
            {
                box.Value = slider.Value;
            }
        };
    }

    private void LoadControls(WidgetSettings settings)
    {
        this.initializing = true;
        try
        {
            this.widthBox.Value = settings.Width;
            this.heightBox.Value = settings.Height;
            UpdatePositionRanges(settings.Width, settings.Height);
            UpdateClockPositionRanges(settings.ClockWidth, settings.ClockHeight);
            this.leftXBox.Value = settings.LeftX;
            this.bottomYBox.Value = settings.BottomY;
            this.clockWidthBox.Value = settings.ClockWidth;
            this.clockHeightBox.Value = settings.ClockHeight;
            this.clockLeftXBox.Value = settings.ClockLeftX;
            this.clockBottomYBox.Value = settings.ClockBottomY;
            this.widthSlider.Value = settings.Width;
            this.heightSlider.Value = settings.Height;
            this.leftXSlider.Value = settings.LeftX;
            this.bottomYSlider.Value = settings.BottomY;
            this.clockWidthSlider.Value = settings.ClockWidth;
            this.clockHeightSlider.Value = settings.ClockHeight;
            this.clockLeftXSlider.Value = settings.ClockLeftX;
            this.clockBottomYSlider.Value = settings.ClockBottomY;
            this.backgroundTransparencyBox.Value = settings.BackgroundTransparencyPercent;
            this.backgroundTransparencySlider.Value = settings.BackgroundTransparencyPercent;
            this.applicationTransparencyBox.Value = settings.ApplicationTransparencyPercent;
            this.applicationTransparencySlider.Value = settings.ApplicationTransparencyPercent;
            this.clockTransparencyBox.Value = settings.BackgroundTransparencyPercent;
            this.clockTransparencySlider.Value = settings.BackgroundTransparencyPercent;
            this.dockEnabledCheck.Checked = settings.DockEnabled;
            this.dockIconSizeBox.Value = settings.DockIconSize;
            this.dockIconSizeSlider.Value = settings.DockIconSize;
            this.dockMagnificationBox.Value = settings.DockMagnificationPercent;
            this.dockMagnificationSlider.Value = settings.DockMagnificationPercent;
            this.dockBottomMarginBox.Value = settings.DockBottomMargin;
            this.dockBottomMarginSlider.Value = settings.DockBottomMargin;
            this.dockItemsBox.Text = settings.DockItemsText ?? string.Empty;
            SelectComboValue(this.visibilityCombo, settings.VisibilityMode);
            this.startupCheck.Checked = settings.StartupEnabled;
            this.powerSavingCheck.Checked = settings.PowerSavingEnabled;
            this.hoverOpacityCheck.Checked = settings.HoverOpacityEnabled;
            this.clockUse24HourCheck.Checked = settings.ClockUse24Hour;
            this.clockCalendarCheck.Checked = settings.ClockCalendarEnabled;
            this.clockPowerCheck.Checked = settings.ClockPowerEnabled;
            SelectComboValue(this.thermalTestCombo, settings.ThermalTestMode);
            SetAlertTestButtonState(settings.AlertTestEnabled);
            LoadMetricLayout(settings);
        }
        finally
        {
            this.initializing = false;
        }
    }

    private void OnSettingChanged(object sender, EventArgs e)
    {
        if (this.initializing)
        {
            return;
        }

        UpdatePositionRanges((int)this.widthBox.Value, (int)this.heightBox.Value);
        UpdateClockPositionRanges((int)this.clockWidthBox.Value, (int)this.clockHeightBox.Value);
        this.owner.PreviewSettings(ReadControls());
    }

    private void UpdatePositionRanges(int width, int height)
    {
        bool wasInitializing = this.initializing;
        this.initializing = true;
        try
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            int leftMin = bounds.Left;
            int leftMax = Math.Max(bounds.Left, bounds.Right - width);
            int bottomMin = Math.Min(bounds.Bottom - 1, bounds.Top + height - 1);
            int bottomMax = Math.Max(bounds.Top, bounds.Bottom - 1);

            SetNumericRange(this.leftXBox, leftMin, leftMax);
            SetTrackRange(this.leftXSlider, leftMin, leftMax);
            SetNumericRange(this.bottomYBox, bottomMin, bottomMax);
            SetTrackRange(this.bottomYSlider, bottomMin, bottomMax);
        }
        finally
        {
            this.initializing = wasInitializing;
        }
    }

    private void UpdateClockPositionRanges(int width, int height)
    {
        bool wasInitializing = this.initializing;
        this.initializing = true;
        try
        {
            Rectangle bounds = Screen.PrimaryScreen.Bounds;
            int leftMin = bounds.Left;
            int leftMax = Math.Max(bounds.Left, bounds.Right - width);
            int bottomMin = Math.Min(bounds.Bottom - 1, bounds.Top + height - 1);
            int bottomMax = Math.Max(bounds.Top, bounds.Bottom - 1);

            SetNumericRange(this.clockLeftXBox, leftMin, leftMax);
            SetTrackRange(this.clockLeftXSlider, leftMin, leftMax);
            SetNumericRange(this.clockBottomYBox, bottomMin, bottomMax);
            SetTrackRange(this.clockBottomYSlider, bottomMin, bottomMax);
        }
        finally
        {
            this.initializing = wasInitializing;
        }
    }

    private static void SetNumericRange(NumericUpDown box, int min, int max)
    {
        if (box.Value < min)
        {
            box.Value = min;
        }
        else if (box.Value > max)
        {
            box.Value = max;
        }

        box.Minimum = min;
        box.Maximum = max;
    }

    private static void SetTrackRange(TrackBar slider, int min, int max)
    {
        if (slider.Value < min)
        {
            slider.Value = min;
        }
        else if (slider.Value > max)
        {
            slider.Value = max;
        }

        slider.Minimum = min;
        slider.Maximum = max;
        slider.LargeChange = Math.Max(10, (max - min) / 20);
    }

    private WidgetSettings ReadControls()
    {
        WidgetSettings settings = this.baseline.Clone();
        settings.Width = (int)this.widthBox.Value;
        settings.Height = (int)this.heightBox.Value;
        settings.LeftX = (int)this.leftXBox.Value;
        settings.BottomY = (int)this.bottomYBox.Value;
        settings.BackgroundTransparencyPercent = (int)this.backgroundTransparencyBox.Value;
        settings.ApplicationTransparencyPercent = (int)this.applicationTransparencyBox.Value;
        settings.ClockWidth = (int)this.clockWidthBox.Value;
        settings.ClockHeight = (int)this.clockHeightBox.Value;
        settings.ClockLeftX = (int)this.clockLeftXBox.Value;
        settings.ClockBottomY = (int)this.clockBottomYBox.Value;
        settings.ClockTransparencyPercent = settings.BackgroundTransparencyPercent;
        settings.ClockUse24Hour = this.clockUse24HourCheck.Checked;
        settings.ClockCalendarEnabled = this.clockCalendarCheck.Checked;
        settings.ClockPowerEnabled = this.clockPowerCheck.Checked;
        settings.DockEnabled = this.dockEnabledCheck.Checked;
        settings.DockIconSize = (int)this.dockIconSizeBox.Value;
        settings.DockMagnificationPercent = (int)this.dockMagnificationBox.Value;
        settings.DockBottomMargin = (int)this.dockBottomMarginBox.Value;
        settings.DockItemsText = this.dockItemsBox.Text;
        settings.ThermalTestMode = (ThermalTestMode)GetComboValue(this.thermalTestCombo, ThermalTestMode.Off);
        settings.VisibilityMode = (WidgetVisibilityMode)GetComboValue(this.visibilityCombo, WidgetVisibilityMode.DesktopOnly);
        settings.StartupEnabled = this.startupCheck.Checked;
        settings.PowerSavingEnabled = this.powerSavingCheck.Checked;
        settings.HoverOpacityEnabled = this.hoverOpacityCheck.Checked;
        string[] selectedMetrics = ReadMetricSlots(false);
        settings.ShowCpu = ContainsMetricId(selectedMetrics, WidgetSettings.MetricCpu);
        settings.ShowMemory = ContainsMetricId(selectedMetrics, WidgetSettings.MetricMemory);
        settings.ShowDisk = ContainsMetricId(selectedMetrics, WidgetSettings.MetricDisk);
        settings.ShowNetwork = ContainsMetricId(selectedMetrics, WidgetSettings.MetricNetwork);
        settings.ShowGpu = ContainsMetricId(selectedMetrics, WidgetSettings.MetricGpu);
        settings.ShowNpu = ContainsMetricId(selectedMetrics, WidgetSettings.MetricNpu);
        settings.AlertTestEnabled = GetAlertTestButtonState();
        settings.MetricOrder = selectedMetrics;
        settings.Normalize();
        return settings;
    }

    private void LoadMetricLayout(WidgetSettings settings)
    {
        if (this.metricSlotPanels == null)
        {
            return;
        }

        for (int i = 0; i < this.metricSlotPanels.Length; i++)
        {
            SetSlotMetric(i, string.Empty);
        }

        string[] order = settings.MetricOrder ?? WidgetSettings.DefaultMetricOrder;
        int slotIndex = 0;
        for (int i = 0; i < order.Length && slotIndex < this.metricSlotPanels.Length; i++)
        {
            string metricId = order[i];
            if (!IsMetricShown(settings, metricId))
            {
                continue;
            }

            SetSlotMetric(slotIndex, metricId);
            slotIndex++;
        }

        RefreshMetricLayoutEditor();
    }

    private static bool IsMetricShown(WidgetSettings settings, string metricId)
    {
        if (string.Equals(metricId, WidgetSettings.MetricCpu, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowCpu;
        }

        if (string.Equals(metricId, WidgetSettings.MetricMemory, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowMemory;
        }

        if (string.Equals(metricId, WidgetSettings.MetricDisk, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowDisk;
        }

        if (string.Equals(metricId, WidgetSettings.MetricNetwork, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowNetwork;
        }

        if (string.Equals(metricId, WidgetSettings.MetricGpu, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowGpu;
        }

        if (string.Equals(metricId, WidgetSettings.MetricNpu, StringComparison.OrdinalIgnoreCase))
        {
            return settings.ShowNpu;
        }

        return false;
    }

    private void RefreshMetricLayoutEditor()
    {
        if (this.metricSlotPanels != null)
        {
            for (int i = 0; i < this.metricSlotPanels.Length; i++)
            {
                RefreshMetricSlot(i);
            }
        }

        if (this.availableMetricsPanel == null)
        {
            return;
        }

        this.availableMetricsPanel.Controls.Clear();
        for (int i = 0; i < WidgetSettings.DefaultMetricOrder.Length; i++)
        {
            string metricId = WidgetSettings.DefaultMetricOrder[i];
            if (!IsMetricInAnySlot(metricId))
            {
                this.availableMetricsPanel.Controls.Add(BuildMetricChip(metricId));
            }
        }
    }

    private void RefreshMetricSlot(int index)
    {
        Panel slot = this.metricSlotPanels[index];
        string metricId = GetSlotMetric(index);
        Label content = FindChildLabel(slot, "slotContent");
        Button remove = FindChildButton(slot, "slotRemove");
        if (content != null)
        {
            content.Text = metricId.Length > 0 ? WidgetSettings.MetricDisplayName(metricId) : string.Empty;
        }

        if (remove != null)
        {
            remove.Visible = metricId.Length > 0;
        }

        slot.BackColor = metricId.Length > 0 ? Color.FromArgb(58, 58, 62) : DarkTheme.Control;
    }

    private static Label FindChildLabel(Control parent, string name)
    {
        Control[] found = parent.Controls.Find(name, false);
        return found.Length > 0 ? found[0] as Label : null;
    }

    private static Button FindChildButton(Control parent, string name)
    {
        Control[] found = parent.Controls.Find(name, false);
        return found.Length > 0 ? found[0] as Button : null;
    }

    private string GetSlotMetric(int index)
    {
        if (this.metricSlotPanels == null || index < 0 || index >= this.metricSlotPanels.Length)
        {
            return string.Empty;
        }

        return this.metricSlotPanels[index].Tag as string ?? string.Empty;
    }

    private void SetSlotMetric(int index, string metricId)
    {
        if (this.metricSlotPanels == null || index < 0 || index >= this.metricSlotPanels.Length)
        {
            return;
        }

        this.metricSlotPanels[index].Tag = metricId ?? string.Empty;
    }

    private bool IsMetricInAnySlot(string metricId)
    {
        if (this.metricSlotPanels == null)
        {
            return false;
        }

        for (int i = 0; i < this.metricSlotPanels.Length; i++)
        {
            if (string.Equals(GetSlotMetric(i), metricId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private int FindMetricSlot(string metricId)
    {
        if (this.metricSlotPanels == null)
        {
            return -1;
        }

        for (int i = 0; i < this.metricSlotPanels.Length; i++)
        {
            if (string.Equals(GetSlotMetric(i), metricId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private string[] ReadMetricSlots(bool includeEmpty)
    {
        List<string> selected = new List<string>();
        if (this.metricSlotPanels != null)
        {
            for (int i = 0; i < this.metricSlotPanels.Length; i++)
            {
                string metricId = GetSlotMetric(i);
                if (metricId.Length > 0 || includeEmpty)
                {
                    selected.Add(metricId);
                }
            }
        }

        return selected.ToArray();
    }

    private static bool ContainsMetricId(string[] metrics, string metricId)
    {
        for (int i = 0; i < metrics.Length; i++)
        {
            if (string.Equals(metrics[i], metricId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void BeginMetricDrag(string metricId, int sourceSlotIndex, Control source)
    {
        if (string.IsNullOrEmpty(metricId))
        {
            return;
        }

        this.draggedMetricId = metricId;
        this.draggedSourceSlotIndex = sourceSlotIndex;
        source.DoDragDrop(metricId, DragDropEffects.Move);
        this.draggedMetricId = null;
        this.draggedSourceSlotIndex = -1;
    }

    private void OnMetricDragEnter(object sender, DragEventArgs e)
    {
        e.Effect = string.IsNullOrEmpty(this.draggedMetricId) ? DragDropEffects.None : DragDropEffects.Move;
    }

    private void DropMetricToSlot(int targetSlotIndex)
    {
        if (string.IsNullOrEmpty(this.draggedMetricId))
        {
            return;
        }

        int existingSlot = FindMetricSlot(this.draggedMetricId);
        if (existingSlot >= 0 && existingSlot != this.draggedSourceSlotIndex)
        {
            SetSlotMetric(existingSlot, string.Empty);
        }

        string targetMetric = GetSlotMetric(targetSlotIndex);
        if (this.draggedSourceSlotIndex >= 0 && this.draggedSourceSlotIndex != targetSlotIndex)
        {
            SetSlotMetric(this.draggedSourceSlotIndex, targetMetric);
        }

        SetSlotMetric(targetSlotIndex, this.draggedMetricId);
        RefreshMetricLayoutEditor();
        this.owner.PreviewSettings(ReadControls());
    }

    private bool GetAlertTestButtonState()
    {
        return this.alertTestButton != null &&
            this.alertTestButton.Tag is bool &&
            (bool)this.alertTestButton.Tag;
    }

    private void SetAlertTestButtonState(bool enabled)
    {
        if (this.alertTestButton == null)
        {
            return;
        }

        this.alertTestButton.Tag = enabled;
        this.alertTestButton.Text = enabled ? "开启" : "关闭";
        this.alertTestButton.BackColor = enabled ? Color.FromArgb(255, 92, 104) : DarkTheme.Control;
        this.alertTestButton.ForeColor = enabled ? Color.White : DarkTheme.Text;
        this.alertTestButton.FlatAppearance.BorderColor = enabled ? Color.FromArgb(255, 132, 142) : DarkTheme.Border;
    }

    private static void SelectComboValue(ComboBox combo, object value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            ComboOption option = combo.Items[i] as ComboOption;
            if (option != null && object.Equals(option.Value, value))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private static object GetComboValue(ComboBox combo, object defaultValue)
    {
        ComboOption option = combo.SelectedItem as ComboOption;
        if (option == null)
        {
            return defaultValue;
        }

        return option.Value;
    }

    private sealed class ComboOption
    {
        public ComboOption(string text, object value)
        {
            this.Text = text;
            this.Value = value;
        }

        public string Text { get; private set; }
        public object Value { get; private set; }

        public override string ToString()
        {
            return this.Text;
        }
    }

    private static void StyleButton(Button button, bool primary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = primary ? DarkTheme.Accent : DarkTheme.Border;
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = primary ? DarkTheme.Accent : DarkTheme.Control;
        button.ForeColor = primary ? Color.Black : DarkTheme.Text;
        button.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        button.UseCompatibleTextRendering = true;
        button.Margin = new Padding(8, 0, 0, 0);
    }

    private static class DarkTheme
    {
        public static readonly Color Window = Color.FromArgb(32, 32, 32);
        public static readonly Color Control = Color.FromArgb(45, 45, 48);
        public static readonly Color Border = Color.FromArgb(72, 72, 78);
        public static readonly Color Text = Color.FromArgb(245, 245, 245);
        public static readonly Color SubtleText = Color.FromArgb(210, 210, 210);
        public static readonly Color Accent = Color.FromArgb(82, 211, 255);
    }
}
