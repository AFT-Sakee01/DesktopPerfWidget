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

internal sealed class LaunchpadForm : Form
{
    private const double AnimationDurationMs = 180.0;
    private static readonly object DescriptorSync = new object();
    private static Task<List<LaunchpadAppDescriptor>> descriptorTask;
    private readonly System.Windows.Forms.Timer animationTimer;
    private readonly List<LaunchpadApp> apps;
    private readonly List<LaunchpadAppDescriptor> allDescriptors;
    private float scale;
    private DateTime animationStartedUtc;
    private bool panelAnimationActive;
    private bool hiding;
    private Rectangle targetBounds;
    private double scrollOffset;
    private int targetScrollOffset;
    private int maxScroll;
    private int hoveredIndex;
    private bool appsLoadingStarted;
    private bool descriptorsLoaded;
    private bool showAllPrograms;
    private bool allProgramsToggleHovered;
    private string launchpadItemsText;
    private string dockItemsText;
    private Bitmap backgroundBitmap;
    private Size backgroundBitmapSize;
    private int backgroundBitmapAppCount;
    private bool backgroundBitmapShowAllPrograms;
    private bool launchpadUnpinMode;
    private bool deleteButtonHovered;

    private sealed class LaunchpadApp : IDisposable
    {
        public string Name { get; set; }
        public string Command { get; set; }
        public string IconKey { get; set; }
        public bool IsLaunchpadPinned { get; set; }

        public void Dispose()
        {
        }
    }

    private sealed class LaunchpadAppDescriptor
    {
        public string Name { get; set; }
        public string Command { get; set; }
        public string IconPath { get; set; }
        public bool IsLaunchpadPinned { get; set; }
    }

    public LaunchpadForm()
    {
        this.apps = new List<LaunchpadApp>();
        this.allDescriptors = new List<LaunchpadAppDescriptor>();
        this.hoveredIndex = -1;
        this.backgroundBitmapAppCount = -1;
        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.FromArgb(22, 23, 28);
        this.KeyPreview = true;
        this.DoubleBuffered = true;
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

        this.animationTimer = new System.Windows.Forms.Timer();
        this.animationTimer.Interval = 16;
        this.animationTimer.Tick += OnAnimationTick;
        AppIconCache.IconReady += OnCachedIconReady;
    }

    internal static void WarmUpIconCacheAsync()
    {
        Task<List<LaunchpadAppDescriptor>> task = EnsureDescriptorTask();
        task.ContinueWith(delegate(Task<List<LaunchpadAppDescriptor>> completed)
        {
            if (completed.Status != TaskStatus.RanToCompletion)
            {
                if (completed.Exception != null)
                {
                    Program.LogException(completed.Exception.GetBaseException());
                }

                return;
            }

            AppIconCache.WarmUp(BuildIconRequests(completed.Result));
        });
    }

    public bool IsLaunchpadVisible
    {
        get { return this.Visible && !this.hiding; }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.animationTimer.Stop();
        this.animationTimer.Tick -= OnAnimationTick;
        this.animationTimer.Dispose();
        AppIconCache.IconReady -= OnCachedIconReady;
        for (int i = 0; i < this.apps.Count; i++)
        {
            this.apps[i].Dispose();
        }

        DisposeBackgroundBitmap();
        base.OnFormClosed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), S(22)))
        {
            this.Region = new Region(path);
        }

        DisposeBackgroundBitmap();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawLaunchpad(e.Graphics);
    }

    private void OnCachedIconReady(object sender, AppIconReadyEventArgs e)
    {
        if (e == null || this.IsDisposed)
        {
            return;
        }

        if (this.InvokeRequired)
        {
            if (!this.IsHandleCreated)
            {
                return;
            }

            try
            {
                this.BeginInvoke(new EventHandler<AppIconReadyEventArgs>(OnCachedIconReady), sender, e);
            }
            catch
            {
            }

            return;
        }

        for (int i = 0; i < this.apps.Count; i++)
        {
            if (string.Equals(this.apps[i].IconKey, e.Key, StringComparison.OrdinalIgnoreCase))
            {
                InvalidateApp(i);
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool toggleHovered = GetAllProgramsToggleRect().Contains(e.Location);
        bool deleteHovered = IsDeleteButtonVisible() && GetDeleteButtonRect().Contains(e.Location);
        int index = FindAppAtPoint(e.Location);
        if (index != this.hoveredIndex || toggleHovered != this.allProgramsToggleHovered || deleteHovered != this.deleteButtonHovered)
        {
            this.hoveredIndex = index;
            this.allProgramsToggleHovered = toggleHovered;
            this.deleteButtonHovered = deleteHovered;
            this.Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (this.hoveredIndex >= 0 || this.allProgramsToggleHovered || this.deleteButtonHovered)
        {
            this.hoveredIndex = -1;
            this.allProgramsToggleHovered = false;
            this.deleteButtonHovered = false;
            this.Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (this.maxScroll <= 0)
        {
            return;
        }

        this.targetScrollOffset = Math.Max(0, Math.Min(this.maxScroll, this.targetScrollOffset - e.Delta / 120 * S(86)));
        this.animationTimer.Start();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (GetAllProgramsToggleRect().Contains(e.Location))
        {
            this.showAllPrograms = !this.showAllPrograms;
            this.launchpadUnpinMode = false;
            RebuildVisibleApps();
            return;
        }

        if (IsDeleteButtonVisible() && GetDeleteButtonRect().Contains(e.Location))
        {
            this.launchpadUnpinMode = !this.launchpadUnpinMode;
            this.Invalidate();
            return;
        }

        if (this.launchpadUnpinMode && HandleLaunchpadUnpinBadgeClick(e.Location))
        {
            return;
        }

        int index = FindAppAtPoint(e.Location);
        if (index >= 0 && index < this.apps.Count)
        {
            LaunchApp(this.apps[index]);
            HideLaunchpad();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            HideLaunchpad();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void ShowLaunchpad(Rectangle workArea, Rectangle dockBounds, bool topMost, string launchpadItemsText, string dockItemsText)
    {
        this.TopMost = topMost;
        this.launchpadItemsText = launchpadItemsText ?? string.Empty;
        this.dockItemsText = dockItemsText ?? string.Empty;
        this.showAllPrograms = false;
        this.launchpadUnpinMode = false;
        this.hoveredIndex = -1;
        this.allProgramsToggleHovered = false;
        this.deleteButtonHovered = false;
        this.targetScrollOffset = 0;
        this.scrollOffset = 0.0;
        RebuildVisibleApps();
        PositionLaunchpad(workArea, dockBounds);
        this.hiding = false;
        this.panelAnimationActive = true;
        this.animationStartedUtc = DateTime.UtcNow;
        this.Opacity = 1.0;
        this.Location = GetHiddenLocation();
        if (!this.Visible)
        {
            this.Show();
        }

        this.BringToFront();
        EnsureAppsLoading();
        this.animationTimer.Start();
        this.Invalidate();
    }

    public void UpdatePinnedItemsText(string launchpadItemsText, string dockItemsText)
    {
        launchpadItemsText = launchpadItemsText ?? string.Empty;
        dockItemsText = dockItemsText ?? string.Empty;
        if (string.Equals(this.launchpadItemsText, launchpadItemsText, StringComparison.Ordinal) &&
            string.Equals(this.dockItemsText, dockItemsText, StringComparison.Ordinal))
        {
            return;
        }

        this.launchpadItemsText = launchpadItemsText;
        this.dockItemsText = dockItemsText;
        if (!this.showAllPrograms)
        {
            RebuildVisibleApps();
            if (!HasLaunchpadPinnedApps())
            {
                this.launchpadUnpinMode = false;
            }
        }
    }

    private void RemoveLaunchpadPinnedItem(string command)
    {
        bool removed;
        this.launchpadItemsText = DockItem.RemoveItemByCommand(this.launchpadItemsText, command, out removed);
        if (!removed)
        {
            return;
        }

        WidgetSettings settings = WidgetSettings.Load();
        settings.LaunchpadItemsText = this.launchpadItemsText;
        settings.Save();
        RebuildVisibleApps();
        if (!HasLaunchpadPinnedApps())
        {
            this.launchpadUnpinMode = false;
        }

        Program.LogInfo("Launchpad item unpinned.");
    }

    public void HideLaunchpad()
    {
        if (!this.Visible)
        {
            return;
        }

        this.hiding = true;
        this.panelAnimationActive = true;
        this.animationStartedUtc = DateTime.UtcNow;
        this.animationTimer.Start();
    }

    public bool ContainsScreenPoint(Point point)
    {
        return this.Visible && this.Bounds.Contains(point);
    }

    public void PositionLaunchpad(Rectangle workArea, Rectangle dockBounds)
    {
        int marginX = S(72);
        int width = Math.Min(S(1180), Math.Max(S(720), workArea.Width - marginX * 2));
        width = Math.Min(width, workArea.Width - S(28));
        int availableAboveDock = Math.Max(S(360), dockBounds.Top - workArea.Top - S(26));
        int height = Math.Min(S(760), Math.Min((int)Math.Round(workArea.Height * 0.68), availableAboveDock));
        height = Math.Max(S(360), height);
        int left = workArea.Left + (workArea.Width - width) / 2;
        int top = Math.Max(workArea.Top + S(40), dockBounds.Top - height - S(16));
        this.targetBounds = new Rectangle(left, top, width, height);
        if (this.Size != this.targetBounds.Size)
        {
            this.Size = this.targetBounds.Size;
            this.targetScrollOffset = Math.Max(0, Math.Min(this.targetScrollOffset, CalculateMaxScroll()));
            this.scrollOffset = Math.Max(0.0, Math.Min(this.scrollOffset, this.targetScrollOffset));
        }

        if (!this.animationTimer.Enabled)
        {
            this.Location = this.targetBounds.Location;
        }
    }

    private void OnAnimationTick(object sender, EventArgs e)
    {
        bool needsMoreTicks = false;
        if (this.panelAnimationActive)
        {
            double progress = Math.Max(0.0, Math.Min(1.0, (DateTime.UtcNow - this.animationStartedUtc).TotalMilliseconds / AnimationDurationMs));
            double eased = EaseOutCubic(progress);
            if (this.hiding)
            {
                eased = 1.0 - eased;
            }

            Point hidden = GetHiddenLocation();
            this.Location = new Point(
                (int)Math.Round(hidden.X + (this.targetBounds.Left - hidden.X) * eased),
                (int)Math.Round(hidden.Y + (this.targetBounds.Top - hidden.Y) * eased));
            this.Invalidate();

            if (progress >= 1.0)
            {
                this.panelAnimationActive = false;
                if (this.hiding)
                {
                    this.Hide();
                    this.hiding = false;
                }
                else
                {
                    this.Location = this.targetBounds.Location;
                }
            }
            else
            {
                needsMoreTicks = true;
            }
        }

        if (UpdateScrollAnimation())
        {
            needsMoreTicks = true;
            this.Invalidate();
        }

        if (!needsMoreTicks)
        {
            this.animationTimer.Stop();
        }
    }

    private bool UpdateScrollAnimation()
    {
        double delta = this.targetScrollOffset - this.scrollOffset;
        if (Math.Abs(delta) < 0.5)
        {
            if (Math.Abs(this.scrollOffset - this.targetScrollOffset) > 0.01)
            {
                this.scrollOffset = this.targetScrollOffset;
                return true;
            }

            return false;
        }

        this.scrollOffset += delta * 0.26;
        return true;
    }

    private Point GetHiddenLocation()
    {
        return new Point(this.targetBounds.Left, this.targetBounds.Top + S(34));
    }

    private void DrawLaunchpad(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        EnsureBackgroundBitmap();
        if (this.backgroundBitmap != null)
        {
            g.DrawImageUnscaled(this.backgroundBitmap, 0, 0);
        }

        DrawAllProgramsToggle(g);
        DrawDeleteButton(g);
        if (this.apps.Count == 0)
        {
            DrawLoadingText(g);
            return;
        }

        Rectangle content = GetContentRect();
        DrawApps(g, content);
        DrawScrollBar(g, content);
    }

    private void EnsureBackgroundBitmap()
    {
        if (this.backgroundBitmap != null &&
            this.backgroundBitmapSize == this.Size &&
            this.backgroundBitmapAppCount == this.apps.Count &&
            this.backgroundBitmapShowAllPrograms == this.showAllPrograms)
        {
            return;
        }

        DisposeBackgroundBitmap();
        if (this.Width <= 0 || this.Height <= 0)
        {
            return;
        }

        this.backgroundBitmap = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppPArgb);
        this.backgroundBitmapSize = this.Size;
        this.backgroundBitmapAppCount = this.apps.Count;
        this.backgroundBitmapShowAllPrograms = this.showAllPrograms;
        using (Graphics g = Graphics.FromImage(this.backgroundBitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Color.FromArgb(22, 23, 28));

            RectangleF shell = new RectangleF(0, 0, this.Width - 1, this.Height - 1);
            using (GraphicsPath path = RoundedRectangle(shell, S(22)))
            using (SolidBrush background = new SolidBrush(Color.FromArgb(245, 22, 23, 28)))
            using (Pen border = new Pen(Color.FromArgb(92, 255, 255, 255), Math.Max(1.0f, this.scale)))
            {
                g.FillPath(background, path);
                g.DrawPath(border, path);
            }

            Rectangle content = GetContentRect();
            using (Font titleFont = new Font("Segoe UI", S(18), FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush titleBrush = new SolidBrush(Color.FromArgb(240, 245, 247, 251)))
            using (StringFormat titleFormat = new StringFormat())
            {
                titleFormat.Alignment = StringAlignment.Near;
                titleFormat.LineAlignment = StringAlignment.Center;
                RectangleF toggleRect = GetAllProgramsToggleRect();
                RectangleF titleRect = new RectangleF(toggleRect.Right + S(14), S(12), Math.Max(1.0f, content.Right - toggleRect.Right - S(14)), S(34));
                g.DrawString(this.showAllPrograms ? "全部应用" : "常用程序", titleFont, titleBrush, titleRect, titleFormat);
            }

            using (Font countFont = new Font("Segoe UI", S(12), FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush countBrush = new SolidBrush(Color.FromArgb(168, 230, 234, 240)))
            using (StringFormat countFormat = new StringFormat())
            {
                countFormat.Alignment = StringAlignment.Far;
                countFormat.LineAlignment = StringAlignment.Center;
                float reservedRight = this.showAllPrograms ? 0.0f : S(46);
                RectangleF countRect = new RectangleF(content.Left, S(12), Math.Max(1.0f, content.Width - reservedRight), S(34));
                string countText = this.apps.Count == 0 ? "loading" : this.apps.Count.ToString(CultureInfo.InvariantCulture);
                g.DrawString(countText, countFont, countBrush, countRect, countFormat);
            }
        }
    }

    private void DisposeBackgroundBitmap()
    {
        if (this.backgroundBitmap != null)
        {
            this.backgroundBitmap.Dispose();
            this.backgroundBitmap = null;
        }

        this.backgroundBitmapSize = Size.Empty;
        this.backgroundBitmapAppCount = -1;
        this.backgroundBitmapShowAllPrograms = false;
    }

    private void DrawLoadingText(Graphics g)
    {
        Rectangle content = GetContentRect();
        using (Font font = new Font("Segoe UI", S(16), FontStyle.Regular, GraphicsUnit.Pixel))
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(196, 240, 244, 248)))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            g.DrawString("正在载入应用...", font, brush, content, format);
        }
    }

    private RectangleF GetAllProgramsToggleRect()
    {
        return new RectangleF(S(30), S(14), S(124), S(30));
    }

    private RectangleF GetDeleteButtonRect()
    {
        return new RectangleF(this.Width - S(64), S(12), S(36), S(34));
    }

    private bool IsDeleteButtonVisible()
    {
        return !this.showAllPrograms;
    }

    private void DrawAllProgramsToggle(Graphics g)
    {
        RectangleF rect = GetAllProgramsToggleRect();
        Color backColor = this.showAllPrograms ?
            Color.FromArgb(this.allProgramsToggleHovered ? 118 : 92, 64, 171, 255) :
            Color.FromArgb(this.allProgramsToggleHovered ? 72 : 48, 255, 255, 255);
        Color borderColor = this.showAllPrograms ?
            Color.FromArgb(150, 144, 214, 255) :
            Color.FromArgb(72, 255, 255, 255);

        using (GraphicsPath path = RoundedRectangle(rect, rect.Height / 2.0f))
        using (SolidBrush background = new SolidBrush(backColor))
        using (Pen border = new Pen(borderColor, Math.Max(1.0f, this.scale)))
        {
            g.FillPath(background, path);
            g.DrawPath(border, path);
        }

        float switchWidth = S(28);
        float switchHeight = S(16);
        RectangleF switchRect = new RectangleF(rect.Left + S(9), rect.Top + (rect.Height - switchHeight) / 2.0f, switchWidth, switchHeight);
        using (GraphicsPath switchPath = RoundedRectangle(switchRect, switchHeight / 2.0f))
        using (SolidBrush switchBrush = new SolidBrush(this.showAllPrograms ? Color.FromArgb(235, 95, 218, 132) : Color.FromArgb(94, 255, 255, 255)))
        {
            g.FillPath(switchBrush, switchPath);
        }

        float knobSize = S(12);
        float knobLeft = this.showAllPrograms ? switchRect.Right - knobSize - S(2) : switchRect.Left + S(2);
        RectangleF knob = new RectangleF(knobLeft, switchRect.Top + (switchRect.Height - knobSize) / 2.0f, knobSize, knobSize);
        using (SolidBrush knobBrush = new SolidBrush(Color.FromArgb(245, 255, 255, 255)))
        {
            g.FillEllipse(knobBrush, knob);
        }

        RectangleF textRect = new RectangleF(switchRect.Right + S(7), rect.Top, rect.Right - switchRect.Right - S(12), rect.Height);
        using (Font font = new Font("Segoe UI", S(12), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(238, 246, 249, 253)))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString("所有程序", font, textBrush, textRect, format);
        }
    }

    private void DrawDeleteButton(Graphics g)
    {
        if (!IsDeleteButtonVisible())
        {
            return;
        }

        RectangleF rect = GetDeleteButtonRect();
        Color backColor = this.launchpadUnpinMode ?
            Color.FromArgb(this.deleteButtonHovered ? 145 : 116, 58, 148, 255) :
            Color.FromArgb(this.deleteButtonHovered ? 78 : 52, 255, 255, 255);
        Color borderColor = this.launchpadUnpinMode ?
            Color.FromArgb(170, 145, 210, 255) :
            Color.FromArgb(72, 255, 255, 255);

        using (GraphicsPath path = RoundedRectangle(rect, S(8)))
        using (SolidBrush background = new SolidBrush(backColor))
        using (Pen border = new Pen(borderColor, Math.Max(1.0f, this.scale)))
        {
            g.FillPath(background, path);
            g.DrawPath(border, path);
        }

        DrawDeleteGlyph(g, rect);
    }

    private void DrawDeleteGlyph(Graphics g, RectangleF rect)
    {
        RectangleF icon = new RectangleF(rect.Left + rect.Width * 0.25f, rect.Top + rect.Height * 0.22f, rect.Width * 0.50f, rect.Height * 0.56f);
        float stroke = Math.Max(1.6f, this.scale * 1.8f);
        using (Pen pen = new Pen(Color.FromArgb(238, 246, 250, 255), stroke))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            g.DrawLine(pen, icon.Left + icon.Width * 0.18f, icon.Top + icon.Height * 0.24f, icon.Right - icon.Width * 0.18f, icon.Top + icon.Height * 0.24f);
            g.DrawLine(pen, icon.Left + icon.Width * 0.35f, icon.Top + icon.Height * 0.10f, icon.Right - icon.Width * 0.35f, icon.Top + icon.Height * 0.10f);
            g.DrawLine(pen, icon.Left + icon.Width * 0.28f, icon.Top + icon.Height * 0.34f, icon.Left + icon.Width * 0.34f, icon.Bottom);
            g.DrawLine(pen, icon.Right - icon.Width * 0.28f, icon.Top + icon.Height * 0.34f, icon.Right - icon.Width * 0.34f, icon.Bottom);
            g.DrawLine(pen, icon.Left + icon.Width * 0.46f, icon.Top + icon.Height * 0.42f, icon.Left + icon.Width * 0.46f, icon.Bottom - icon.Height * 0.12f);
            g.DrawLine(pen, icon.Right - icon.Width * 0.46f, icon.Top + icon.Height * 0.42f, icon.Right - icon.Width * 0.46f, icon.Bottom - icon.Height * 0.12f);
        }
    }

    private void DrawApps(Graphics g, Rectangle content)
    {
        GridMetrics metrics = CalculateGridMetrics(content);
        this.maxScroll = CalculateMaxScroll(metrics);
        int scroll = (int)Math.Round(this.scrollOffset);
        int firstRow = Math.Max(0, scroll / Math.Max(1, metrics.RowStride) - 1);
        int lastRow = Math.Min(metrics.Rows - 1, (scroll + content.Height) / Math.Max(1, metrics.RowStride) + 1);

        GraphicsState state = g.Save();
        g.SetClip(content);
        using (Font labelFont = new Font("Segoe UI", S(12), FontStyle.Regular, GraphicsUnit.Pixel))
        using (StringFormat labelFormat = new StringFormat())
        using (SolidBrush labelBrush = new SolidBrush(Color.FromArgb(236, 242, 245, 249)))
        {
            labelFormat.Alignment = StringAlignment.Center;
            labelFormat.LineAlignment = StringAlignment.Near;
            labelFormat.Trimming = StringTrimming.EllipsisWord;
            labelFormat.FormatFlags = StringFormatFlags.LineLimit;

            for (int row = firstRow; row <= lastRow; row++)
            {
                for (int col = 0; col < metrics.Columns; col++)
                {
                    int index = row * metrics.Columns + col;
                    if (index < 0 || index >= this.apps.Count)
                    {
                        continue;
                    }

                    RectangleF tile = GetTileRect(metrics, row, col);
                    tile.Y -= scroll;
                    DrawAppTile(g, this.apps[index], tile, index == this.hoveredIndex, labelFont, labelBrush, labelFormat);
                }
            }
        }

        g.Restore(state);
    }

    private void DrawAppTile(Graphics g, LaunchpadApp app, RectangleF tile, bool hovered, Font labelFont, Brush labelBrush, StringFormat labelFormat)
    {
        if (hovered)
        {
            using (GraphicsPath hoverPath = RoundedRectangle(new RectangleF(tile.Left + S(5), tile.Top + S(2), tile.Width - S(10), tile.Height - S(5)), S(12)))
            using (SolidBrush hoverBrush = new SolidBrush(Color.FromArgb(48, 255, 255, 255)))
            {
                g.FillPath(hoverBrush, hoverPath);
            }
        }

        float iconSize = S(64);
        RectangleF iconRect = new RectangleF(
            tile.Left + (tile.Width - iconSize) / 2.0f,
            tile.Top + S(10),
            iconSize,
            iconSize);
        Bitmap bitmap = AppIconCache.GetBitmap(app.IconKey);
        if (bitmap != null)
        {
            g.DrawImage(bitmap, iconRect);
        }
        else
        {
            using (SolidBrush fallback = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
            {
                g.FillEllipse(fallback, iconRect);
            }
        }

        RectangleF labelRect = new RectangleF(tile.Left + S(5), iconRect.Bottom + S(8), tile.Width - S(10), S(42));
        g.DrawString(app.Name, labelFont, labelBrush, labelRect, labelFormat);

        if (this.launchpadUnpinMode && !this.showAllPrograms && app.IsLaunchpadPinned)
        {
            DrawUnpinBadge(g, GetUnpinBadgeRect(iconRect));
        }
    }

    private RectangleF GetUnpinBadgeRect(RectangleF iconRect)
    {
        float size = Math.Max(S(18), iconRect.Width * 0.30f);
        return new RectangleF(iconRect.Right - size * 0.74f, iconRect.Top - size * 0.22f, size, size);
    }

    private void DrawUnpinBadge(Graphics g, RectangleF rect)
    {
        using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
        {
            RectangleF shadow = new RectangleF(rect.Left + S(1), rect.Top + S(1), rect.Width, rect.Height);
            g.FillEllipse(shadowBrush, shadow);
        }

        using (SolidBrush background = new SolidBrush(Color.FromArgb(238, 225, 48, 65)))
        using (Pen border = new Pen(Color.FromArgb(220, 255, 255, 255), Math.Max(1.0f, this.scale)))
        using (Pen cross = new Pen(Color.White, Math.Max(1.8f, this.scale * 2.0f)))
        {
            cross.StartCap = LineCap.Round;
            cross.EndCap = LineCap.Round;
            g.FillEllipse(background, rect);
            g.DrawEllipse(border, rect);
            float pad = rect.Width * 0.32f;
            g.DrawLine(cross, rect.Left + pad, rect.Top + pad, rect.Right - pad, rect.Bottom - pad);
            g.DrawLine(cross, rect.Right - pad, rect.Top + pad, rect.Left + pad, rect.Bottom - pad);
        }
    }

    private void DrawScrollBar(Graphics g, Rectangle content)
    {
        if (this.maxScroll <= 0)
        {
            return;
        }

        float trackHeight = content.Height;
        float thumbHeight = Math.Max(S(38), trackHeight * content.Height / (content.Height + this.maxScroll));
        float thumbTop = content.Top + (float)((trackHeight - thumbHeight) * this.scrollOffset / Math.Max(1, this.maxScroll));
        RectangleF thumb = new RectangleF(this.Width - S(13), thumbTop, S(4), thumbHeight);
        using (GraphicsPath path = RoundedRectangle(thumb, thumb.Width / 2.0f))
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(130, 255, 255, 255)))
        {
            g.FillPath(brush, path);
        }
    }

    private int FindAppAtPoint(Point point)
    {
        Rectangle content = GetContentRect();
        if (!content.Contains(point))
        {
            return -1;
        }

        GridMetrics metrics = CalculateGridMetrics(content);
        int localY = point.Y - content.Top + (int)Math.Round(this.scrollOffset);
        int row = localY / Math.Max(1, metrics.RowStride);
        int col = (point.X - content.Left) / Math.Max(1, metrics.ColumnStride);
        if (row < 0 || col < 0 || col >= metrics.Columns)
        {
            return -1;
        }

        int index = row * metrics.Columns + col;
        if (index < 0 || index >= this.apps.Count)
        {
            return -1;
        }

        RectangleF tile = GetTileRect(metrics, row, col);
        tile.Y -= (int)Math.Round(this.scrollOffset);
        return tile.Contains(point.X, point.Y) ? index : -1;
    }

    private bool HandleLaunchpadUnpinBadgeClick(Point point)
    {
        int index = FindUnpinBadgeAtPoint(point);
        if (index < 0 || index >= this.apps.Count)
        {
            return false;
        }

        LaunchpadApp app = this.apps[index];
        if (app == null || !app.IsLaunchpadPinned)
        {
            return false;
        }

        RemoveLaunchpadPinnedItem(app.Command);
        return true;
    }

    private int FindUnpinBadgeAtPoint(Point point)
    {
        if (!this.launchpadUnpinMode || this.showAllPrograms)
        {
            return -1;
        }

        Rectangle content = GetContentRect();
        if (!content.Contains(point))
        {
            return -1;
        }

        GridMetrics metrics = CalculateGridMetrics(content);
        int scroll = (int)Math.Round(this.scrollOffset);
        int firstRow = Math.Max(0, scroll / Math.Max(1, metrics.RowStride) - 1);
        int lastRow = Math.Min(metrics.Rows - 1, (scroll + content.Height) / Math.Max(1, metrics.RowStride) + 1);
        for (int row = firstRow; row <= lastRow; row++)
        {
            for (int col = 0; col < metrics.Columns; col++)
            {
                int index = row * metrics.Columns + col;
                if (index < 0 || index >= this.apps.Count || !this.apps[index].IsLaunchpadPinned)
                {
                    continue;
                }

                RectangleF tile = GetTileRect(metrics, row, col);
                tile.Y -= scroll;
                float iconSize = S(64);
                RectangleF iconRect = new RectangleF(
                    tile.Left + (tile.Width - iconSize) / 2.0f,
                    tile.Top + S(10),
                    iconSize,
                    iconSize);
                if (GetUnpinBadgeRect(iconRect).Contains(point.X, point.Y))
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private Rectangle GetContentRect()
    {
        return new Rectangle(S(30), S(54), Math.Max(1, this.Width - S(60)), Math.Max(1, this.Height - S(78)));
    }

    private int CalculateMaxScroll()
    {
        return CalculateMaxScroll(CalculateGridMetrics(GetContentRect()));
    }

    private int CalculateMaxScroll(GridMetrics metrics)
    {
        int contentHeight = metrics.Rows <= 0 ? 0 : metrics.Rows * metrics.RowStride - metrics.RowGap;
        return Math.Max(0, contentHeight - metrics.Content.Height);
    }

    private GridMetrics CalculateGridMetrics(Rectangle content)
    {
        int tileWidth = S(112);
        int tileHeight = S(128);
        int minGap = S(10);
        int columns = Math.Max(4, (content.Width + minGap) / Math.Max(1, tileWidth + minGap));
        columns = Math.Max(1, Math.Min(columns, Math.Max(1, this.apps.Count)));
        int gap = columns <= 1 ? 0 : Math.Max(minGap, (content.Width - columns * tileWidth) / Math.Max(1, columns - 1));
        int rowGap = S(18);
        int rows = this.apps.Count == 0 ? 0 : (this.apps.Count + columns - 1) / columns;
        return new GridMetrics
        {
            Content = content,
            Columns = columns,
            Rows = rows,
            TileWidth = tileWidth,
            TileHeight = tileHeight,
            ColumnGap = gap,
            RowGap = rowGap,
            ColumnStride = tileWidth + gap,
            RowStride = tileHeight + rowGap
        };
    }

    private RectangleF GetTileRect(GridMetrics metrics, int row, int col)
    {
        return new RectangleF(
            metrics.Content.Left + col * metrics.ColumnStride,
            metrics.Content.Top + row * metrics.RowStride,
            metrics.TileWidth,
            metrics.TileHeight);
    }

    private void LaunchApp(LaunchpadApp app)
    {
        if (app == null || string.IsNullOrEmpty(app.Command))
        {
            return;
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = app.Command;
            startInfo.UseShellExecute = true;
            Process.Start(startInfo);
            Program.LogInfo("Launchpad item launched: " + app.Name + " -> " + app.Command);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private static Task<List<LaunchpadAppDescriptor>> EnsureDescriptorTask()
    {
        lock (DescriptorSync)
        {
            if (descriptorTask == null)
            {
                descriptorTask = Task.Run((Func<List<LaunchpadAppDescriptor>>)LoadStartMenuAppDescriptors);
            }

            return descriptorTask;
        }
    }

    private static List<AppIconRequest> BuildIconRequests(List<LaunchpadAppDescriptor> descriptors)
    {
        List<AppIconRequest> requests = new List<AppIconRequest>();
        if (descriptors == null)
        {
            return requests;
        }

        for (int i = 0; i < descriptors.Count; i++)
        {
            requests.Add(new AppIconRequest(GetDescriptorIconPath(descriptors[i]), descriptors[i].Name));
        }

        return requests;
    }

    private void RebuildVisibleApps()
    {
        if (!this.descriptorsLoaded)
        {
            return;
        }

        for (int i = 0; i < this.apps.Count; i++)
        {
            this.apps[i].Dispose();
        }

        this.apps.Clear();
        List<LaunchpadAppDescriptor> source = this.showAllPrograms ?
            this.allDescriptors :
            BuildCommonAppDescriptors(this.allDescriptors, this.launchpadItemsText, this.dockItemsText);
        for (int i = 0; i < source.Count; i++)
        {
            this.apps.Add(new LaunchpadApp
            {
                Name = source[i].Name,
                Command = source[i].Command,
                IconKey = AppIconCache.RequestIcon(GetDescriptorIconPath(source[i]), source[i].Name),
                IsLaunchpadPinned = source[i].IsLaunchpadPinned
            });
        }

        this.hoveredIndex = -1;
        this.targetScrollOffset = Math.Max(0, Math.Min(this.targetScrollOffset, CalculateMaxScroll()));
        this.scrollOffset = Math.Max(0.0, Math.Min(this.scrollOffset, this.targetScrollOffset));
        DisposeBackgroundBitmap();
        this.Invalidate();
    }

    private bool HasLaunchpadPinnedApps()
    {
        for (int i = 0; i < this.apps.Count; i++)
        {
            if (this.apps[i].IsLaunchpadPinned)
            {
                return true;
            }
        }

        return false;
    }

    private static List<LaunchpadAppDescriptor> BuildCommonAppDescriptors(List<LaunchpadAppDescriptor> allApps, string launchpadItemsText, string dockItemsText)
    {
        List<LaunchpadAppDescriptor> common = new List<LaunchpadAppDescriptor>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddPinnedLaunchpadDescriptors(common, seen, launchpadItemsText, true);
        AddPinnedLaunchpadDescriptors(common, seen, dockItemsText, false);

        if (common.Count == 0 && allApps != null)
        {
            int fallbackCount = Math.Min(12, allApps.Count);
            for (int i = 0; i < fallbackCount; i++)
            {
                common.Add(allApps[i]);
            }
        }

        return common;
    }

    private static void AddPinnedLaunchpadDescriptors(List<LaunchpadAppDescriptor> descriptors, HashSet<string> seen, string itemsText, bool isLaunchpadPinned)
    {
        List<DockItem> items = DockItem.ParseItems(itemsText);
        for (int i = 0; i < items.Count; i++)
        {
            DockItem item = items[i];
            string key = BuildDescriptorKey(item.Command, item.Label);
            if (!seen.Add(key))
            {
                continue;
            }

            descriptors.Add(new LaunchpadAppDescriptor
            {
                Name = string.IsNullOrEmpty(item.Label) ? "App" : item.Label,
                Command = item.Command,
                IconPath = ResolveLaunchpadIconPath(item.Command),
                IsLaunchpadPinned = isLaunchpadPinned
            });
        }
    }

    private static string GetDescriptorIconPath(LaunchpadAppDescriptor descriptor)
    {
        if (descriptor == null)
        {
            return string.Empty;
        }

        return string.IsNullOrEmpty(descriptor.IconPath) ? descriptor.Command : descriptor.IconPath;
    }

    private static string BuildDescriptorKey(string command, string name)
    {
        command = command ?? string.Empty;
        if (command.Trim().Length > 0)
        {
            return command.Trim().Trim('"').ToUpperInvariant();
        }

        return (name ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string ResolveLaunchpadIconPath(string command)
    {
        string fileName;
        string arguments;
        SplitCommandLine(command, out fileName, out arguments);
        string uriIconPath = ResolveUriIconPath(fileName);
        if (!string.IsNullOrEmpty(uriIconPath))
        {
            return uriIconPath;
        }

        if (string.IsNullOrEmpty(fileName) || IsShellCommand(fileName))
        {
            return string.Empty;
        }

        string expanded = Environment.ExpandEnvironmentVariables(fileName).Trim().Trim('"');
        if (File.Exists(expanded))
        {
            return expanded;
        }

        string[] candidates = new string[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), expanded),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), expanded)
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            if (File.Exists(candidates[i]))
            {
                return candidates[i];
            }
        }

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string[] directories = path.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < directories.Length; i++)
        {
            try
            {
                string candidate = Path.Combine(directories[i].Trim(), expanded);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static string ResolveUriIconPath(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        if (fileName.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            string settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "ImmersiveControlPanel",
                "SystemSettings.exe");
            if (File.Exists(settingsPath))
            {
                return settingsPath;
            }

            string controlPanelPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "control.exe");
            if (File.Exists(controlPanelPath))
            {
                return controlPanelPath;
            }
        }

        return string.Empty;
    }

    private static void SplitCommandLine(string command, out string fileName, out string arguments)
    {
        fileName = string.Empty;
        arguments = string.Empty;
        if (string.IsNullOrEmpty(command))
        {
            return;
        }

        string value = Environment.ExpandEnvironmentVariables(command).Trim();
        if (value.Length == 0)
        {
            return;
        }

        if (IsShellCommand(value) || File.Exists(value))
        {
            fileName = value;
            return;
        }

        if (value.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = value.IndexOf('"', 1);
            if (end > 1)
            {
                fileName = value.Substring(1, end - 1);
                arguments = value.Substring(end + 1).Trim();
                return;
            }
        }

        int split = value.IndexOf(' ');
        if (split > 0)
        {
            string candidate = value.Substring(0, split);
            if (File.Exists(candidate) || candidate.IndexOf('\\') < 0)
            {
                fileName = candidate;
                arguments = value.Substring(split + 1).Trim();
                return;
            }
        }

        fileName = value;
    }

    private static bool IsShellCommand(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int colon = value.IndexOf(':');
        if (colon <= 1)
        {
            return false;
        }

        int slash = value.IndexOf('\\');
        return slash < 0 || slash > colon;
    }

    private void EnsureAppsLoading()
    {
        if (this.appsLoadingStarted)
        {
            return;
        }

        this.appsLoadingStarted = true;
        Task<List<LaunchpadAppDescriptor>> descriptorLoadTask = EnsureDescriptorTask();
        descriptorLoadTask.ContinueWith(delegate(Task<List<LaunchpadAppDescriptor>> completed)
        {
            if (completed.Status != TaskStatus.RanToCompletion)
            {
                if (completed.Exception != null)
                {
                    Program.LogException(completed.Exception.GetBaseException());
                }

                return;
            }

            List<LaunchpadAppDescriptor> descriptors = completed.Result;
            SafeBeginInvoke(delegate
            {
                if (this.IsDisposed)
                {
                    return;
                }

                for (int i = 0; i < this.apps.Count; i++)
                {
                    this.apps[i].Dispose();
                }

                this.apps.Clear();
                this.allDescriptors.Clear();
                for (int i = 0; i < descriptors.Count; i++)
                {
                    this.allDescriptors.Add(descriptors[i]);
                }

                this.descriptorsLoaded = true;
                List<LaunchpadAppDescriptor> visibleDescriptors = this.showAllPrograms ?
                    this.allDescriptors :
                    BuildCommonAppDescriptors(this.allDescriptors, this.launchpadItemsText, this.dockItemsText);
                for (int i = 0; i < visibleDescriptors.Count; i++)
                {
                    this.apps.Add(new LaunchpadApp
                    {
                        Name = visibleDescriptors[i].Name,
                        Command = visibleDescriptors[i].Command,
                        IconKey = AppIconCache.RequestIcon(GetDescriptorIconPath(visibleDescriptors[i]), visibleDescriptors[i].Name),
                        IsLaunchpadPinned = visibleDescriptors[i].IsLaunchpadPinned
                    });
                }

                DisposeBackgroundBitmap();
                this.targetScrollOffset = Math.Max(0, Math.Min(this.targetScrollOffset, CalculateMaxScroll()));
                this.scrollOffset = Math.Max(0.0, Math.Min(this.scrollOffset, this.targetScrollOffset));
                this.Invalidate();
            });
        });
    }

    private void SafeBeginInvoke(Action action)
    {
        if (action == null || this.IsDisposed)
        {
            return;
        }

        try
        {
            if (this.IsHandleCreated)
            {
                this.BeginInvoke(action);
            }
        }
        catch
        {
        }
    }

    private void InvalidateApp(int index)
    {
        if (!this.Visible || index < 0 || index >= this.apps.Count)
        {
            return;
        }

        Rectangle content = GetContentRect();
        GridMetrics metrics = CalculateGridMetrics(content);
        if (metrics.Columns <= 0)
        {
            this.Invalidate();
            return;
        }

        int row = index / metrics.Columns;
        int col = index % metrics.Columns;
        RectangleF tile = GetTileRect(metrics, row, col);
        tile.Y -= (int)Math.Round(this.scrollOffset);
        Rectangle invalid = Rectangle.Ceiling(tile);
        invalid.Inflate(S(4), S(4));
        this.Invalidate(invalid);
    }

    private static List<LaunchpadAppDescriptor> LoadStartMenuAppDescriptors()
    {
        List<LaunchpadAppDescriptor> apps = new List<LaunchpadAppDescriptor>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string userStart = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        string commonStart = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);

        AddStartMenuAppsFromRoot(apps, seen, userStart, SearchOption.TopDirectoryOnly);
        AddStartMenuAppsFromRoot(apps, seen, Path.Combine(userStart, "Programs"), SearchOption.AllDirectories);
        AddStartMenuAppsFromRoot(apps, seen, commonStart, SearchOption.TopDirectoryOnly);
        AddStartMenuAppsFromRoot(apps, seen, Path.Combine(commonStart, "Programs"), SearchOption.AllDirectories);

        apps.Sort(delegate(LaunchpadAppDescriptor left, LaunchpadAppDescriptor right)
        {
            return string.Compare(left.Name, right.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        return apps;
    }

    private static void AddStartMenuAppsFromRoot(List<LaunchpadAppDescriptor> apps, HashSet<string> seen, string root, SearchOption searchOption)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        try
        {
            string[] files = Directory.GetFiles(root, "*.*", searchOption);
            for (int i = 0; i < files.Length; i++)
            {
                string extension = Path.GetExtension(files[i]);
                if (!string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".appref-ms", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string name = Path.GetFileNameWithoutExtension(files[i]);
                if (string.IsNullOrEmpty(name) || string.Equals(name, "desktop", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string key = name + "|" + files[i];
                if (!seen.Add(key))
                {
                    continue;
                }

                apps.Add(new LaunchpadAppDescriptor
                {
                    Name = name,
                    Command = files[i],
                    IconPath = files[i]
                });
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = radius * 2.0f;
        GraphicsPath path = new GraphicsPath();
        if (diameter <= 0.0f)
        {
            path.AddRectangle(bounds);
            path.CloseFigure();
            return path;
        }

        RectangleF arc = new RectangleF(bounds.Location, new SizeF(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static double EaseOutCubic(double value)
    {
        value = Math.Max(0.0, Math.Min(1.0, value));
        double inverse = 1.0 - value;
        return 1.0 - inverse * inverse * inverse;
    }

    private int S(float value)
    {
        return Math.Max(1, (int)Math.Round(value * this.scale));
    }

    private struct GridMetrics
    {
        public Rectangle Content;
        public int Columns;
        public int Rows;
        public int TileWidth;
        public int TileHeight;
        public int ColumnGap;
        public int RowGap;
        public int ColumnStride;
        public int RowStride;
    }
}
