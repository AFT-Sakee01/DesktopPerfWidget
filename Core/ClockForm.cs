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

internal sealed class ClockForm : Form
{
    private readonly System.Windows.Forms.Timer timer;
    private readonly System.Windows.Forms.Timer hoverTimer;
    private readonly Dictionary<string, DateTime> thermalCriticalSinceUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    private WidgetSettings currentSettings;
    private float scale;
    private bool hiddenForFullscreen;
    private bool layeredUpdateFailureLogged;
    private PowerReading cachedPowerReading;
    private DateTime cachedPowerReadingUtc;
    private List<ThermalReading> cachedThermalReadings = new List<ThermalReading>();
    private DateTime cachedThermalReadingsUtc;
    private int renderTickCount;
    private double hoverOpacityProgress;
    private DateTime hoverOpacityLastUtc;

    private struct PowerReading
    {
        public bool StatusKnown;
        public bool IsCharging;
        public bool WattsKnown;
        public double Watts;
    }

    private sealed class ThermalReading
    {
        public string Name { get; set; }
        public double Celsius { get; set; }
        public bool CriticalActive { get; set; }
    }

    public ClockForm(WidgetSettings settings)
    {
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();

        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        using (Graphics g = this.CreateGraphics())
        {
            this.scale = Math.Max(1.0f, g.DpiX / 96.0f);
        }

        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.FromArgb(18, 19, 22);
        this.MinimumSize = new Size(WidgetSettings.MinClockWidth, WidgetSettings.MinClockHeight);
        this.MaximumSize = new Size(WidgetSettings.MaxClockWidth, WidgetSettings.MaxClockHeight + S(32));
        this.Size = new Size(this.currentSettings.ClockWidth, this.currentSettings.ClockHeight);

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = 250;
        this.timer.Tick += OnTimerTick;
        this.hoverTimer = new System.Windows.Forms.Timer();
        this.hoverTimer.Interval = 30;
        this.hoverTimer.Tick += OnHoverTimerTick;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyRuntimeSettings(this.currentSettings);
        this.timer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
        this.hoverTimer.Stop();
        this.hoverTimer.Tick -= OnHoverTimerTick;
        this.hoverTimer.Dispose();
        base.OnFormClosed(e);
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        this.renderTickCount++;
        Size desiredSize = GetDesiredClockSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
            PositionClock();
        }

        RenderLayeredWindow();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), S(12)))
        {
            this.Region = new Region(path);
        }

        RenderLayeredWindow();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_SETTINGCHANGE = 0x001A;

        base.WndProc(ref m);

        if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_SETTINGCHANGE)
        {
            PositionClock();
        }
    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        ThermalTestMode oldThermalTestMode = this.currentSettings.ThermalTestMode;
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        if (oldThermalTestMode != this.currentSettings.ThermalTestMode)
        {
            this.thermalCriticalSinceUtc.Clear();
            this.cachedThermalReadingsUtc = DateTime.MinValue;
        }

        Size desiredSize = GetDesiredClockSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        bool shouldBeTopMost = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }

        ApplyClickThroughStyle();
        UpdateHoverAnimationTimer();
        NativeMethods.SetWindowPos(
            this.Handle,
            shouldBeTopMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE);

        PositionClock();
        RenderLayeredWindow();
    }

    private void OnHoverTimerTick(object sender, EventArgs e)
    {
        if (UpdateHoverOpacityAnimation())
        {
            RenderLayeredWindow();
        }
    }

    private void UpdateHoverAnimationTimer()
    {
        if (this.currentSettings.HoverOpacityEnabled)
        {
            if (!this.hoverTimer.Enabled)
            {
                this.hoverOpacityLastUtc = DateTime.UtcNow;
                this.hoverTimer.Start();
            }

            return;
        }

        if (this.hoverTimer.Enabled)
        {
            this.hoverTimer.Stop();
        }

        if (this.hoverOpacityProgress > 0.0)
        {
            this.hoverOpacityProgress = 0.0;
            RenderLayeredWindow();
        }
    }

    private bool UpdateHoverOpacityAnimation()
    {
        DateTime now = DateTime.UtcNow;
        double elapsed = this.hoverOpacityLastUtc == DateTime.MinValue ? 0.03 : (now - this.hoverOpacityLastUtc).TotalSeconds;
        this.hoverOpacityLastUtc = now;

        bool hovered =
            this.currentSettings.HoverOpacityEnabled &&
            !this.hiddenForFullscreen &&
            this.Visible &&
            this.Bounds.Contains(Cursor.Position);

        double target = hovered ? 1.0 : 0.0;
        double old = this.hoverOpacityProgress;
        double step = Math.Max(0.0, Math.Min(1.0, elapsed / 0.15));
        if (this.hoverOpacityProgress < target)
        {
            this.hoverOpacityProgress = Math.Min(target, this.hoverOpacityProgress + step);
        }
        else if (this.hoverOpacityProgress > target)
        {
            this.hoverOpacityProgress = Math.Max(target, this.hoverOpacityProgress - step);
        }

        return Math.Abs(old - this.hoverOpacityProgress) > 0.001;
    }

    public void SetHiddenForFullscreen(bool hidden)
    {
        this.hiddenForFullscreen = hidden;
        if (hidden)
        {
            if (this.Visible)
            {
                this.Hide();
            }

            return;
        }

        if (!this.Visible)
        {
            this.Show();
        }

        PositionClock();
        RenderLayeredWindow();
    }

    private void PositionClock()
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        Size desiredSize = GetDesiredClockSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        int left = Math.Max(workArea.Left, Math.Min(this.currentSettings.ClockLeftX, workArea.Right - this.Width));
        int baseHeight = Math.Max(WidgetSettings.MinClockHeight, this.currentSettings.ClockHeight);
        int top = this.currentSettings.ClockBottomY - baseHeight + 1;
        top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - baseHeight));
        this.Location = new Point(left, top);

        NativeMethods.SetWindowPos(
            this.Handle,
            this.currentSettings.VisibilityMode == WidgetVisibilityMode.DesktopOnly ? NativeMethods.HWND_TOP : NativeMethods.HWND_TOPMOST,
            left,
            top,
            this.Width,
            this.Height,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_SHOWWINDOW);
    }

    private Size GetDesiredClockSize()
    {
        int extraHeight = GetThermalAlerts().Count > 0 ? GetThermalAlertExtraHeight() : 0;
        return new Size(this.currentSettings.ClockWidth, this.currentSettings.ClockHeight + extraHeight);
    }

    private int GetThermalAlertExtraHeight()
    {
        return Math.Max(S(24), Math.Min(S(32), (int)Math.Round(this.currentSettings.ClockHeight * 0.42f)));
    }

    private void ApplyClickThroughStyle()
    {
        if (!this.IsHandleCreated)
        {
            return;
        }

        bool clickThrough = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        int exStyle = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE);
        int desired = clickThrough ?
            (exStyle | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED) :
            ((exStyle & ~NativeMethods.WS_EX_TRANSPARENT) | NativeMethods.WS_EX_LAYERED);

        if (desired == exStyle)
        {
            return;
        }

        NativeMethods.SetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE, desired);
        NativeMethods.SetWindowPos(
            this.Handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_FRAMECHANGED);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawClock(e.Graphics);
    }

    private void DrawClock(Graphics g)
    {
        DrawClockBackground(g);
        DrawClockContentLayer(g);
    }

    private void ConfigureClockGraphics(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    private void DrawClockBackground(Graphics g)
    {
        ConfigureClockGraphics(g);

        int alpha = GetBackgroundOpacityAlpha();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(12)))
        using (SolidBrush background = new SolidBrush(Color.FromArgb(alpha, 18, 19, 22)))
        {
            g.FillPath(background, shell);
        }
    }

    private void DrawClockContentLayer(Graphics g)
    {
        int contentAlpha = GetContentOpacityAlpha();
        if (contentAlpha <= 0)
        {
            return;
        }

        if (contentAlpha >= 255)
        {
            DrawClockContent(g);
            return;
        }

        using (Bitmap contentBitmap = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppPArgb))
        using (Graphics contentGraphics = Graphics.FromImage(contentBitmap))
        {
            contentGraphics.Clear(Color.Transparent);
            DrawClockContent(contentGraphics);
            DrawingUtil.DrawImageWithAlpha(g, contentBitmap, contentAlpha);
        }
    }

    private void DrawClockContent(Graphics g)
    {
        ConfigureClockGraphics(g);

        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(12)))
        using (Pen outline = new Pen(Color.FromArgb(90, 255, 255, 255), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        string formatText = this.currentSettings.ClockUse24Hour ? "HH:mm:ss" : "h:mm:ss tt";
        string timeText = DateTime.Now.ToString(formatText);
        List<ThermalReading> thermalAlerts = GetThermalAlerts();
        float baseHeight = Math.Min(this.currentSettings.ClockHeight, this.Height);
        float thermalReserve = thermalAlerts.Count > 0 ? Math.Max(S(18), GetThermalAlertExtraHeight() - S(6)) : 0.0f;
        RectangleF textRect = new RectangleF(
            S(12),
            S(5),
            Math.Max(10, this.Width - S(24)),
            Math.Max(10, baseHeight - S(10)));

        if (this.currentSettings.ClockCalendarEnabled || this.currentSettings.ClockPowerEnabled)
        {
            DrawClockWithInfo(g, timeText, textRect);
        }
        else
        {
            float fontSize = Math.Max(14.0f, Math.Min(textRect.Height * 0.74f, this.Width * 0.16f));

            using (Font baseFont = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(244, 248, 250)))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;

                Font drawFont = baseFont;
                bool disposeFont = false;
                float size = baseFont.Size;
                while (size > 10.0f * this.scale && g.MeasureString(timeText, drawFont).Width > textRect.Width)
                {
                    if (disposeFont)
                    {
                        drawFont.Dispose();
                    }

                    size -= 1.0f * this.scale;
                    drawFont = new Font(baseFont.FontFamily, size, baseFont.Style, GraphicsUnit.Pixel);
                    disposeFont = true;
                }

                g.DrawString(timeText, drawFont, brush, textRect, format);

                if (disposeFont)
                {
                    drawFont.Dispose();
                }
            }
        }

        if (thermalAlerts.Count > 0)
        {
            RectangleF thermalRect = new RectangleF(
                S(12),
                baseHeight + S(3),
                Math.Max(10, this.Width - S(24)),
                thermalReserve);
            DrawThermalAlerts(g, thermalRect, thermalAlerts);
        }
    }

    private void DrawClockWithInfo(Graphics g, string timeText, RectangleF bounds)
    {
        DateTime now = DateTime.Now;
        bool showCalendar = this.currentSettings.ClockCalendarEnabled;
        bool showPower = this.currentSettings.ClockPowerEnabled;
        float gap = S(6);
        float rightWidth = showCalendar ? Math.Min(bounds.Width * 0.30f, Math.Max(S(96), bounds.Width * 0.23f)) : 0;
        float powerWidth = showPower ? Math.Min(bounds.Width * 0.24f, Math.Max(S(74), bounds.Width * 0.18f)) : 0;
        float usedGap = 0;
        if (showPower)
        {
            usedGap += gap;
        }

        if (showCalendar)
        {
            usedGap += gap;
        }

        float timeWidth = Math.Max(10, bounds.Width - rightWidth - powerWidth - usedGap);
        RectangleF timeRect = new RectangleF(bounds.Left, bounds.Top, timeWidth, bounds.Height);
        float x = timeRect.Right + (showPower ? gap : 0);
        RectangleF powerRect = showPower ? new RectangleF(x, bounds.Top, powerWidth, bounds.Height) : RectangleF.Empty;
        x = showPower ? powerRect.Right + (showCalendar ? gap : 0) : x;
        RectangleF dateRect = showCalendar ? new RectangleF(x, bounds.Top, rightWidth, bounds.Height * 0.50f) : RectangleF.Empty;
        RectangleF dayRect = showCalendar ? new RectangleF(x, bounds.Top + bounds.Height * 0.50f, rightWidth, bounds.Height * 0.50f) : RectangleF.Empty;

        string dateText = FormatClockDate(now);
        string dayText = FormatClockWeekday(now);
        float timeFontSize = Math.Max(14.0f, Math.Min(bounds.Height * 0.66f, timeRect.Width * 0.25f));
        float detailFontSize = Math.Max(9.0f, Math.Min(bounds.Height * 0.28f, rightWidth * 0.19f));
        float powerLabelFontSize = Math.Max(8.5f, Math.Min(bounds.Height * 0.22f, powerWidth * 0.20f));
        float powerValueFontSize = Math.Max(9.0f, Math.Min(bounds.Height * 0.26f, powerWidth * 0.22f));

        using (Font timeFont = new Font("Segoe UI", timeFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font detailFont = new Font("Segoe UI", detailFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font powerLabelFont = new Font("Segoe UI", powerLabelFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
        using (Font powerValueFont = new Font("Segoe UI", powerValueFontSize, FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush timeBrush = new SolidBrush(Color.FromArgb(244, 248, 250)))
        using (SolidBrush detailBrush = new SolidBrush(Color.FromArgb(224, 235, 241)))
        {
            DrawClockFittedText(g, timeText, timeFont, timeBrush, timeRect, StringAlignment.Near);

            if (showPower)
            {
                DrawClockPower(g, powerRect, powerLabelFont, powerValueFont);
            }

            if (showCalendar)
            {
                DrawClockFittedText(g, dateText, detailFont, detailBrush, dateRect, StringAlignment.Far);
                DrawClockFittedText(g, dayText, detailFont, detailBrush, dayRect, StringAlignment.Far);
            }
        }
    }

    private void DrawClockPower(Graphics g, RectangleF bounds, Font labelFont, Font valueFont)
    {
        PowerReading reading = GetPowerReading();
        bool charging = reading.StatusKnown && reading.IsCharging;
        string labelText = charging ? "Charging" : "Power";
        string valueText = reading.WattsKnown ? FormatWatts(reading.Watts) : "-- W";
        Color accent = charging ? Color.FromArgb(170, 238, 190) : Color.FromArgb(255, 178, 186);
        RectangleF labelRect = new RectangleF(bounds.Left, bounds.Top, bounds.Width, bounds.Height * 0.48f);
        RectangleF valueRect = new RectangleF(bounds.Left, bounds.Top + bounds.Height * 0.45f, bounds.Width, bounds.Height * 0.55f);

        using (SolidBrush brush = new SolidBrush(accent))
        {
            DrawClockFittedText(g, labelText, labelFont, brush, labelRect, StringAlignment.Center);
            DrawClockFittedText(g, valueText, valueFont, brush, valueRect, StringAlignment.Center);
        }
    }

    private void DrawThermalAlerts(Graphics g, RectangleF bounds, List<ThermalReading> alerts)
    {
        if (alerts == null || alerts.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        int total = alerts.Count;
        int visibleSensors = Math.Min(3, total);
        bool hasMore = total > 3;
        if (visibleSensors <= 0)
        {
            return;
        }

        float gap = S(6);
        float chipHeight = Math.Max(S(16), bounds.Height - S(2));
        float chipTop = bounds.Top + Math.Max(0.0f, (bounds.Height - chipHeight) / 2.0f);

        using (Font chipFont = new Font("Segoe UI", Math.Max(8.0f, 9.5f * this.scale), FontStyle.Bold, GraphicsUnit.Pixel))
        {
            float moreWidth = 0.0f;
            if (hasMore)
            {
                string moreText = "+" + (total - visibleSensors).ToString();
                moreWidth = Math.Max(S(30), g.MeasureString(moreText, chipFont).Width + S(18));
                moreWidth = Math.Min(moreWidth, bounds.Width * 0.28f);
                RectangleF moreRect = new RectangleF(bounds.Right - moreWidth, chipTop, moreWidth, chipHeight);
                double hiddenMaxTemp = 0.0;
                for (int i = visibleSensors; i < total; i++)
                {
                    hiddenMaxTemp = Math.Max(hiddenMaxTemp, alerts[i].Celsius);
                }

                DrawThermalChip(g, moreRect, moreText, hiddenMaxTemp, false, chipFont);
            }

            float sensorAreaRight = hasMore ? bounds.Right - moreWidth - gap : bounds.Right;
            float sensorAreaWidth = Math.Max(S(30), sensorAreaRight - bounds.Left);
            float slotWidth = Math.Max(S(30), (sensorAreaWidth - gap * 2.0f) / 3.0f);
            float x = bounds.Left;
            for (int i = 0; i < visibleSensors; i++)
            {
                string text = FormatThermalSensorName(alerts[i].Name);
                float desiredWidth = g.MeasureString(text, chipFont).Width + S(alerts[i].CriticalActive ? 32 : 20);
                float width = Math.Min(slotWidth, Math.Max(S(30), desiredWidth));
                RectangleF chipRect = new RectangleF(x, chipTop, width, chipHeight);
                DrawThermalChip(g, chipRect, text, alerts[i].Celsius, alerts[i].CriticalActive, chipFont);
                x += slotWidth + gap;
            }
        }
    }

    private void DrawThermalChip(Graphics g, RectangleF rect, string text, double celsius, bool criticalActive, Font font)
    {
        float radius = Math.Min(rect.Height / 2.0f, S(11));
        int redAlpha = GetThermalRedAlpha(celsius);
        using (GraphicsPath path = RoundedRectangle(rect, radius))
        using (SolidBrush baseBrush = new SolidBrush(Color.FromArgb(160, 28, 28, 30)))
        using (SolidBrush redBrush = new SolidBrush(Color.FromArgb(redAlpha, 255, 44, 58)))
        using (Pen border = new Pen(Color.FromArgb(45, 255, 255, 255), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(baseBrush, path);
            g.FillPath(redBrush, path);
            g.DrawPath(border, path);
        }

        RectangleF textRect = rect;
        if (criticalActive)
        {
            float iconSize = Math.Max(S(12), Math.Min(rect.Height * 0.70f, S(17)));
            RectangleF iconRect = new RectangleF(rect.Right - iconSize - S(7), rect.Top + (rect.Height - iconSize) / 2.0f, iconSize, iconSize);
            DrawSmallWarningIcon(g, iconRect);
            textRect = new RectangleF(rect.Left + S(8), rect.Top, Math.Max(4, rect.Width - iconSize - S(18)), rect.Height);
        }
        else
        {
            textRect = new RectangleF(rect.Left + S(8), rect.Top, Math.Max(4, rect.Width - S(16)), rect.Height);
        }

        using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(246, 246, 246)))
        {
            DrawClockFittedText(g, text, font, textBrush, textRect, StringAlignment.Near);
        }
    }

    private void DrawSmallWarningIcon(Graphics g, RectangleF rect)
    {
        int warningAlpha = (this.renderTickCount % 2 == 0) ? 77 : 179;
        float centerX = rect.Left + rect.Width / 2.0f;
        float centerY = rect.Top + rect.Height / 2.0f;
        float size = Math.Min(rect.Width, rect.Height);
        PointF[] triangle = new PointF[]
        {
            new PointF(centerX, centerY - size * 0.46f),
            new PointF(centerX - size * 0.48f, centerY + size * 0.42f),
            new PointF(centerX + size * 0.48f, centerY + size * 0.42f)
        };

        using (Pen pen = new Pen(Color.FromArgb(warningAlpha, 255, 207, 82), Math.Max(1.0f, 2.0f * this.scale)))
        {
            pen.LineJoin = LineJoin.Round;
            g.DrawPolygon(pen, triangle);
        }

        using (Font markFont = new Font("Segoe UI", Math.Max(7.0f, size * 0.66f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush markBrush = new SolidBrush(Color.FromArgb(warningAlpha, 255, 207, 82)))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            g.DrawString("!", markFont, markBrush, rect, format);
        }
    }

    private static int GetThermalRedAlpha(double celsius)
    {
        double progress = (celsius - 70.0) / 30.0;
        if (progress < 0.0)
        {
            progress = 0.0;
        }
        else if (progress > 1.0)
        {
            progress = 1.0;
        }

        double alpha = 0.30 + progress * (0.85 - 0.30);
        return (int)Math.Round(alpha * 255.0);
    }

    private static string FormatThermalSensorName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "TZ";
        }

        return name.Trim();
    }

    private void DrawClockFittedText(Graphics g, string text, Font baseFont, Brush brush, RectangleF rect, StringAlignment alignment)
    {
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = alignment;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;

            Font drawFont = baseFont;
            bool disposeFont = false;
            float size = baseFont.Size;
            while (size > 8.0f * this.scale && g.MeasureString(text, drawFont).Width > rect.Width)
            {
                if (disposeFont)
                {
                    drawFont.Dispose();
                }

                size -= 0.8f * this.scale;
                drawFont = new Font(baseFont.FontFamily, size, baseFont.Style, GraphicsUnit.Pixel);
                disposeFont = true;
            }

            g.DrawString(text, drawFont, brush, rect, format);

            if (disposeFont)
            {
                drawFont.Dispose();
            }
        }
    }

    private static string FormatClockDate(DateTime value)
    {
        string[] months = new string[]
        {
            "Jan.", "Feb.", "Mar.", "Apr.", "May.", "Jun.",
            "Jul.", "Aug.", "Sep.", "Oct.", "Nov.", "Dec."
        };

        return months[value.Month - 1] + value.Day.ToString() + GetOrdinalSuffix(value.Day);
    }

    private static string FormatClockWeekday(DateTime value)
    {
        string[] days = new string[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
        return days[(int)value.DayOfWeek];
    }

    private PowerReading GetPowerReading()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - this.cachedPowerReadingUtc).TotalSeconds < 2.0)
        {
            return this.cachedPowerReading;
        }

        this.cachedPowerReading = ReadPowerReading();
        this.cachedPowerReadingUtc = now;
        return this.cachedPowerReading;
    }

    private List<ThermalReading> GetThermalAlerts()
    {
        DateTime now = DateTime.UtcNow;
        if (this.currentSettings.ThermalTestMode != ThermalTestMode.Off)
        {
            List<ThermalReading> simulated = BuildSimulatedThermalReadings(this.currentSettings.ThermalTestMode);
            UpdateThermalCriticalStates(simulated, now, true);
            simulated.Sort(CompareThermalReading);
            return simulated;
        }

        if ((now - this.cachedThermalReadingsUtc).TotalSeconds >= 2.0)
        {
            this.cachedThermalReadings = ReadThermalReadings();
            if (this.cachedThermalReadings == null)
            {
                this.cachedThermalReadings = new List<ThermalReading>();
            }

            this.cachedThermalReadingsUtc = now;
            UpdateThermalCriticalStates(this.cachedThermalReadings, now, false);
        }

        List<ThermalReading> alerts = new List<ThermalReading>();
        for (int i = 0; i < this.cachedThermalReadings.Count; i++)
        {
            if (this.cachedThermalReadings[i].Celsius >= 70.0)
            {
                alerts.Add(this.cachedThermalReadings[i]);
            }
        }

        alerts.Sort(CompareThermalReading);
        return alerts;
    }

    private void UpdateThermalCriticalStates(List<ThermalReading> readings, DateTime now, bool instantCritical)
    {
        if (readings == null)
        {
            return;
        }

        HashSet<string> activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < readings.Count; i++)
        {
            ThermalReading reading = readings[i];
            if (reading == null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(reading.Name))
            {
                continue;
            }

            activeNames.Add(reading.Name);
            if (reading.Celsius >= 95.0)
            {
                DateTime since;
                if (!this.thermalCriticalSinceUtc.TryGetValue(reading.Name, out since))
                {
                    since = instantCritical ? now.AddSeconds(-3.0) : now;
                    this.thermalCriticalSinceUtc[reading.Name] = since;
                }

                reading.CriticalActive = (now - since).TotalSeconds >= 3.0;
            }
            else
            {
                this.thermalCriticalSinceUtc.Remove(reading.Name);
                reading.CriticalActive = false;
            }
        }

        List<string> stale = new List<string>();
        foreach (string name in this.thermalCriticalSinceUtc.Keys)
        {
            if (!activeNames.Contains(name))
            {
                stale.Add(name);
            }
        }

        for (int i = 0; i < stale.Count; i++)
        {
            this.thermalCriticalSinceUtc.Remove(stale[i]);
        }
    }

    private static int CompareThermalReading(ThermalReading left, ThermalReading right)
    {
        int value = right.Celsius.CompareTo(left.Celsius);
        if (value != 0)
        {
            return value;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static List<ThermalReading> ReadThermalReadings()
    {
        List<ThermalReading> readings = new List<ThermalReading>();
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\cimv2", "SELECT Name, Temperature, HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    string name = Convert.ToString(GetManagementValue(item, "Name"));
                    double celsius = ConvertThermalZoneCelsius(
                        GetManagementValue(item, "Temperature"),
                        GetManagementValue(item, "HighPrecisionTemperature"));
                    if (string.IsNullOrEmpty(name) || celsius <= 0.0)
                    {
                        continue;
                    }

                    readings.Add(new ThermalReading
                    {
                        Name = name.Trim(),
                        Celsius = celsius,
                        CriticalActive = false
                    });
                }
            }
        }
        catch
        {
        }

        return readings;
    }

    private List<ThermalReading> BuildSimulatedThermalReadings(ThermalTestMode mode)
    {
        double celsius = mode == ThermalTestMode.Simulate100 ? 100.0 : 75.0;
        DateTime now = DateTime.UtcNow;
        if ((now - this.cachedThermalReadingsUtc).TotalSeconds >= 2.0 || this.cachedThermalReadings.Count == 0)
        {
            this.cachedThermalReadings = ReadThermalReadings();
            if (this.cachedThermalReadings == null)
            {
                this.cachedThermalReadings = new List<ThermalReading>();
            }

            this.cachedThermalReadingsUtc = now;
        }

        List<ThermalReading> readings = new List<ThermalReading>();
        HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < this.cachedThermalReadings.Count; i++)
        {
            string name = this.cachedThermalReadings[i].Name;
            if (string.IsNullOrEmpty(name) || !usedNames.Add(name))
            {
                continue;
            }

            readings.Add(new ThermalReading
            {
                Name = name,
                Celsius = celsius,
                CriticalActive = false
            });
        }

        if (readings.Count > 0)
        {
            return readings;
        }

        for (int i = 0; i < 6; i++)
        {
            readings.Add(new ThermalReading
            {
                Name = @"\_SB.TZ" + i.ToString(),
                Celsius = celsius,
                CriticalActive = false
            });
        }

        return readings;
    }

    private static double ConvertThermalZoneCelsius(object temperature, object highPrecisionTemperature)
    {
        double highPrecision = ToPositiveDouble(highPrecisionTemperature);
        if (highPrecision > 0.0)
        {
            return highPrecision / 10.0 - 273.15;
        }

        double standard = ToPositiveDouble(temperature);
        if (standard > 0.0)
        {
            return standard - 273.15;
        }

        return 0.0;
    }

    private static PowerReading ReadPowerReading()
    {
        PowerReading reading = new PowerReading();
        try
        {
            PowerLineStatus lineStatus = SystemInformation.PowerStatus.PowerLineStatus;
            if (lineStatus != PowerLineStatus.Unknown)
            {
                reading.StatusKnown = true;
                reading.IsCharging = lineStatus == PowerLineStatus.Online;
            }
        }
        catch
        {
        }

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryStatus"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    double chargeMilliwatts = ToPositiveMilliwatts(GetManagementValue(item, "ChargeRate"));
                    double dischargeMilliwatts = ToPositiveMilliwatts(GetManagementValue(item, "DischargeRate"));
                    object charging = GetManagementValue(item, "Charging");
                    object discharging = GetManagementValue(item, "Discharging");
                    object powerOnline = GetManagementValue(item, "PowerOnline");

                    if (chargeMilliwatts > 0)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = true;
                        reading.WattsKnown = true;
                        reading.Watts = chargeMilliwatts / 1000.0;
                        return reading;
                    }

                    if (dischargeMilliwatts > 0)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = false;
                        reading.WattsKnown = true;
                        reading.Watts = dischargeMilliwatts / 1000.0;
                        return reading;
                    }

                    if (charging != null)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = Convert.ToBoolean(charging);
                    }

                    if (discharging != null && Convert.ToBoolean(discharging))
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = false;
                    }

                    if (powerOnline != null)
                    {
                        reading.StatusKnown = true;
                        if (!Convert.ToBoolean(powerOnline))
                        {
                            reading.IsCharging = false;
                        }
                    }

                    return reading;
                }
            }
        }
        catch
        {
        }

        return reading;
    }

    private static object GetManagementValue(ManagementBaseObject item, string name)
    {
        try
        {
            PropertyData property = item.Properties[name];
            return property == null ? null : property.Value;
        }
        catch
        {
            return null;
        }
    }

    private static double ToPositiveDouble(object value)
    {
        if (value == null)
        {
            return 0.0;
        }

        try
        {
            double number = Convert.ToDouble(value);
            return number > 0.0 ? number : 0.0;
        }
        catch
        {
            return 0.0;
        }
    }

    private static double ToPositiveMilliwatts(object value)
    {
        if (value == null)
        {
            return 0;
        }

        try
        {
            double number = Convert.ToDouble(value);
            if (number <= 0 || number >= 4294967294.0)
            {
                return 0;
            }

            return number;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatWatts(double watts)
    {
        if (watts >= 100.0)
        {
            return watts.ToString("0") + " W";
        }

        return watts.ToString("0.0") + " W";
    }

    private static string GetOrdinalSuffix(int day)
    {
        int lastTwo = day % 100;
        if (lastTwo >= 11 && lastTwo <= 13)
        {
            return "th";
        }

        switch (day % 10)
        {
            case 1:
                return "st";
            case 2:
                return "nd";
            case 3:
                return "rd";
            default:
                return "th";
        }
    }

    private void RenderLayeredWindow()
    {
        if (!this.IsHandleCreated || this.Width <= 0 || this.Height <= 0)
        {
            return;
        }

        try
        {
            using (Bitmap bitmap = new Bitmap(this.Width, this.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                DrawClockBackground(g);
                DrawClockContentLayer(g);
                if (!NativeMethods.UpdateLayeredWindowFromBitmap(this.Handle, this.Location, bitmap, GetApplicationOpacityAlpha()))
                {
                    if (!this.layeredUpdateFailureLogged)
                    {
                        this.layeredUpdateFailureLogged = true;
                        Program.LogInfo("Clock UpdateLayeredWindow failed; falling back to normal paint.");
                    }

                    this.Invalidate();
                }
            }
        }
        catch (Exception ex)
        {
            if (!this.layeredUpdateFailureLogged)
            {
                this.layeredUpdateFailureLogged = true;
                Program.LogException(ex);
            }
        }
    }

    private int GetBackgroundOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.BackgroundTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private int GetContentOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.ApplicationTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private byte GetApplicationOpacityAlpha()
    {
        return (byte)ApplyHoverTransparencyTarget(255);
    }

    private int ApplyHoverTransparencyTarget(int alpha)
    {
        if (!this.currentSettings.HoverOpacityEnabled || this.hoverOpacityProgress <= 0.0)
        {
            return alpha;
        }

        int hoverAlpha = (int)Math.Round(255.0 * 0.05);
        if (alpha <= hoverAlpha)
        {
            return alpha;
        }

        double animated = alpha + (hoverAlpha - alpha) * this.hoverOpacityProgress;
        return Math.Max(0, Math.Min(255, (int)Math.Round(animated)));
    }

    private int S(int value)
    {
        return (int)Math.Round(value * this.scale);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = radius * 2.0f;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
