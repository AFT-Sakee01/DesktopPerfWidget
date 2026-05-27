using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using D2D = SharpDX.Direct2D1;
using DX = SharpDX;
using WIC = SharpDX.WIC;
using RawColor4 = SharpDX.Mathematics.Interop.RawColor4;
using RawRectangleF = SharpDX.Mathematics.Interop.RawRectangleF;
using RawVector2 = SharpDX.Mathematics.Interop.RawVector2;

internal static class Direct2DDockProgram
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (HasArg(args, "--stop"))
        {
            foreach (Process process in Process.GetProcessesByName("Direct2DDock"))
            {
                try
                {
                    if (process.Id != Process.GetCurrentProcess().Id)
                    {
                        process.Kill();
                    }
                }
                catch
                {
                }
            }

            return;
        }

        Direct2DNative.TrySetDpiAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new Direct2DDockForm(Direct2DDockSettings.Load()));
    }

    private static bool HasArg(string[] args, string value)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class Direct2DDockForm : Form
{
    private const double ResizeAnimationMs = 190.0;
    private const double IconAnimationMs = 180.0;
    private readonly Timer timer;
    private readonly List<DockRuntimeItem> items;
    private Direct2DDockSettings settings;
    private D2D.Factory factory;
    private WIC.ImagingFactory wicFactory;
    private WIC.Bitmap wicBitmap;
    private D2D.RenderTarget target;
    private D2D.SolidColorBrush shellBrush;
    private D2D.SolidColorBrush shellBorderBrush;
    private D2D.SolidColorBrush tileBrush;
    private D2D.SolidColorBrush tileHoverBrush;
    private D2D.SolidColorBrush indicatorBrush;
    private D2D.SolidColorBrush focusedIndicatorBrush;
    private D2D.SolidColorBrush glyphBrush;
    private DateTime lastRunningRefreshUtc;
    private string loadedRunningSignature;
    private int pinnedCount;
    private float scale;
    private Point lastCursor;
    private bool needsRender;
    private bool resizing;
    private bool startPendingEntriesAfterResize;
    private DateTime resizeStartedUtc;
    private Size resizeStartSize;
    private Size resizeTargetSize;

    private enum ItemAnimation
    {
        Normal,
        EnteringPending,
        Entering,
        Exiting
    }

    private sealed class DockRuntimeItem : IDisposable
    {
        public DockItem Item;
        public Bitmap GdiIcon;
        public D2D.Bitmap D2DIcon;
        public bool IsRunning;
        public bool IsFocused;
        public int InstanceCount;
        public IntPtr WindowHandle;
        public int ProcessId;
        public string ExecutablePath;
        public string WindowTitle;
        public string AnimationKey;
        public ItemAnimation Animation;
        public DateTime AnimationStartedUtc;

        public void Dispose()
        {
            if (this.D2DIcon != null)
            {
                this.D2DIcon.Dispose();
                this.D2DIcon = null;
            }

            if (this.GdiIcon != null)
            {
                this.GdiIcon.Dispose();
                this.GdiIcon = null;
            }
        }
    }

    private sealed class DockLayout
    {
        public RectangleF[] SystemRects;
        public RectangleF[] ItemRects;
        public RectangleF SeparatorRect;
    }

    public Direct2DDockForm(Direct2DDockSettings settings)
    {
        this.settings = settings;
        this.items = new List<DockRuntimeItem>();
        this.loadedRunningSignature = string.Empty;
        this.lastCursor = Point.Empty;
        this.needsRender = true;
        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.Black;
        this.TopMost = false;
        this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        this.scale = GetInitialScale();

        BuildItems(EnumerateRunningWindows());
        this.Size = GetDesiredSize();
        PositionDock(this.Size);

        this.timer = new Timer();
        this.timer.Interval = 16;
        this.timer.Tick += OnTimerTick;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= Direct2DNative.WS_EX_TOOLWINDOW | Direct2DNative.WS_EX_LAYERED;
            return parameters;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        EnsureRenderTarget();
        this.timer.Start();
        Render();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
        DisposeItems();
        DisposeRenderTarget();
        if (this.factory != null)
        {
            this.factory.Dispose();
            this.factory = null;
        }

        if (this.wicFactory != null)
        {
            this.wicFactory.Dispose();
            this.wicFactory = null;
        }

        base.OnFormClosed(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using (GraphicsPath regionPath = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), S(25)))
        {
            this.Region = new Region(regionPath);
        }

        DisposeRenderTarget();

        this.needsRender = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Render();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        this.needsRender = true;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        this.needsRender = true;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        int hoverIndex;
        DockLayout layout = CalculateLayout(new PointF(e.X, e.Y), true, out hoverIndex);
        for (int i = 0; i < layout.SystemRects.Length; i++)
        {
            if (layout.SystemRects[i].Contains(e.X, e.Y))
            {
                if (i == 0)
                {
                    Direct2DNative.ToggleDesktop();
                }
                else
                {
                    Direct2DNative.OpenStartMenu();
                }

                return;
            }
        }

        for (int i = 0; i < layout.ItemRects.Length && i < this.items.Count; i++)
        {
            if (layout.ItemRects[i].Contains(e.X, e.Y))
            {
                ActivateOrLaunch(this.items[i]);
                return;
            }
        }
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        bool changed = RefreshRunningIfNeeded();
        Point cursor = this.PointToClient(Cursor.Position);
        if (cursor != this.lastCursor)
        {
            this.lastCursor = cursor;
            changed = true;
        }

        bool animated = UpdateAnimations(DateTime.UtcNow);
        if (changed || animated || this.needsRender)
        {
            Render();
        }
    }

    private void EnsureRenderTarget()
    {
        if (this.target != null)
        {
            return;
        }

        if (this.factory == null)
        {
            this.factory = new D2D.Factory(D2D.FactoryType.SingleThreaded);
        }

        if (this.wicFactory == null)
        {
            this.wicFactory = new WIC.ImagingFactory();
        }

        int width = Math.Max(1, this.Width);
        int height = Math.Max(1, this.Height);
        this.wicBitmap = new WIC.Bitmap(
            this.wicFactory,
            width,
            height,
            WIC.PixelFormat.Format32bppPBGRA,
            WIC.BitmapCreateCacheOption.CacheOnLoad);

        D2D.RenderTargetProperties renderProperties = new D2D.RenderTargetProperties(
            D2D.RenderTargetType.Default,
            new D2D.PixelFormat(SharpDX.DXGI.Format.Unknown, D2D.AlphaMode.Premultiplied),
            0,
            0,
            D2D.RenderTargetUsage.None,
            D2D.FeatureLevel.Level_DEFAULT);
        this.target = new D2D.WicRenderTarget(this.factory, this.wicBitmap, renderProperties);
        CreateBrushes();
        RecreateDeviceIcons();
    }

    private void CreateBrushes()
    {
        this.shellBrush = new D2D.SolidColorBrush(this.target, Rgba(18, 19, 22, 0.84f));
        this.shellBorderBrush = new D2D.SolidColorBrush(this.target, Rgba(255, 255, 255, 0.32f));
        this.tileBrush = new D2D.SolidColorBrush(this.target, Rgba(248, 250, 255, 0.28f));
        this.tileHoverBrush = new D2D.SolidColorBrush(this.target, Rgba(82, 211, 255, 0.46f));
        this.indicatorBrush = new D2D.SolidColorBrush(this.target, Rgba(142, 149, 158, 0.82f));
        this.focusedIndicatorBrush = new D2D.SolidColorBrush(this.target, Rgba(82, 211, 255, 0.94f));
        this.glyphBrush = new D2D.SolidColorBrush(this.target, Rgba(244, 248, 250, 0.92f));
    }

    private void DisposeRenderTarget()
    {
        DisposeBrush(ref this.shellBrush);
        DisposeBrush(ref this.shellBorderBrush);
        DisposeBrush(ref this.tileBrush);
        DisposeBrush(ref this.tileHoverBrush);
        DisposeBrush(ref this.indicatorBrush);
        DisposeBrush(ref this.focusedIndicatorBrush);
        DisposeBrush(ref this.glyphBrush);
        DisposeDeviceIcons();
        if (this.target != null)
        {
            this.target.Dispose();
            this.target = null;
        }

        if (this.wicBitmap != null)
        {
            this.wicBitmap.Dispose();
            this.wicBitmap = null;
        }
    }

    private static void DisposeBrush(ref D2D.SolidColorBrush brush)
    {
        if (brush != null)
        {
            brush.Dispose();
            brush = null;
        }
    }

    private void Render()
    {
        if (!this.IsHandleCreated || this.Width <= 0 || this.Height <= 0)
        {
            return;
        }

        try
        {
            EnsureRenderTarget();
            this.target.BeginDraw();
            this.target.Clear(Rgba(0, 0, 0, 0.0f));
            DrawDock();
            this.target.EndDraw();
            SubmitLayeredWindow();
            this.needsRender = false;
        }
        catch (DX.SharpDXException)
        {
            DisposeRenderTarget();
            this.needsRender = true;
        }
    }

    private void SubmitLayeredWindow()
    {
        if (this.wicBitmap == null)
        {
            return;
        }

        using (Bitmap bitmap = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppPArgb))
        {
            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppPArgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                int bufferSize = stride * bitmap.Height;
                this.wicBitmap.CopyPixels(stride, data.Scan0, bufferSize);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            Direct2DNative.UpdateLayeredWindowFromBitmap(this.Handle, this.Location, bitmap, 255);
        }
    }

    private void DrawDock()
    {
        Point client = this.PointToClient(Cursor.Position);
        int hoverIndex;
        DockLayout layout = CalculateLayout(new PointF(client.X, client.Y), this.ClientRectangle.Contains(client), out hoverIndex);
        D2D.RoundedRectangle shell = Rounded(new RectangleF(0.5f, 0.5f, this.Width - 1.0f, this.Height - 1.0f), Math.Min(this.Height / 2.0f, S(25)));
        this.target.FillRoundedRectangle(shell, this.shellBrush);
        this.target.DrawRoundedRectangle(shell, this.shellBorderBrush, Math.Max(1.0f, this.scale));

        for (int i = 0; i < layout.SystemRects.Length; i++)
        {
            bool hovered = layout.SystemRects[i].Contains(client.X, client.Y);
            DrawTile(layout.SystemRects[i], hovered, 1.0f);
            if (i == 0)
            {
                DrawDesktopGlyph(layout.SystemRects[i]);
            }
            else
            {
                DrawStartGlyph(layout.SystemRects[i]);
            }
        }

        if (layout.SeparatorRect.Height > 0)
        {
            this.target.DrawLine(
                new RawVector2(layout.SeparatorRect.Left, layout.SeparatorRect.Top),
                new RawVector2(layout.SeparatorRect.Left, layout.SeparatorRect.Bottom),
                this.shellBorderBrush,
                Math.Max(1.0f, this.scale));
        }

        DateTime now = DateTime.UtcNow;
        for (int i = 0; i < this.items.Count; i++)
        {
            float alpha;
            float offsetY;
            if (!GetAnimationVisuals(this.items[i], layout.ItemRects[i], now, out alpha, out offsetY))
            {
                continue;
            }

            RectangleF rect = layout.ItemRects[i];
            rect.Y += offsetY;
            DrawRuntimeItem(this.items[i], rect, hoverIndex == i && this.items[i].Animation == ItemAnimation.Normal, alpha);
        }
    }

    private void DrawRuntimeItem(DockRuntimeItem item, RectangleF rect, bool hovered, float alpha)
    {
        DrawTile(rect, hovered, alpha);
        EnsureDeviceIcon(item);
        if (item.D2DIcon != null)
        {
            RectangleF iconRect = Inset(rect, Math.Max(2.0f, rect.Height * 0.14f));
            this.target.DrawBitmap(item.D2DIcon, Raw(iconRect), alpha, D2D.BitmapInterpolationMode.Linear);
        }

        if (item.IsRunning)
        {
            float height = Math.Max(S(3), rect.Width * 0.10f);
            float width = item.IsFocused ? Math.Max(S(12), rect.Width * 0.50f) : height;
            RectangleF indicator = new RectangleF(rect.Left + (rect.Width - width) / 2.0f, this.Height - S(5), width, height);
            D2D.RoundedRectangle rounded = Rounded(indicator, height / 2.0f);
            D2D.SolidColorBrush brush = item.IsFocused ? this.focusedIndicatorBrush : this.indicatorBrush;
            brush.Opacity = alpha;
            this.target.FillRoundedRectangle(rounded, brush);
            brush.Opacity = 1.0f;
        }
    }

    private void DrawTile(RectangleF rect, bool hovered, float alpha)
    {
        D2D.SolidColorBrush brush = hovered ? this.tileHoverBrush : this.tileBrush;
        brush.Opacity = alpha;
        D2D.RoundedRectangle rounded = Rounded(rect, Math.Max(S(5), rect.Height * 0.25f));
        this.target.FillRoundedRectangle(rounded, brush);
        brush.Opacity = 1.0f;
    }

    private void DrawDesktopGlyph(RectangleF rect)
    {
        RectangleF icon = Inset(rect, rect.Height * 0.24f);
        RectangleF monitor = new RectangleF(icon.Left + icon.Width * 0.12f, icon.Top + icon.Height * 0.16f, icon.Width * 0.76f, icon.Height * 0.50f);
        this.target.DrawRectangle(Raw(monitor), this.glyphBrush, Math.Max(1.5f, icon.Width * 0.07f));
        float centerX = monitor.Left + monitor.Width / 2.0f;
        this.target.DrawLine(new RawVector2(centerX, monitor.Bottom), new RawVector2(centerX, icon.Bottom), this.glyphBrush, Math.Max(1.5f, icon.Width * 0.07f));
        this.target.DrawLine(new RawVector2(icon.Left + icon.Width * 0.30f, icon.Bottom), new RawVector2(icon.Left + icon.Width * 0.70f, icon.Bottom), this.glyphBrush, Math.Max(1.5f, icon.Width * 0.07f));
    }

    private void DrawStartGlyph(RectangleF rect)
    {
        RectangleF icon = Inset(rect, rect.Height * 0.23f);
        float gap = Math.Max(2.0f, icon.Width * 0.08f);
        float paneW = (icon.Width - gap) / 2.0f;
        float paneH = (icon.Height - gap) / 2.0f;
        this.target.FillRectangle(Raw(new RectangleF(icon.Left, icon.Top, paneW, paneH)), this.glyphBrush);
        this.target.FillRectangle(Raw(new RectangleF(icon.Left + paneW + gap, icon.Top, paneW, paneH)), this.glyphBrush);
        this.target.FillRectangle(Raw(new RectangleF(icon.Left, icon.Top + paneH + gap, paneW, paneH)), this.glyphBrush);
        this.target.FillRectangle(Raw(new RectangleF(icon.Left + paneW + gap, icon.Top + paneH + gap, paneW, paneH)), this.glyphBrush);
    }

    private DockLayout CalculateLayout(PointF cursor, bool hasCursor, out int hoverIndex)
    {
        hoverIndex = -1;
        int count = this.items.Count;
        DockLayout layout = new DockLayout();
        layout.SystemRects = new RectangleF[2];
        layout.ItemRects = new RectangleF[count];
        float pad = S(6);
        float gap = S(8);
        float systemGap = count > 0 ? S(10) : 0;
        float separatorSpace = this.pinnedCount > 0 && count > this.pinnedCount ? S(14) : 0;
        float baseIcon = GetBaseIconSize();
        float maxIcon = GetMaxIconSize();
        float systemWidth = baseIcon * 2 + gap;
        float pinnedGaps = Math.Max(0, this.pinnedCount - 1) * gap;
        float runningGaps = Math.Max(0, count - this.pinnedCount - 1) * gap;
        float total = pad * 2 + systemWidth + systemGap + count * maxIcon + pinnedGaps + runningGaps + separatorSpace;
        float x = Math.Max(pad, (this.Width - total) / 2.0f);
        float centerY = this.Height / 2.0f;

        for (int i = 0; i < layout.SystemRects.Length; i++)
        {
            layout.SystemRects[i] = new RectangleF(x, centerY - baseIcon / 2.0f, baseIcon, baseIcon);
            x += baseIcon + (i < layout.SystemRects.Length - 1 ? gap : 0);
        }

        x += systemGap;
        double bestProgress = 0.0;
        for (int i = 0; i < this.pinnedCount && i < count; i++)
        {
            layout.ItemRects[i] = CalculateItemRect(i, x, maxIcon, baseIcon, centerY, cursor, hasCursor, ref hoverIndex, ref bestProgress);
            x += maxIcon + (i < this.pinnedCount - 1 ? gap : 0);
        }

        if (separatorSpace > 0)
        {
            float lineX = x + separatorSpace / 2.0f;
            layout.SeparatorRect = new RectangleF(lineX, S(8), Math.Max(1.0f, this.scale), this.Height - S(16));
            x += separatorSpace;
        }

        for (int i = this.pinnedCount; i < count; i++)
        {
            layout.ItemRects[i] = CalculateItemRect(i, x, maxIcon, baseIcon, centerY, cursor, hasCursor, ref hoverIndex, ref bestProgress);
            x += maxIcon + (i < count - 1 ? gap : 0);
        }

        if (bestProgress <= 0.05)
        {
            hoverIndex = -1;
        }

        return layout;
    }

    private RectangleF CalculateItemRect(int index, float slotLeft, float maxIcon, float baseIcon, float centerY, PointF cursor, bool hasCursor, ref int hoverIndex, ref double bestProgress)
    {
        float centerX = slotLeft + maxIcon / 2.0f;
        double progress = 0.0;
        if (hasCursor)
        {
            double influence = Math.Max(baseIcon * 1.8f, S(48));
            double distance = Math.Abs(cursor.X - centerX);
            if (distance < influence)
            {
                progress = Math.Sin((1.0 - distance / influence) * Math.PI / 2.0);
            }
        }

        if (progress > bestProgress)
        {
            bestProgress = progress;
            hoverIndex = index;
        }

        double maxFactor = baseIcon <= 0 ? 1.0 : Math.Max(1.0, maxIcon / baseIcon);
        float size = (float)Math.Min(maxIcon, baseIcon * (1.0 + (maxFactor - 1.0) * progress));
        return new RectangleF(centerX - size / 2.0f, centerY - size / 2.0f, size, size);
    }

    private Size GetDesiredSize()
    {
        int count = this.items.Count;
        int baseIcon = (int)Math.Round(GetBaseIconSize());
        int maxIcon = (int)Math.Round(GetMaxIconSize());
        int gap = S(8);
        int pad = S(6);
        int system = baseIcon * 2 + gap;
        int separator = this.pinnedCount > 0 && count > this.pinnedCount ? S(14) : 0;
        int itemGaps = Math.Max(0, this.pinnedCount - 1) * gap + Math.Max(0, count - this.pinnedCount - 1) * gap;
        int width = pad * 2 + system + S(10) + count * maxIcon + itemGaps + separator;
        width = Math.Min(width, GetPhysicalWorkArea().Width - S(16));
        int height = Math.Max(maxIcon + pad * 2, baseIcon + pad * 2);
        return new Size(Math.Max(S(150), width), Math.Max(S(34), height));
    }

    private float GetBaseIconSize()
    {
        return Math.Max(S(24), this.settings.DockIconSize);
    }

    private float GetMaxIconSize()
    {
        return Math.Max(S(28), (float)Math.Round(this.settings.DockIconSize * Math.Max(1.0, this.settings.DockMagnificationPercent / 100.0)));
    }

    private bool RefreshRunningIfNeeded()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - this.lastRunningRefreshUtc).TotalSeconds < 2.0)
        {
            return false;
        }

        List<WindowInfo> running = EnumerateRunningWindows();
        string signature = BuildRunningSignature(running);
        this.lastRunningRefreshUtc = now;
        if (string.Equals(signature, this.loadedRunningSignature, StringComparison.Ordinal))
        {
            return false;
        }

        int newPinnedCount;
        List<DockRuntimeItem> rebuilt = BuildRuntimeItems(running, out newPinnedCount);
        ApplyAnimatedItems(rebuilt, newPinnedCount, signature);
        return true;
    }

    private void BuildItems(List<WindowInfo> running)
    {
        int newPinnedCount;
        List<DockRuntimeItem> rebuilt = BuildRuntimeItems(running, out newPinnedCount);
        DisposeItems();
        this.items.AddRange(rebuilt);
        this.pinnedCount = newPinnedCount;
        this.loadedRunningSignature = BuildRunningSignature(running);
        this.lastRunningRefreshUtc = DateTime.UtcNow;
        UpdateFocusedItems(Direct2DNative.GetForegroundWindowHandle());
    }

    private List<DockRuntimeItem> BuildRuntimeItems(List<WindowInfo> running, out int newPinnedCount)
    {
        List<DockRuntimeItem> result = new List<DockRuntimeItem>();
        List<DockItem> pinned = DockItem.ParseItems(this.settings.DockItemsText);
        newPinnedCount = Math.Min(pinned.Count, 24);
        for (int i = 0; i < newPinnedCount; i++)
        {
            DockItem item = pinned[i];
            string executable = ResolveExecutablePath(item.Command);
            result.Add(new DockRuntimeItem
            {
                Item = item,
                GdiIcon = LoadIconBitmap(executable, item.Label),
                IsRunning = false,
                InstanceCount = 0,
                ExecutablePath = executable,
                AnimationKey = "P|" + i.ToString(CultureInfo.InvariantCulture) + "|" + item.Command.ToUpperInvariant(),
                Animation = ItemAnimation.Normal
            });
        }

        int runningCount = 0;
        for (int i = 0; i < running.Count && runningCount < 24; i++)
        {
            WindowInfo window = running[i];
            string executable = GetProcessExecutablePath(window.ProcessId);
            if (TryMarkPinned(result, newPinnedCount, executable, window))
            {
                continue;
            }

            if (TryIncrementRunning(result, newPinnedCount, executable, window))
            {
                continue;
            }

            string label = !string.IsNullOrEmpty(executable) ? Path.GetFileNameWithoutExtension(executable) : window.Title;
            DockRuntimeItem runtime = new DockRuntimeItem
            {
                Item = new DockItem(label, executable),
                GdiIcon = LoadIconBitmap(executable, label),
                IsRunning = true,
                InstanceCount = 1,
                WindowHandle = window.Handle,
                ProcessId = window.ProcessId,
                ExecutablePath = executable,
                WindowTitle = window.Title,
                AnimationKey = BuildRunningKey(executable, window),
                Animation = ItemAnimation.Normal
            };
            result.Add(runtime);
            runningCount++;
        }

        return result;
    }

    private bool TryMarkPinned(List<DockRuntimeItem> result, int pinnedLimit, string executable, WindowInfo window)
    {
        if (string.IsNullOrEmpty(executable))
        {
            return false;
        }

        for (int i = 0; i < pinnedLimit && i < result.Count; i++)
        {
            DockRuntimeItem item = result[i];
            if (!string.IsNullOrEmpty(item.ExecutablePath) &&
                string.Equals(item.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase))
            {
                item.IsRunning = true;
                item.InstanceCount++;
                if (item.WindowHandle == IntPtr.Zero)
                {
                    item.WindowHandle = window.Handle;
                    item.ProcessId = window.ProcessId;
                    item.WindowTitle = window.Title;
                }

                return true;
            }
        }

        return false;
    }

    private bool TryIncrementRunning(List<DockRuntimeItem> result, int pinnedLimit, string executable, WindowInfo window)
    {
        for (int i = pinnedLimit; i < result.Count; i++)
        {
            DockRuntimeItem item = result[i];
            if (!string.IsNullOrEmpty(executable) &&
                !string.IsNullOrEmpty(item.ExecutablePath) &&
                string.Equals(item.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase))
            {
                item.InstanceCount++;
                return true;
            }
        }

        return false;
    }

    private void ApplyAnimatedItems(List<DockRuntimeItem> rebuilt, int newPinnedCount, string signature)
    {
        DateTime now = DateTime.UtcNow;
        Dictionary<string, DockRuntimeItem> rebuiltByKey = new Dictionary<string, DockRuntimeItem>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rebuilt.Count; i++)
        {
            if (!string.IsNullOrEmpty(rebuilt[i].AnimationKey) && !rebuiltByKey.ContainsKey(rebuilt[i].AnimationKey))
            {
                rebuiltByKey.Add(rebuilt[i].AnimationKey, rebuilt[i]);
            }
        }

        HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<DockRuntimeItem> merged = new List<DockRuntimeItem>();
        bool hasEntering = false;

        for (int i = 0; i < this.items.Count; i++)
        {
            DockRuntimeItem oldItem = this.items[i];
            DockRuntimeItem next;
            if (!string.IsNullOrEmpty(oldItem.AnimationKey) &&
                rebuiltByKey.TryGetValue(oldItem.AnimationKey, out next) &&
                !used.Contains(oldItem.AnimationKey))
            {
                TransferIcon(oldItem, next);
                next.Animation = oldItem.Animation == ItemAnimation.Entering || oldItem.Animation == ItemAnimation.EnteringPending ? oldItem.Animation : ItemAnimation.Normal;
                next.AnimationStartedUtc = oldItem.AnimationStartedUtc;
                merged.Add(next);
                used.Add(oldItem.AnimationKey);
                continue;
            }

            if (i >= this.pinnedCount && oldItem.IsRunning)
            {
                if (oldItem.Animation != ItemAnimation.Exiting)
                {
                    oldItem.Animation = ItemAnimation.Exiting;
                    oldItem.AnimationStartedUtc = now;
                }

                merged.Add(oldItem);
            }
            else
            {
                oldItem.Dispose();
            }
        }

        for (int i = 0; i < rebuilt.Count; i++)
        {
            DockRuntimeItem item = rebuilt[i];
            if (!string.IsNullOrEmpty(item.AnimationKey) && used.Contains(item.AnimationKey))
            {
                continue;
            }

            item.Animation = i >= newPinnedCount ? ItemAnimation.EnteringPending : ItemAnimation.Normal;
            item.AnimationStartedUtc = DateTime.MinValue;
            if (item.Animation == ItemAnimation.EnteringPending)
            {
                hasEntering = true;
            }

            merged.Add(item);
        }

        this.items.Clear();
        this.items.AddRange(merged);
        this.pinnedCount = newPinnedCount;
        this.loadedRunningSignature = signature;
        UpdateFocusedItems(Direct2DNative.GetForegroundWindowHandle());
        Size targetSize = GetDesiredSize();
        if (targetSize != this.Size)
        {
            BeginResize(targetSize, hasEntering);
        }
        else if (hasEntering)
        {
            StartPendingEntries(now);
        }

        this.needsRender = true;
    }

    private static void TransferIcon(DockRuntimeItem source, DockRuntimeItem target)
    {
        if (source.GdiIcon != null)
        {
            if (target.GdiIcon != null && !object.ReferenceEquals(target.GdiIcon, source.GdiIcon))
            {
                target.GdiIcon.Dispose();
            }

            target.GdiIcon = source.GdiIcon;
            source.GdiIcon = null;
        }

        if (source.D2DIcon != null)
        {
            if (target.D2DIcon != null && !object.ReferenceEquals(target.D2DIcon, source.D2DIcon))
            {
                target.D2DIcon.Dispose();
            }

            target.D2DIcon = source.D2DIcon;
            source.D2DIcon = null;
        }
    }

    private bool UpdateAnimations(DateTime now)
    {
        bool changed = false;
        if (this.resizing)
        {
            double progress = Math.Max(0.0, Math.Min(1.0, (now - this.resizeStartedUtc).TotalMilliseconds / ResizeAnimationMs));
            double eased = EaseOutCubic(progress);
            Size next = new Size(
                (int)Math.Round(this.resizeStartSize.Width + (this.resizeTargetSize.Width - this.resizeStartSize.Width) * eased),
                (int)Math.Round(this.resizeStartSize.Height + (this.resizeTargetSize.Height - this.resizeStartSize.Height) * eased));
            PositionDock(next);
            changed = true;
            if (progress >= 1.0)
            {
                this.resizing = false;
                PositionDock(this.resizeTargetSize);
                if (this.startPendingEntriesAfterResize)
                {
                    this.startPendingEntriesAfterResize = false;
                    StartPendingEntries(now);
                }
            }
        }

        bool removed = false;
        for (int i = this.items.Count - 1; i >= 0; i--)
        {
            DockRuntimeItem item = this.items[i];
            if (item.Animation == ItemAnimation.Entering)
            {
                changed = true;
                if ((now - item.AnimationStartedUtc).TotalMilliseconds >= IconAnimationMs)
                {
                    item.Animation = ItemAnimation.Normal;
                }
            }
            else if (item.Animation == ItemAnimation.Exiting)
            {
                changed = true;
                if ((now - item.AnimationStartedUtc).TotalMilliseconds >= IconAnimationMs)
                {
                    item.Dispose();
                    this.items.RemoveAt(i);
                    removed = true;
                }
            }
            else if (item.Animation == ItemAnimation.EnteringPending)
            {
                changed = true;
            }
        }

        if (removed)
        {
            Size targetSize = GetDesiredSize();
            if (targetSize != this.Size)
            {
                BeginResize(targetSize, false);
            }
        }

        return changed;
    }

    private void BeginResize(Size targetSize, bool startEntriesAfterResize)
    {
        this.resizeStartSize = this.Size;
        this.resizeTargetSize = targetSize;
        this.resizeStartedUtc = DateTime.UtcNow;
        this.resizing = true;
        this.startPendingEntriesAfterResize = startEntriesAfterResize;
    }

    private void StartPendingEntries(DateTime now)
    {
        for (int i = 0; i < this.items.Count; i++)
        {
            if (this.items[i].Animation == ItemAnimation.EnteringPending)
            {
                this.items[i].Animation = ItemAnimation.Entering;
                this.items[i].AnimationStartedUtc = now;
            }
        }
    }

    private bool GetAnimationVisuals(DockRuntimeItem item, RectangleF rect, DateTime now, out float alpha, out float offsetY)
    {
        alpha = 1.0f;
        offsetY = 0.0f;
        if (item.Animation == ItemAnimation.EnteringPending)
        {
            return false;
        }

        if (item.Animation == ItemAnimation.Entering)
        {
            double progress = Math.Max(0.0, Math.Min(1.0, (now - item.AnimationStartedUtc).TotalMilliseconds / IconAnimationMs));
            double eased = EaseOutCubic(progress);
            alpha = (float)eased;
            offsetY = (float)(rect.Height * 0.62f * (1.0 - eased));
        }
        else if (item.Animation == ItemAnimation.Exiting)
        {
            double progress = Math.Max(0.0, Math.Min(1.0, (now - item.AnimationStartedUtc).TotalMilliseconds / IconAnimationMs));
            double eased = progress * progress * progress;
            alpha = (float)(1.0 - eased);
            offsetY = (float)(rect.Height * 0.62f * eased);
        }

        return alpha > 0.01f;
    }

    private void ActivateOrLaunch(DockRuntimeItem item)
    {
        if (item == null)
        {
            return;
        }

        if (item.IsRunning && item.WindowHandle != IntPtr.Zero && Direct2DNative.ActivateWindow(item.WindowHandle))
        {
            return;
        }

        if (item.Item != null && !string.IsNullOrEmpty(item.Item.Command))
        {
            string file;
            string args;
            SplitCommandLine(item.Item.Command, out file, out args);
            if (!string.IsNullOrEmpty(file))
            {
                ProcessStartInfo info = new ProcessStartInfo();
                info.FileName = file;
                info.Arguments = args;
                info.UseShellExecute = true;
                Process.Start(info);
            }
        }
    }

    private void UpdateFocusedItems(IntPtr foreground)
    {
        int foregroundProcess;
        Direct2DNative.TryGetWindowProcessId(foreground, out foregroundProcess);
        for (int i = 0; i < this.items.Count; i++)
        {
            DockRuntimeItem item = this.items[i];
            item.IsFocused = item.IsRunning &&
                foreground != IntPtr.Zero &&
                (item.WindowHandle == foreground || (foregroundProcess > 0 && item.ProcessId == foregroundProcess));
        }
    }

    private void PositionDock(Size size)
    {
        Rectangle work = GetPhysicalWorkArea();
        int left = work.Left + (work.Width - size.Width) / 2;
        int top = work.Bottom - size.Height - this.settings.DockBottomMargin;
        if (this.Size != size)
        {
            this.Size = size;
        }

        this.Location = new Point(left, top);
    }

    private Rectangle GetPhysicalWorkArea()
    {
        Rectangle bounds = Screen.PrimaryScreen.Bounds;
        Rectangle work = Screen.PrimaryScreen.WorkingArea;
        Size physical = Direct2DNative.GetPrimaryScreenSize();
        if (physical.Width <= 0 || physical.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return work;
        }

        if (bounds.Width == physical.Width && bounds.Height == physical.Height)
        {
            return work;
        }

        float scaleX = physical.Width / (float)bounds.Width;
        float scaleY = physical.Height / (float)bounds.Height;
        return new Rectangle(
            (int)Math.Round(work.Left * scaleX),
            (int)Math.Round(work.Top * scaleY),
            (int)Math.Round(work.Width * scaleX),
            (int)Math.Round(work.Height * scaleY));
    }

    private void RecreateDeviceIcons()
    {
        for (int i = 0; i < this.items.Count; i++)
        {
            EnsureDeviceIcon(this.items[i]);
        }
    }

    private void EnsureDeviceIcon(DockRuntimeItem item)
    {
        if (item == null || item.D2DIcon != null || item.GdiIcon == null || this.target == null)
        {
            return;
        }

        item.D2DIcon = CreateD2DBitmap(item.GdiIcon);
    }

    private D2D.Bitmap CreateD2DBitmap(Bitmap source)
    {
        using (Bitmap copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb))
        {
            using (Graphics g = Graphics.FromImage(copy))
            {
                g.Clear(Color.Transparent);
                g.DrawImage(source, 0, 0, source.Width, source.Height);
            }

            Rectangle rect = new Rectangle(0, 0, copy.Width, copy.Height);
            BitmapData data = copy.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                int byteCount = stride * copy.Height;
                byte[] bytes = new byte[byteCount];
                Marshal.Copy(data.Scan0, bytes, 0, byteCount);
                using (DX.DataStream stream = new DX.DataStream(byteCount, true, true))
                {
                    stream.Write(bytes, 0, byteCount);
                    stream.Position = 0;
                    D2D.BitmapProperties properties = new D2D.BitmapProperties(
                        new D2D.PixelFormat(SharpDX.DXGI.Format.B8G8R8A8_UNorm, D2D.AlphaMode.Premultiplied));
                    return new D2D.Bitmap(this.target, new DX.Size2(copy.Width, copy.Height), new DX.DataPointer(stream.DataPointer, byteCount), data.Stride, properties);
                }
            }
            finally
            {
                copy.UnlockBits(data);
            }
        }
    }

    private void DisposeDeviceIcons()
    {
        for (int i = 0; i < this.items.Count; i++)
        {
            if (this.items[i].D2DIcon != null)
            {
                this.items[i].D2DIcon.Dispose();
                this.items[i].D2DIcon = null;
            }
        }
    }

    private void DisposeItems()
    {
        for (int i = 0; i < this.items.Count; i++)
        {
            this.items[i].Dispose();
        }

        this.items.Clear();
    }

    private List<WindowInfo> EnumerateRunningWindows()
    {
        return Direct2DNative.EnumerateApplicationWindows(this.Handle);
    }

    private static string BuildRunningSignature(List<WindowInfo> windows)
    {
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < windows.Count; i++)
        {
            builder.Append(windows[i].ProcessId);
            builder.Append(':');
            builder.Append(windows[i].Handle.ToInt64().ToString("X", CultureInfo.InvariantCulture));
            builder.Append(';');
        }

        return builder.ToString();
    }

    private static string BuildRunningKey(string executable, WindowInfo window)
    {
        if (!string.IsNullOrEmpty(executable))
        {
            return "R|" + executable.ToUpperInvariant();
        }

        return "R|" + window.Handle.ToInt64().ToString("X", CultureInfo.InvariantCulture);
    }

    private static Bitmap LoadIconBitmap(string executable, string label)
    {
        if (!string.IsNullOrEmpty(executable) && File.Exists(executable))
        {
            try
            {
                using (Icon icon = Icon.ExtractAssociatedIcon(executable))
                {
                    if (icon != null)
                    {
                        return IconToBitmap(icon);
                    }
                }
            }
            catch
            {
            }
        }

        return CreateFallbackIcon(label);
    }

    private static Bitmap IconToBitmap(Icon icon)
    {
        using (Bitmap source = icon.ToBitmap())
        {
            Bitmap bitmap = new Bitmap(128, 128, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawImage(source, new Rectangle(2, 2, 124, 124));
            }

            return bitmap;
        }
    }

    private static Bitmap CreateFallbackIcon(string label)
    {
        Bitmap bitmap = new Bitmap(128, 128, PixelFormat.Format32bppPArgb);
        string glyph = string.IsNullOrEmpty(label) ? "A" : label.Substring(0, 1).ToUpperInvariant();
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF rect = new RectangleF(10, 10, 108, 108);
            using (GraphicsPath path = RoundedRectangle(rect, 26))
            using (LinearGradientBrush brush = new LinearGradientBrush(rect, Color.FromArgb(76, 191, 255), Color.FromArgb(122, 236, 177), LinearGradientMode.ForwardDiagonal))
            {
                g.FillPath(brush, path);
            }

            using (Font font = new Font("Segoe UI", 46.0f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(245, 250, 252)))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(glyph, font, textBrush, rect, format);
            }
        }

        return bitmap;
    }

    private static string GetProcessExecutablePath(int processId)
    {
        if (processId <= 0)
        {
            return string.Empty;
        }

        try
        {
            using (Process process = Process.GetProcessById(processId))
            {
                if (process.MainModule != null)
                {
                    return process.MainModule.FileName;
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ResolveExecutablePath(string command)
    {
        string file;
        string args;
        SplitCommandLine(command, out file, out args);
        if (string.IsNullOrEmpty(file) || file.IndexOf(':') == file.Length - 1)
        {
            return string.Empty;
        }

        string expanded = Environment.ExpandEnvironmentVariables(file).Trim().Trim('"');
        if (File.Exists(expanded))
        {
            return expanded;
        }

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string[] parts = path.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string candidate = Path.Combine(parts[i].Trim(), expanded);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return expanded;
    }

    private static void SplitCommandLine(string command, out string file, out string arguments)
    {
        file = string.Empty;
        arguments = string.Empty;
        if (string.IsNullOrEmpty(command))
        {
            return;
        }

        string value = Environment.ExpandEnvironmentVariables(command).Trim();
        if (value.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = value.IndexOf('"', 1);
            if (end > 1)
            {
                file = value.Substring(1, end - 1);
                arguments = value.Substring(end + 1).Trim();
                return;
            }
        }

        int split = value.IndexOf(' ');
        if (split > 0)
        {
            file = value.Substring(0, split);
            arguments = value.Substring(split + 1).Trim();
        }
        else
        {
            file = value;
        }
    }

    private static RawColor4 Rgba(int r, int g, int b, float a)
    {
        return new RawColor4(r / 255.0f, g / 255.0f, b / 255.0f, a);
    }

    private static RawRectangleF Raw(RectangleF rect)
    {
        return new RawRectangleF(rect.Left, rect.Top, rect.Right, rect.Bottom);
    }

    private static D2D.RoundedRectangle Rounded(RectangleF rect, float radius)
    {
        return new D2D.RoundedRectangle
        {
            Rect = Raw(rect),
            RadiusX = radius,
            RadiusY = radius
        };
    }

    private static RectangleF Inset(RectangleF rect, float inset)
    {
        return new RectangleF(rect.Left + inset, rect.Top + inset, Math.Max(1.0f, rect.Width - inset * 2.0f), Math.Max(1.0f, rect.Height - inset * 2.0f));
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

    private float GetInitialScale()
    {
        float dpiScale = 1.0f;
        try
        {
            using (Graphics g = this.CreateGraphics())
            {
                dpiScale = Math.Max(1.0f, g.DpiX / 96.0f);
            }
        }
        catch
        {
        }

        Rectangle bounds = Screen.PrimaryScreen.Bounds;
        Size physical = Direct2DNative.GetPrimaryScreenSize();
        if (physical.Width <= 0 || physical.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return dpiScale;
        }

        float screenScale = Math.Max(physical.Width / (float)bounds.Width, physical.Height / (float)bounds.Height);
        return Math.Max(dpiScale, Math.Max(1.0f, screenScale));
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
}

internal sealed class Direct2DDockSettings
{
    public int DockIconSize = 96;
    public int DockMagnificationPercent = 120;
    public int DockBottomMargin = 6;
    public string DockItemsText = GetDefaultDockItemsText();

    public static Direct2DDockSettings Load()
    {
        Direct2DDockSettings settings = new Direct2DDockSettings();
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopPerfWidget", "settings.ini");
        if (!File.Exists(path))
        {
            return settings;
        }

        try
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                int split = line.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, split).Trim();
                string value = line.Substring(split + 1).Trim();
                int intValue;
                if (string.Equals(key, "DockIconSize", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
                {
                    settings.DockIconSize = Math.Max(24, Math.Min(288, intValue));
                }
                else if (string.Equals(key, "DockMagnificationPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
                {
                    settings.DockMagnificationPercent = Math.Max(100, Math.Min(145, intValue));
                }
                else if (string.Equals(key, "DockBottomMargin", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out intValue))
                {
                    settings.DockBottomMargin = Math.Max(0, Math.Min(240, intValue));
                }
                else if (string.Equals(key, "DockItemsText", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(key, "DockItems", StringComparison.OrdinalIgnoreCase))
                {
                    settings.DockItemsText = DecodeSettingText(value);
                }
            }
        }
        catch
        {
        }

        return settings;
    }

    private static string GetDefaultDockItemsText()
    {
        return
            "资源管理器|%WINDIR%\\explorer.exe\r\n" +
            "设置|ms-settings:\r\n" +
            "记事本|%WINDIR%\\System32\\notepad.exe\r\n" +
            "任务管理器|%WINDIR%\\System32\\Taskmgr.exe";
    }

    private static string DecodeSettingText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(value.Substring("base64:".Length));
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        return value.Replace("\\r\\n", "\r\n").Replace("\\n", "\r\n");
    }
}

internal sealed class DockItem
{
    public DockItem(string label, string command)
    {
        this.Label = label ?? string.Empty;
        this.Command = command ?? string.Empty;
    }

    public string Label { get; private set; }
    public string Command { get; private set; }

    public static List<DockItem> ParseItems(string text)
    {
        List<DockItem> items = new List<DockItem>();
        if (string.IsNullOrEmpty(text))
        {
            return items;
        }

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split(new char[] { '\n' }, StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            int split = line.IndexOf('|');
            string label;
            string command;
            if (split >= 0)
            {
                label = line.Substring(0, split).Trim();
                command = line.Substring(split + 1).Trim();
            }
            else
            {
                command = line;
                label = Path.GetFileNameWithoutExtension(command);
            }

            if (command.Length > 0)
            {
                items.Add(new DockItem(label.Length == 0 ? "App" : label, command));
            }
        }

        return items;
    }
}

internal sealed class WindowInfo
{
    public IntPtr Handle;
    public int ProcessId;
    public string Title;
}

internal static class Direct2DNative
{
    public static readonly IntPtr HWND_TOP = new IntPtr(0);
    private const int GWL_EXSTYLE = -20;
    private const int GW_OWNER = 4;
    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    private const byte VK_LWIN = 0x5B;
    private const byte VK_D = 0x44;
    private const int KEYEVENTF_KEYUP = 0x0002;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int ULW_ALPHA = 0x00000002;

    public static void TrySetDpiAware()
    {
        try
        {
            SetProcessDPIAware();
        }
        catch
        {
        }
    }

    public static List<WindowInfo> EnumerateApplicationWindows(IntPtr ownHandle)
    {
        List<WindowInfo> windows = new List<WindowInfo>();
        int ownProcessId = Process.GetCurrentProcess().Id;
        EnumWindows(delegate(IntPtr handle, IntPtr lParam)
        {
            if (handle == IntPtr.Zero || handle == ownHandle || !IsWindowVisible(handle) || GetWindow(handle, GW_OWNER) != IntPtr.Zero)
            {
                return true;
            }

            int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
            {
                return true;
            }

            uint processIdValue;
            GetWindowThreadProcessId(handle, out processIdValue);
            int processId = processIdValue > int.MaxValue ? 0 : (int)processIdValue;
            if (processId <= 0 || processId == ownProcessId || IsUtilityProcess(processId))
            {
                return true;
            }

            RECT rect;
            if (!GetWindowRect(handle, out rect) || rect.Right - rect.Left < 32 || rect.Bottom - rect.Top < 32)
            {
                return true;
            }

            string title = GetWindowTitle(handle);
            if (string.IsNullOrEmpty(title))
            {
                return true;
            }

            windows.Add(new WindowInfo { Handle = handle, ProcessId = processId, Title = title });
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public static IntPtr GetForegroundWindowHandle()
    {
        try
        {
            return GetForegroundWindow();
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static bool TryGetWindowProcessId(IntPtr handle, out int processId)
    {
        processId = 0;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        uint value;
        GetWindowThreadProcessId(handle, out value);
        if (value == 0 || value > int.MaxValue)
        {
            return false;
        }

        processId = (int)value;
        return true;
    }

    public static bool ActivateWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (IsIconic(handle))
            {
                ShowWindow(handle, SW_RESTORE);
            }
            else
            {
                ShowWindow(handle, SW_SHOW);
            }

            return SetForegroundWindow(handle);
        }
        catch
        {
            return false;
        }
    }

    public static void ToggleDesktop()
    {
        SendWinKeyChord(VK_D);
    }

    public static void OpenStartMenu()
    {
        try
        {
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch
        {
        }
    }

    public static Size GetPrimaryScreenSize()
    {
        try
        {
            return new Size(GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
        }
        catch
        {
            return Size.Empty;
        }
    }

    public static bool UpdateLayeredWindowFromBitmap(IntPtr handle, Point location, Bitmap bitmap, byte sourceAlpha)
    {
        IntPtr screenDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmapHandle = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return false;
            }

            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero)
            {
                return false;
            }

            bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memoryDc, bitmapHandle);

            POINT destination = new POINT(location.X, location.Y);
            SIZE size = new SIZE(bitmap.Width, bitmap.Height);
            POINT source = new POINT(0, 0);
            BLENDFUNCTION blend = new BLENDFUNCTION();
            blend.BlendOp = AC_SRC_OVER;
            blend.BlendFlags = 0;
            blend.SourceConstantAlpha = sourceAlpha;
            blend.AlphaFormat = AC_SRC_ALPHA;

            return UpdateLayeredWindow(
                handle,
                screenDc,
                ref destination,
                ref size,
                memoryDc,
                ref source,
                0,
                ref blend,
                ULW_ALPHA);
        }
        finally
        {
            if (memoryDc != IntPtr.Zero && oldBitmap != IntPtr.Zero)
            {
                SelectObject(memoryDc, oldBitmap);
            }

            if (bitmapHandle != IntPtr.Zero)
            {
                DeleteObject(bitmapHandle);
            }

            if (memoryDc != IntPtr.Zero)
            {
                DeleteDC(memoryDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    private static void SendWinKeyChord(byte key)
    {
        try
        {
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch
        {
        }
    }

    private static bool IsUtilityProcess(int processId)
    {
        try
        {
            using (Process process = Process.GetProcessById(processId))
            {
                string name = process.ProcessName ?? string.Empty;
                return string.Equals(name, "TextInputHost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "SearchHost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "Widgets", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            return false;
        }
    }

    private static string GetWindowTitle(IntPtr handle)
    {
        int length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder(length + 1);
        int copied = GetWindowText(handle, builder, builder.Capacity);
        return copied <= 0 ? string.Empty : builder.ToString().Trim();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr handle, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr handle,
        IntPtr destinationDc,
        ref POINT destination,
        ref SIZE size,
        IntPtr sourceDc,
        ref POINT source,
        int colorKey,
        ref BLENDFUNCTION blend,
        int flags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, int flags, UIntPtr extraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;

        public POINT(int x, int y)
        {
            this.X = x;
            this.Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int CX;
        public int CY;

        public SIZE(int cx, int cy)
        {
            this.CX = cx;
            this.CY = cy;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }
}
